using System.Diagnostics;
using ClaudeCodeAccountRotation.Core;

namespace ClaudeCodeAccountRotation.App.Adapters.Process;

/// <summary>
/// A leader this process started to run on after it exits, seen only as
/// "still running or exited with a code, and can be killed". Disposing it
/// releases the handle and leaves the process running.
/// </summary>
internal interface ILeaderChild : IDisposable
{
    /// <summary>The exit code once the child has exited; null while it runs.</summary>
    int? ExitCode { get; }

    void Kill();
}

/// <summary>Starts this executable as a leader with the given argument list.</summary>
internal delegate Result<ILeaderChild, string> LeaderChildFactory(IReadOnlyList<string> arguments);

/// <summary>
/// This executable started again with no window: <c>CREATE_NO_WINDOW</c> gives
/// it a console of its own that is never shown, so closing the launching
/// terminal does not reach it, and the console programs it starts in turn
/// (<c>wsl.exe</c>, <c>claude</c>) attach to that hidden console rather than
/// each opening a window. No stream is redirected: on Windows a redirect of
/// any one stream hands the child this process's other handles, and the
/// leader's log would print into the launching terminal after this exits.
/// </summary>
internal sealed class ProcessLeaderChild(System.Diagnostics.Process process) : ILeaderChild
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Ownership transfers to the caller through the result.")]
    public static Result<ILeaderChild, string> Start(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string? executable = Environment.ProcessPath;
        if (executable is null)
        {
            return Result<ILeaderChild, string>.Failure("could not start the leader: this executable's path is unknown");
        }

        ProcessStartInfo start = new(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            var started = System.Diagnostics.Process.Start(start);
            return started is null
                ? Result<ILeaderChild, string>.Failure("could not start the leader: " + executable + " did not start")
                : Result<ILeaderChild, string>.Success(new ProcessLeaderChild(started));
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return Result<ILeaderChild, string>.Failure("could not start the leader: " + exception.Message);
        }
    }

    public int? ExitCode => process.HasExited ? process.ExitCode : null;

    public void Kill() => ChildProcess.TryKill(process);

    public void Dispose() => process.Dispose();
}
