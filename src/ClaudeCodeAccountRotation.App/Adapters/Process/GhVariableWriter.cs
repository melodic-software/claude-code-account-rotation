using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ClaudeCodeAccountRotation.Core;

namespace ClaudeCodeAccountRotation.App.Adapters.Process;

/// <summary>
/// Which repositories may use an organization variable, as GitHub last
/// answered: <c>all</c>, <c>private</c> or <c>selected</c>, and for
/// <c>selected</c> the ids of those repositories.
/// </summary>
internal sealed record VariableVisibility(string Kind, IReadOnlyList<long> SelectedRepositoryIds);

/// <summary>
/// Changes the value of an organization's Actions variable that already
/// exists, sending back the visibility read just before. Never creates or
/// deletes one, and never writes a repository or environment variable.
/// </summary>
internal interface IGhVariableWriter
{
    /// <summary>A 404 comes back as a failure with that status: the variable does not exist and is not created.</summary>
    Task<Result<Unit, GhApiFailure>> SetOrganizationVariableAsync(string organization, string name, bool value, VariableVisibility visibility, CancellationToken cancellationToken);
}

/// <summary>The writer when no <c>gh</c> could be found: every write fails with the reason.</summary>
internal sealed class UnavailableGhVariableWriter(string reason) : IGhVariableWriter
{
    public Task<Result<Unit, GhApiFailure>> SetOrganizationVariableAsync(string organization, string name, bool value, VariableVisibility visibility, CancellationToken cancellationToken) =>
        Task.FromResult(Result<Unit, GhApiFailure>.Failure(new GhApiFailure(reason, RateLimited: false)));
}

/// <summary>
/// <c>gh api --method PATCH orgs/&lt;org&gt;/actions/variables/&lt;name&gt; -f value=&lt;true|false&gt; -f visibility=&lt;kind&gt;</c>,
/// plus <c>-F selected_repository_ids[]=&lt;id&gt;</c> per id when the kind is
/// <c>selected</c>, through the shared <see cref="GhWriteGate"/>: one gh write
/// at a time, at least a second apart.
/// <para>
/// "Update an organization variable" marks <c>visibility</c> optional but
/// does not say an omitted one is kept, so the caller reads the current
/// visibility first and this sends it back unchanged. Basis:
/// https://docs.github.com/en/rest/actions/variables#update-an-organization-variable.
/// <c>gh variable set</c> is never used: it sets visibility to private when
/// none is given.
/// </para>
/// <para>
/// Without <c>--include</c>, a refusal is told by gh's exit code, the
/// <c>(HTTP nnn)</c> it prints on its error stream, and the body it prints on
/// its output. Nothing gh prints is relayed.
/// </para>
/// </summary>
internal sealed partial class GhVariableWriter(string executable, TimeSpan timeout, GhWriteGate gate) : IGhVariableWriter
{
    /// <summary>The gate this writer's PATCHes pass.</summary>
    internal GhWriteGate Gate => gate;

    /// <summary>
    /// <c>-f</c> for the value and the visibility, which GitHub takes as
    /// strings; <c>-F</c> for each repository id, an integer. A selected
    /// variable with no repositories sends <c>selected_repository_ids[]</c>
    /// alone, which gh sends as an empty array (<c>gh api --help</c>).
    /// </summary>
    public static IReadOnlyList<string> Arguments(string organization, string name, bool value, VariableVisibility visibility)
    {
        ArgumentNullException.ThrowIfNull(visibility);
        List<string> arguments = ["api", "--method", "PATCH", VariablePath(organization, name), "-f", "value=" + (value ? "true" : "false"), "-f", "visibility=" + visibility.Kind];
        if (visibility.Kind == "selected")
        {
            if (visibility.SelectedRepositoryIds.Count == 0)
            {
                arguments.AddRange(["-F", "selected_repository_ids[]"]);
            }

            foreach (long id in visibility.SelectedRepositoryIds)
            {
                arguments.AddRange(["-F", "selected_repository_ids[]=" + id.ToString(CultureInfo.InvariantCulture)]);
            }
        }

        return arguments;
    }

    public async Task<Result<Unit, GhApiFailure>> SetOrganizationVariableAsync(string organization, string name, bool value, VariableVisibility visibility, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> arguments = Arguments(organization, name, value, visibility);
        Result<GhExit, string> run = await gate.RunAsync(() => GhProcess.RunAsync(executable, arguments, input: null, timeout, cancellationToken), cancellationToken);
        return run.IsFailure
            ? Result<Unit, GhApiFailure>.Failure(new GhApiFailure(run.Error, RateLimited: false))
            : Read(VariablePath(organization, name), run.Value);
    }

    /// <summary>Exit 0 is GitHub's 204. Otherwise the status gh names, or the body's own <c>status</c>, decides.</summary>
    public static Result<Unit, GhApiFailure> Read(string path, GhExit exit)
    {
        ArgumentNullException.ThrowIfNull(exit);
        if (exit.ExitCode == 0)
        {
            return Result<Unit, GhApiFailure>.Success(Unit.Value);
        }

        int status = Status(exit);
        if (status is 403 or 429 && GhApiReader.RateLimited(status, [], exit.StandardOutput))
        {
            return Result<Unit, GhApiFailure>.Failure(new GhApiFailure(GhApiReader.RateLimitMessage, RateLimited: true, status));
        }

        if (status == 404)
        {
            return Result<Unit, GhApiFailure>.Failure(new GhApiFailure(
                "GitHub answered HTTP 404 for " + path + ": the variable is not set or not visible to this sign-in. The tool never creates it.",
                RateLimited: false,
                404));
        }

        return Result<Unit, GhApiFailure>.Failure(new GhApiFailure(
            (status == 0 ? "gh api exited with code " + exit.ExitCode.ToString(CultureInfo.InvariantCulture) : "GitHub answered HTTP " + status.ToString(CultureInfo.InvariantCulture))
                + " for " + path + "; check `gh auth status` and that the sign-in may write it (admin:org)",
            RateLimited: false,
            status == 0 ? null : status));
    }

    private static string VariablePath(string organization, string name) => "orgs/" + organization + "/actions/variables/" + name;

    private static int Status(GhExit exit)
    {
        Match named = HttpStatusPattern().Match(exit.StandardError);
        if (named.Success)
        {
            return int.Parse(named.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture);
        }

        try
        {
            return JsonNode.Parse(exit.StandardOutput) is JsonObject body
                && body["status"] is JsonValue value
                && value.TryGetValue(out string? text)
                && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
                    ? parsed
                    : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    [GeneratedRegex(@"\(HTTP (\d{3})\)", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex HttpStatusPattern();
}
