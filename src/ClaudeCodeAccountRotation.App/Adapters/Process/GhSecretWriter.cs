using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Accounts;

namespace ClaudeCodeAccountRotation.App.Adapters.Process;

/// <summary>Who may read an organization secret, as <c>gh secret set --visibility</c> names it.</summary>
internal enum OrgSecretVisibility
{
    Private,
    All,
    Selected,
}

/// <summary>One secret to set and who may read it: <c>Visibility</c> and <c>Repositories</c> are as <see cref="ICiSecretWriter.SetAsync"/> takes them.</summary>
internal sealed record SecretTarget(CiTokenSecret Secret, OrgSecretVisibility? Visibility, IReadOnlyList<string> Repositories);

/// <summary>Writes one GitHub Actions secret. The value is handed over in memory and kept by nothing here.</summary>
internal interface ICiSecretWriter
{
    /// <summary>
    /// Whether a set could succeed at all, asked before a token is made: a
    /// token minted for a writer that then fails is a credential made for
    /// nothing.
    /// </summary>
    Task<Result<Unit, string>> CheckAsync(CancellationToken cancellationToken);

    /// <param name="repositories">For <see cref="OrgSecretVisibility.Selected"/> only: the repositories, by name, that may read it.</param>
    Task<Result<Unit, string>> SetAsync(
        CiTokenSecret secret,
        OrgSecretVisibility? visibility,
        IReadOnlyList<string> repositories,
        string value,
        CancellationToken cancellationToken);
}

/// <summary>The writer when no <c>gh</c> could be found: every set fails with the reason, before any token is made.</summary>
internal sealed class UnavailableCiSecretWriter(string reason) : ICiSecretWriter
{
    public Task<Result<Unit, string>> CheckAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Result<Unit, string>.Failure(reason));

    public Task<Result<Unit, string>> SetAsync(
        CiTokenSecret secret,
        OrgSecretVisibility? visibility,
        IReadOnlyList<string> repositories,
        string value,
        CancellationToken cancellationToken) => Task.FromResult(Result<Unit, string>.Failure(reason));
}

/// <summary>
/// <c>gh secret set</c>, with the value written to its standard input rather
/// than passed with <c>--body</c>, so it is never part of a command line another
/// process can list. <c>gh</c> reads the value from standard input when
/// <c>--body</c> is absent and input is not a terminal.
/// <para>
/// Sets pass the shared <see cref="GhWriteGate"/>: one gh write at a time,
/// at least a second apart, whichever writer runs it.
/// </para>
/// <para>
/// Nothing <c>gh</c> prints is returned: a failure is reported by its exit code
/// alone, with the operator pointed at running the same command by hand, or as
/// <see cref="RateLimitMessage"/> when its error output names a rate limit.
/// </para>
/// </summary>
internal sealed class GhSecretWriter(string executable, TimeSpan timeout, GhWriteGate gate) : ICiSecretWriter
{
    /// <summary>The gate this writer's sets pass.</summary>
    internal GhWriteGate Gate => gate;

    public const string RateLimitMessage =
        "GitHub's API rate limit was reached, so this write and every one after it was stopped and nothing retried them. Wait for the limit to reset, then try again.";

    /// <summary>The <c>gh</c> on PATH, or the reason there is none.</summary>
    public static Result<string, string> Locate(string? pathVariable, bool isWindows)
    {
        string name = isWindows ? "gh.exe" : "gh";
        foreach (string directory in (pathVariable ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string path = Path.Combine(directory, name);
            if (File.Exists(path))
            {
                return Result<string, string>.Success(path);
            }
        }

        return Result<string, string>.Failure("the GitHub CLI (gh) was not found on PATH; install it and run `gh auth login` first");
    }

    /// <summary>
    /// The argument list, never a command line, and never the value. An
    /// organization secret must name its visibility: gh would make it private.
    /// </summary>
    public static IReadOnlyList<string> Arguments(CiTokenSecret secret, OrgSecretVisibility? visibility, IReadOnlyList<string> repositories)
    {
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentNullException.ThrowIfNull(repositories);
        List<string> arguments = ["secret", "set", secret.Name, "--app", "actions"];
        if (secret.Scope == CiSecretScope.Repository)
        {
            arguments.AddRange(["--repo", secret.Owner]);
            return arguments;
        }

        if (visibility is null)
        {
            throw new ArgumentException("an organization secret needs its visibility, or gh makes it private", nameof(visibility));
        }

        arguments.AddRange(["--org", secret.Owner]);
        if (visibility == OrgSecretVisibility.Selected)
        {
            // --repos implies the selected visibility.
            arguments.AddRange(["--repos", string.Join(',', repositories)]);
        }
        else
        {
            arguments.AddRange(["--visibility", visibility == OrgSecretVisibility.All ? "all" : "private"]);
        }

        return arguments;
    }

    /// <summary><c>gh auth status</c>: exit 0 when gh holds a working sign-in.</summary>
    public async Task<Result<Unit, string>> CheckAsync(CancellationToken cancellationToken)
    {
        Result<int, string> status = await RunAsync(["auth", "status"], input: null, cancellationToken);
        if (status.IsFailure)
        {
            return Result<Unit, string>.Failure(status.Error);
        }

        return status.Value == 0
            ? Result<Unit, string>.Success(Unit.Value)
            : Result<Unit, string>.Failure("the GitHub CLI is not signed in; run `gh auth login` first");
    }

    public async Task<Result<Unit, string>> SetAsync(
        CiTokenSecret secret,
        OrgSecretVisibility? visibility,
        IReadOnlyList<string> repositories,
        string value,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        IReadOnlyList<string> arguments = Arguments(secret, visibility, repositories);
        Result<GhExit, string> set = await gate.RunAsync(() => GhProcess.RunAsync(executable, arguments, value, timeout, cancellationToken), cancellationToken);

        if (set.IsFailure)
        {
            return Result<Unit, string>.Failure(set.Error);
        }

        if (set.Value.ExitCode == 0)
        {
            return Result<Unit, string>.Success(Unit.Value);
        }

        // Matched, never relayed: the rest of what gh printed stays here.
        return set.Value.StandardError.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
            ? Result<Unit, string>.Failure(RateLimitMessage)
            : Result<Unit, string>.Failure(
                "gh secret set exited with code " + set.Value.ExitCode
                + "; check `gh auth status` and that you may write secrets for " + secret.Owner);
    }

    /// <summary>Runs gh with <paramref name="input"/>, if any, on its standard input, and returns its exit code alone.</summary>
    private async Task<Result<int, string>> RunAsync(IReadOnlyList<string> arguments, string? input, CancellationToken cancellationToken)
    {
        Result<GhExit, string> run = await GhProcess.RunAsync(executable, arguments, input, timeout, cancellationToken);
        return run.IsSuccess
            ? Result<int, string>.Success(run.Value.ExitCode)
            : Result<int, string>.Failure(run.Error);
    }
}
