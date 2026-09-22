using System.Diagnostics;
using System.Text.Json;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Ports;
using Microsoft.Extensions.Logging;

namespace ClaudeCodeAccountRotation.App.Adapters.Process;

/// <summary>
/// Runs the unmodified CLI with an argument list (never a joined command line),
/// optionally under <c>CLAUDE_CONFIG_DIR</c>, and kills the process at the
/// timeout. A non-zero exit, a timeout, or unparsable output is a failure
/// carrying a short classified sentence. Two commands run this way: <c>auth status
/// --json</c>, the authority on which account a folder holds, and <c>auth
/// logout</c>, which revokes a removed account's login.
/// <para>
/// A failure carries the command and its exit code, or which kind of failure it
/// was, and nothing the child wrote. What the child wrote goes to the log, cut
/// off at <see cref="LoggedOutputLimit"/> characters, never into the returned
/// string: those strings are embedded verbatim in the responses the page
/// renders, and the child's output is on the wrong side of that boundary
/// whatever it happens to hold.
/// </para>
/// </summary>
internal sealed partial class ClaudeCliProcessAuthStatus : IClaudeCliAuthStatus, IClaudeCliLogout
{
    private static readonly string[] _statusArguments = ["auth", "status", "--json"];
    private static readonly string[] _logoutArguments = ["auth", "logout"];

    /// <summary>
    /// How much of a child's printout one log line may quote. A CLI failure is a
    /// sentence or two, and 400 characters covers that while leaving room for the
    /// warning template in a typical log record. Anything longer is the case the
    /// cap exists for (a dumped document, a token, a stack), and
    /// <see cref="TruncationMarker"/> says the log is not the whole printout.
    /// </summary>
    private const int LoggedOutputLimit = 400;

    private const string TruncationMarker = " (truncated)";

    /// <summary>
    /// How long to keep reading after a kill. The timeout token is already
    /// canceled, so this bound is a fresh source: long enough for a killed child
    /// to finish flushing what it had written, and short enough that a pipe that
    /// never closes cannot hold the caller.
    /// </summary>
    private static readonly TimeSpan _pipeDrainAfterKill = TimeSpan.FromSeconds(2);

    private readonly ClaudeExecutable _executable;
    private readonly TimeSpan _timeout;
    private readonly ILogger<ClaudeCliProcessAuthStatus> _logger;

    public ClaudeCliProcessAuthStatus(ClaudeExecutable executable, TimeSpan timeout, ILogger<ClaudeCliProcessAuthStatus> logger)
    {
        ArgumentNullException.ThrowIfNull(executable);
        ArgumentNullException.ThrowIfNull(logger);
        _executable = executable;
        _timeout = timeout;
        _logger = logger;
    }

    public async Task<Result<ClaudeAuthStatus, string>> ReadAsync(string? configDirectory, CancellationToken cancellationToken)
    {
        Result<string, string> run = await RunAsync(_statusArguments, configDirectory, cancellationToken);
        if (run.IsFailure)
        {
            return Result<ClaudeAuthStatus, string>.Failure(run.Error);
        }

        Result<ClaudeAuthStatus, string> parsed = Parse(run.Value);
        if (parsed.IsFailure)
        {
            LogNoJson("claude " + string.Join(' ', _statusArguments), BoundForLog(run.Value));
        }

        return parsed;
    }

    public async Task<Result<Unit, string>> LogoutAsync(string configDirectory, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);
        Result<string, string> run = await RunAsync(_logoutArguments, configDirectory, cancellationToken);
        return run.IsFailure ? Result<Unit, string>.Failure(run.Error) : Result<Unit, string>.Success(Unit.Value);
    }

    private async Task<Result<string, string>> RunAsync(string[] arguments, string? configDirectory, CancellationToken cancellationToken)
    {
        string command = "claude " + string.Join(' ', arguments);
        ProcessStartInfo startInfo = _executable.StartInfo(arguments, configDirectory);

        using System.Diagnostics.Process process = new() { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            return Result<string, string>.Failure("could not start " + _executable.FileName + ": " + exception.Message);
        }

        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> standardError = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            ChildProcess.TryKill(process);
            LogTimedOut(command, BoundForLog(await PrintedAfterKillAsync(standardOutput, standardError)));
            return Result<string, string>.Failure(command + " timed out after " + _timeout.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " s and was killed");
        }
        catch (OperationCanceledException)
        {
            ChildProcess.TryKill(process);
            await PrintedAfterKillAsync(standardOutput, standardError);
            throw;
        }

        string output = await standardOutput;
        string error = await standardError;
        if (process.ExitCode == 0)
        {
            return Result<string, string>.Success(output);
        }

        string exitCode = process.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
        LogNonZeroExit(command, exitCode, BoundForLog(error + output));
        return Result<string, string>.Failure(command + " exited with code " + exitCode + "; see the log for what it printed");
    }

    /// <summary>
    /// Reads whatever both pipes still have after <see cref="ChildProcess.TryKill"/>.
    /// The caller's token may already be canceled, and the timeout token always is,
    /// so the bound is a new source. A read that faults or does not finish is
    /// observed here; its text is not.
    /// </summary>
    private static async Task<string> PrintedAfterKillAsync(Task<string> standardOutput, Task<string> standardError)
    {
        using CancellationTokenSource bound = new(_pipeDrainAfterKill);
        // Both drains start before either is awaited, and WhenAll waits out a fault
        // from one so the other cannot be abandoned.
        string[] printed = await Task.WhenAll(
            DrainPipeAsync(standardError, bound.Token),
            DrainPipeAsync(standardOutput, bound.Token));
        return printed[0] + printed[1];
    }

    private static async Task<string> DrainPipeAsync(Task<string> read, CancellationToken bound)
    {
        try
        {
            return await read.WaitAsync(bound);
        }
        catch (OperationCanceledException)
        {
            // The bound elapsed, or the read was canceled with the caller.
        }
        catch (IOException)
        {
            // The pipe closed when the child was killed.
        }
        catch (InvalidOperationException)
        {
            // The process object was disposed while the read was still pending.
        }

        if (read.IsCompletedSuccessfully)
        {
            return await read;
        }

        ObserveLaterFault(read);
        return string.Empty;
    }

    /// <summary>
    /// A read that is still running when the drain bound elapses faults later, when
    /// the process is disposed. Nothing awaits it by then, so the fault has to be
    /// observed on purpose.
    /// </summary>
    private static void ObserveLaterFault(Task read)
    {
        if (read.IsCompleted)
        {
            _ = read.Exception;
            return;
        }

        _ = read.ContinueWith(
            static completed =>
            {
                _ = completed.Exception;
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static string BoundForLog(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.Length <= LoggedOutputLimit)
        {
            return trimmed;
        }

        return trimmed[..LoggedOutputLimit] + TruncationMarker;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Command} exited with code {ExitCode} and printed: {Output}")]
    private partial void LogNonZeroExit(string command, string exitCode, string output);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Command} timed out and was killed; it had printed: {Output}")]
    private partial void LogTimedOut(string command, string output);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Command} printed no JSON object: {Output}")]
    private partial void LogNoJson(string command, string output);

    private static Result<ClaudeAuthStatus, string> Parse(string output)
    {
        try
        {
            using var document = JsonDocument.Parse(output.Trim());
            JsonElement root = document.RootElement;
            return Result<ClaudeAuthStatus, string>.Success(new ClaudeAuthStatus(
                // A logged-out account is a successful status. Callers judge LoggedIn
                // false as an ordinary result; it is not turned into an error here.
                LoggedIn: root.TryGetProperty("loggedIn", out JsonElement loggedIn) && loggedIn.ValueKind == JsonValueKind.True,
                Email: Text(root, "email"),
                AuthMethod: Text(root, "authMethod"),
                OrganizationName: Text(root, "orgName"),
                SubscriptionType: Text(root, "subscriptionType"),
                ProjectsDirectory: Text(root, "projectsDirectory")));
        }
        catch (JsonException)
        {
            // The exception message quotes an invalid literal, which is the child's
            // own text, so it stays out of the failure the page renders. The bounded
            // printout is logged by the caller.
            return Result<ClaudeAuthStatus, string>.Failure("claude auth status printed no JSON object");
        }
    }

    private static string? Text(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
