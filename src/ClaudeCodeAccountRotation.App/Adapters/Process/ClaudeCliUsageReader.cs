using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Ports;
using ClaudeCodeAccountRotation.Core.Quota;

namespace ClaudeCodeAccountRotation.App.Adapters.Process;

/// <summary>
/// Reads the live account's usage through the unmodified CLI: one
/// <c>get_usage</c> control request over <c>--input-format stream-json</c>
/// with <c>skip_behaviors</c>, which makes no model call. The child runs under
/// this side's own live config directory and nothing else, from a fixed empty
/// working directory, in safe mode so no hook, plugin or MCP server of the
/// operator's starts for it.
/// <para>
/// <c>CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC</c> is removed from the child's
/// environment: with it set, the CLI's usage request fails inside the CLI and it
/// answers with its last saved snapshot as though it were current (issue #220).
/// A settings file can still set it, so every answer is also checked for
/// freshness: <c>seven_day_breakdown.as_of</c>, the server's capture time, must
/// be no older than the run minus <see cref="FreshnessTolerance"/>. An answer
/// without one is not trusted either.
/// </para>
/// <para>
/// Nothing the child printed is logged or returned: a failure carries a fixed
/// sentence and, at most, an exit code.
/// </para>
/// </summary>
internal sealed class ClaudeCliUsageReader : ILiveUsageReader
{
    /// <summary>
    /// How far before the run's start the server's capture time may fall and
    /// still count as this run's read: clock skew between this machine and the
    /// server, which the WSL clock in particular can drift into. A saved
    /// snapshot younger than this is let through, and is at most this old.
    /// </summary>
    internal static readonly TimeSpan FreshnessTolerance = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long one read may take before the child is killed and the endpoint
    /// is read instead. A safe-mode read measured about 1.6 s on 2.1.312. Kept
    /// short because the fallback runs after it inside the same bounds: the
    /// leader's deadline for a read through the other side, and the host's
    /// 45 s shutdown drain.
    /// </summary>
    internal static readonly TimeSpan Bound = TimeSpan.FromSeconds(15);

    /// <summary>How long the child gets to exit once its answer is in and its input is closed, before it is killed.</summary>
    private static readonly TimeSpan _exitGrace = TimeSpan.FromSeconds(5);

    internal static readonly string[] Arguments =
    [
        "-p",
        "--safe-mode",
        "--no-session-persistence",
        "--input-format", "stream-json",
        "--output-format", "stream-json",
        "--verbose",
    ];

    /// <summary>
    /// Removed from the child's environment. The first is the flag that turns a
    /// usage read into a stale snapshot. The credential variables would make the
    /// CLI read some other login's usage than the live directory's. The session
    /// variables belong to a Claude Code session the tool may have been started
    /// from, and the child is not part of it.
    /// </summary>
    internal static readonly string[] ClearedVariables =
    [
        "CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC",
        "CLAUDE_CODE_OAUTH_TOKEN",
        "ANTHROPIC_API_KEY",
        "ANTHROPIC_AUTH_TOKEN",
        "CLAUDECODE",
        "CLAUDE_CODE_ENTRYPOINT",
        "CLAUDE_CODE_SESSION_ID",
        "CLAUDE_CODE_CHILD_SESSION",
        "CLAUDE_CODE_SESSION_ATTENDED",
        "CLAUDE_CODE_MESSAGING_SOCKET",
        "CLAUDE_CODE_MESSAGING_TOKEN",
    ];

    private readonly ClaudeExecutable _executable;
    private readonly Result<string?, string> _configDirectory;
    private readonly string _workingDirectory;
    private readonly TimeSpan _timeout;
    private readonly TimeProvider _timeProvider;

    /// <param name="workingDirectory">A folder of the tool's own, created if absent and never anything the operator works in.</param>
    public ClaudeCliUsageReader(
        ClaudeExecutable executable,
        string liveConfigDirectory,
        string stateFilePath,
        string homeDirectory,
        string workingDirectory,
        TimeSpan timeout,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(executable);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        _executable = executable;
        _configDirectory = ConfigDirectoryFor(liveConfigDirectory, stateFilePath, homeDirectory);
        _workingDirectory = workingDirectory;
        _timeout = timeout;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// What <c>CLAUDE_CONFIG_DIR</c> the child needs to see the same live
    /// directory and state file this side does: the directory itself when the
    /// state file sits inside it, or unset for the CLI's default layout
    /// (<c>~/.claude</c> beside <c>~/.claude.json</c>). Pointing the CLI at
    /// <c>~/.claude</c> explicitly would move its state file into that folder,
    /// so the default layout must leave the variable unset. Any other layout
    /// cannot be expressed to the CLI and is refused.
    /// </summary>
    internal static Result<string?, string> ConfigDirectoryFor(string liveConfigDirectory, string stateFilePath, string homeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(liveConfigDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
        string live = Path.TrimEndingDirectorySeparator(Path.GetFullPath(liveConfigDirectory));
        string state = Path.GetFullPath(stateFilePath);
        string home = Path.TrimEndingDirectorySeparator(Path.GetFullPath(homeDirectory));
        if (SamePath(state, Path.Combine(live, ".claude.json")))
        {
            return Result<string?, string>.Success(live);
        }

        return SamePath(live, Path.Combine(home, ".claude")) && SamePath(state, Path.Combine(home, ".claude.json"))
            ? Result<string?, string>.Success(null)
            : Result<string?, string>.Failure("the state file is neither inside the live directory nor at the CLI's default place beside it, so the CLI cannot be pointed at this layout");
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(left, right, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>
    /// How the child is started: the shared start info, standard input
    /// redirected, the working directory set, and the environment cleaned.
    /// </summary>
    internal ProcessStartInfo StartInfo(string? configDirectory)
    {
        ProcessStartInfo startInfo = _executable.StartInfo(Arguments, configDirectory);
        startInfo.RedirectStandardInput = true;
        startInfo.WorkingDirectory = _workingDirectory;
        foreach (string variable in ClearedVariables)
        {
            startInfo.Environment.Remove(variable);
        }

        if (configDirectory is null)
        {
            startInfo.Environment.Remove("CLAUDE_CONFIG_DIR");
        }

        return startInfo;
    }

    public async Task<Result<LiveUsageAnswer, string>> ReadAsync(CancellationToken cancellationToken)
    {
        if (_configDirectory.IsFailure)
        {
            return Failure(_configDirectory.Error);
        }

        try
        {
            Directory.CreateDirectory(_workingDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Failure("the CLI's working directory could not be created (" + exception.GetType().Name + ")");
        }

        string requestId = "usage-" + Guid.NewGuid().ToString("N");
        using System.Diagnostics.Process process = new() { StartInfo = StartInfo(_configDirectory.Value) };
        DateTimeOffset startedAt = _timeProvider.GetUtcNow();
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return Failure("the CLI could not be started");
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_timeout);
        // Read to the end so a full pipe cannot block the child; never looked at.
        Task<string> standardError = process.StandardError.ReadToEndAsync(budget.Token);
        try
        {
            string? answer;
            try
            {
                await process.StandardInput.WriteLineAsync(Request(requestId).AsMemory(), budget.Token);
                await process.StandardInput.FlushAsync(budget.Token);
                answer = await AnswerLineAsync(process.StandardOutput, requestId, budget.Token);
            }
            catch (IOException)
            {
                answer = null;
            }

            try
            {
                // End of input is the CLI's cue to exit.
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // It has already gone; the pipe was closed under the flush.
            }

            if (answer is null)
            {
                await process.WaitForExitAsync(budget.Token);
                return Failure("the CLI exited with code " + process.ExitCode.ToString(CultureInfo.InvariantCulture) + " without answering the usage request; it may be too old for get_usage or --safe-mode");
            }

            using var grace = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
            grace.CancelAfter(_exitGrace);
            try
            {
                await process.WaitForExitAsync(grace.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The answer is in; a child slow to exit costs nothing but its kill.
                await KillAndReapAsync(process);
            }

            return Parse(answer, requestId, startedAt, _timeProvider.GetUtcNow());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await KillAndReapAsync(process);
            return Failure("the CLI did not answer within " + _timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s and was killed");
        }
        catch (OperationCanceledException)
        {
            await KillAndReapAsync(process);
            throw;
        }
        finally
        {
            Observe(standardError);
        }
    }

    /// <summary>
    /// Kills the child and waits, briefly and on a fresh bound, for it to be
    /// gone: until it is, Windows holds its working directory open.
    /// </summary>
    private static async Task KillAndReapAsync(System.Diagnostics.Process process)
    {
        ChildProcess.TryKill(process);
        using CancellationTokenSource bound = new(_exitGrace);
        try
        {
            await process.WaitForExitAsync(bound.Token);
        }
        catch (OperationCanceledException)
        {
            // Not gone within the bound; nothing more to do for it here.
        }
    }

    private static string Request(string requestId) =>
        new JsonObject
        {
            ["type"] = "control_request",
            ["request_id"] = requestId,
            ["request"] = new JsonObject { ["subtype"] = "get_usage", ["skip_behaviors"] = true },
        }.ToJsonString();

    /// <summary>
    /// Reads standard output a line at a time until the control response for
    /// <paramref name="requestId"/>, skipping every other event the CLI streams
    /// first. Null when the output ends before it.
    /// </summary>
    private static async Task<string?> AnswerLineAsync(StreamReader output, string requestId, CancellationToken cancellationToken)
    {
        while (await output.ReadLineAsync(cancellationToken) is string line)
        {
            if (IsAnswer(line, requestId))
            {
                return line;
            }
        }

        return null;
    }

    private static bool IsAnswer(string line, string requestId)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && Text(root, "type") == "control_response"
                && root.TryGetProperty("response", out JsonElement response)
                && Text(response, "request_id") == requestId;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// The control response turned into the endpoint's shape, or why it cannot
    /// be. Pure, so every refusal is tested without a process.
    /// </summary>
    internal static Result<LiveUsageAnswer, string> Parse(string line, string requestId, DateTimeOffset startedAt, DateTimeOffset now)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return Failure("the CLI's answer did not parse");
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("response", out JsonElement envelope)
                || envelope.ValueKind != JsonValueKind.Object
                || Text(envelope, "request_id") != requestId)
            {
                return Failure("the CLI's answer is not the response to the usage request");
            }

            if (Text(envelope, "subtype") != "success"
                || !envelope.TryGetProperty("response", out JsonElement usage)
                || usage.ValueKind != JsonValueKind.Object)
            {
                return Failure("the CLI refused the usage request");
            }

            if (!usage.TryGetProperty("rate_limits_available", out JsonElement available) || available.ValueKind != JsonValueKind.True)
            {
                return Failure("the CLI reports no usage figures for this login");
            }

            if (!usage.TryGetProperty("rate_limits", out JsonElement limits) || limits.ValueKind != JsonValueKind.Object)
            {
                return Failure("the CLI's usage request failed inside the CLI and it had no figures to answer with");
            }

            DateTimeOffset? asOf = limits.TryGetProperty("seven_day_breakdown", out JsonElement breakdown)
                ? JsonInstant.Read(breakdown, "as_of")
                : null;
            if (asOf is not DateTimeOffset captured)
            {
                return Failure("the CLI's answer carries no capture time, so it may be a saved snapshot rather than a read");
            }

            if (captured < startedAt - FreshnessTolerance)
            {
                return Failure(
                    "the CLI answered with a snapshot captured at " + captured.ToString("u", CultureInfo.InvariantCulture)
                    + ", before this read started; CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC set in a settings file's env block is the likely cause");
            }

            JsonObject body = new();
            if (limits.TryGetProperty("limits", out JsonElement generic) && generic.ValueKind == JsonValueKind.Array)
            {
                body["limits"] = JsonNode.Parse(generic.GetRawText());
            }
            else
            {
                JsonArray windows = Windows(limits);
                if (windows.Count == 0)
                {
                    return Failure("the CLI's answer carries no usage window this build reads");
                }

                body["limits"] = windows;
            }

            if (limits.TryGetProperty("extra_usage", out JsonElement extra) && extra.ValueKind == JsonValueKind.Object)
            {
                body["extra_usage"] = JsonNode.Parse(extra.GetRawText());
            }

            return Result<LiveUsageAnswer, string>.Success(new LiveUsageAnswer(body, captured < now ? captured : now));
        }
    }

    /// <summary>
    /// The generic array built from the named windows, for a CLI whose answer
    /// lacks <c>limits[]</c>: the five-hour and seven-day windows and each
    /// model-scoped one. Every other window, codenamed or new, is ignored, and a
    /// window whose utilization is absent or outside 0 to 100 is left out
    /// rather than shown as a number nobody measured.
    /// </summary>
    private static JsonArray Windows(JsonElement limits)
    {
        JsonArray windows = [];
        if (limits.TryGetProperty("five_hour", out JsonElement fiveHour) && Window("session", "session", fiveHour, scope: null) is JsonObject session)
        {
            windows.Add(session);
        }

        if (limits.TryGetProperty("seven_day", out JsonElement sevenDay) && Window("weekly_all", "weekly", sevenDay, scope: null) is JsonObject weekly)
        {
            windows.Add(weekly);
        }

        if (limits.TryGetProperty("model_scoped", out JsonElement scoped) && scoped.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement model in scoped.EnumerateArray())
            {
                if (Window("weekly_scoped", "weekly", model, Text(model, "display_name")) is JsonObject window)
                {
                    windows.Add(window);
                }
            }
        }

        return windows;
    }

    private static JsonObject? Window(string kind, string group, JsonElement window, string? scope)
    {
        if (window.ValueKind != JsonValueKind.Object
            || !window.TryGetProperty("utilization", out JsonElement utilization)
            || utilization.ValueKind != JsonValueKind.Number
            || !utilization.TryGetDouble(out double percent)
            || percent is < 0 or > 100)
        {
            return null;
        }

        JsonObject entry = new()
        {
            ["kind"] = kind,
            ["group"] = group,
            ["percent"] = percent,
            ["is_active"] = true,
        };
        if (JsonInstant.Read(window, "resets_at") is DateTimeOffset resetsAt)
        {
            entry["resets_at"] = resetsAt.ToString("O", CultureInfo.InvariantCulture);
        }

        if (scope is not null)
        {
            entry["scope"] = new JsonObject { ["model"] = new JsonObject { ["display_name"] = scope } };
        }

        return entry;
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static Result<LiveUsageAnswer, string> Failure(string reason) => Result<LiveUsageAnswer, string>.Failure(reason);

    /// <summary>The drain of standard error, observed and never read: a kill can fault it after nobody awaits it.</summary>
    private static void Observe(Task read) =>
        _ = read.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}
