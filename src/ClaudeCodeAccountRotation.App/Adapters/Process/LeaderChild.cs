using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
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

    int Id { get; }

    /// <summary>Kills the child and its tree, then waits up to <paramref name="wait"/>; true once it has exited.</summary>
    bool Stop(TimeSpan wait);
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
internal sealed partial class ProcessLeaderChild(System.Diagnostics.Process process) : ILeaderChild
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
        // Under `dotnet <app>.dll` the process is the host, and the app is its first argument.
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
        }

        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        if (OperatingSystem.IsWindows())
        {
            StopStandardHandleInheritance();
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

    public int Id => process.Id;

    public bool Stop(TimeSpan wait)
    {
        ChildProcess.TryKill(process);
        return process.WaitForExit(wait);
    }

    public void Dispose() => process.Dispose();

    /// <summary>
    /// Clears the inherit flag on this process's standard handles. The start
    /// passes <c>bInheritHandles</c>, so a child would otherwise hold a caller's
    /// pipe open for its whole life, and <c>$(exe --open)</c> would never see
    /// end of file. Safe here: this process exits right after the start.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static void StopStandardHandleInheritance()
    {
        foreach (int standard in (int[])[StandardInput, StandardOutput, StandardError])
        {
            StopInheriting(GetStdHandle(standard));
        }
    }

    /// <summary>False for a handle that is not valid, such as no standard handle at all.</summary>
    [SupportedOSPlatform("windows")]
    internal static bool StopInheriting(nint handle) => SetHandleInformation(handle, HandleFlagInherit, 0);

    [SupportedOSPlatform("windows")]
    internal static bool IsInheritable(nint handle) => GetHandleInformation(handle, out uint flags) && (flags & HandleFlagInherit) != 0;

    private const int StandardInput = -10;
    private const int StandardOutput = -11;
    private const int StandardError = -12;
    private const uint HandleFlagInherit = 1;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [SupportedOSPlatform("windows")]
    private static partial nint GetStdHandle(int standardHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [SupportedOSPlatform("windows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetHandleInformation(nint handle, uint mask, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [SupportedOSPlatform("windows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetHandleInformation(nint handle, out uint flags);
}
