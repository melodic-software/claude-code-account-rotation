using System.Diagnostics;
using ClaudeCodeAccountRotation.Core;

namespace ClaudeCodeAccountRotation.App.Adapters.Process;

/// <summary>What one <c>gh</c> run left behind: its exit code and everything it printed.</summary>
internal sealed record GhExit(int ExitCode, string StandardOutput, string StandardError);

/// <summary>
/// One <c>gh</c> run with no terminal: an argument list, never a command line,
/// and an optional value on standard input. Each caller decides what, if
/// anything, of the output it relays.
/// </summary>
internal static class GhProcess
{
    public static async Task<Result<GhExit, string>> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string? input,
        TimeSpan timeout,
        CancellationToken cancellationToken)
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
            return Result<GhExit, string>.Failure("the GitHub CLI (gh) could not be started");
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(timeout);
        try
        {
            // Both read to the end: a full pipe would block gh.
            Task<string> output = process.StandardOutput.ReadToEndAsync(budget.Token);
            Task<string> error = process.StandardError.ReadToEndAsync(budget.Token);
            if (input is not null)
            {
                await process.StandardInput.WriteAsync(input.AsMemory(), budget.Token);
                await process.StandardInput.FlushAsync(budget.Token);
            }

            process.StandardInput.Close();
            await Task.WhenAll(output, error);
            await process.WaitForExitAsync(budget.Token);
            return Result<GhExit, string>.Success(new GhExit(process.ExitCode, await output, await error));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            ChildProcess.TryKill(process);
            return Result<GhExit, string>.Failure("gh did not finish in time");
        }
        catch (IOException)
        {
            ChildProcess.TryKill(process);
            return Result<GhExit, string>.Failure("gh closed its input early");
        }
    }
}
