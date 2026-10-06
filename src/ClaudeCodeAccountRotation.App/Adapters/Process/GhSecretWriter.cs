using System.Diagnostics;
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
/// Nothing <c>gh</c> prints is returned: a failure is reported by its exit code
/// alone, with the operator pointed at running the same command by hand.
/// </para>
/// </summary>
internal sealed class GhSecretWriter(string executable, TimeSpan timeout) : ICiSecretWriter
{
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

    /// <summary>The argument list, never a command line, and never the value.</summary>
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
        Result<int, string> set = await RunAsync(Arguments(secret, visibility, repositories), value, cancellationToken);
        if (set.IsFailure)
        {
            return Result<Unit, string>.Failure(set.Error);
        }

        return set.Value == 0
            ? Result<Unit, string>.Success(Unit.Value)
            : Result<Unit, string>.Failure(
                "gh secret set exited with code " + set.Value
                + "; check `gh auth status` and that you may write secrets for " + secret.Owner);
    }

    /// <summary>Runs gh with <paramref name="input"/>, if any, on its standard input, and returns its exit code.</summary>
    private async Task<Result<int, string>> RunAsync(IReadOnlyList<string> arguments, string? input, CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // No prompt may wait on a terminal that is not there, and no update
        // notice is wanted on output nobody reads.
        startInfo.Environment["GH_PROMPT_DISABLED"] = "1";
        startInfo.Environment["GH_NO_UPDATE_NOTIFIER"] = "1";

        using System.Diagnostics.Process process = new() { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return Result<int, string>.Failure("the GitHub CLI (gh) could not be started");
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(timeout);
        try
        {
            // Drained, not read: a full pipe would block gh, and what it says is not relayed.
            Task drainOut = process.StandardOutput.ReadToEndAsync(budget.Token);
            Task drainError = process.StandardError.ReadToEndAsync(budget.Token);
            if (input is not null)
            {
                await process.StandardInput.WriteAsync(input.AsMemory(), budget.Token);
                await process.StandardInput.FlushAsync(budget.Token);
            }

            process.StandardInput.Close();
            await Task.WhenAll(drainOut, drainError);
            await process.WaitForExitAsync(budget.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            ChildProcess.TryKill(process);
            return Result<int, string>.Failure("gh did not finish in time");
        }
        catch (IOException)
        {
            ChildProcess.TryKill(process);
            return Result<int, string>.Failure("gh closed its input early");
        }

        return Result<int, string>.Success(process.ExitCode);
    }
}
