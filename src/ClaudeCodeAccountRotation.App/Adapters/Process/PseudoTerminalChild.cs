using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Channels;
using ClaudeCodeAccountRotation.Core;
using Microsoft.Win32.SafeHandles;

namespace ClaudeCodeAccountRotation.App.Adapters.Process;

/// <summary>
/// A CLI child that runs on a terminal, for the commands that draw an
/// interactive screen and refuse to run on pipes (<c>claude setup-token</c> is
/// one). The rest of the tool sees only <see cref="ILoginChild"/>: text comes
/// out, a code goes in, it can be killed.
/// <para>
/// The child's standard input, output and error are all the terminal, on
/// Windows a pseudoconsole (<see cref="WindowsPseudoConsoleChild"/>) and on
/// Linux a pseudo-terminal (<see cref="UnixPseudoTerminalChild"/>), sized
/// <see cref="Columns"/> wide. Output must be the terminal too, not a pipe: the
/// child's screen lays itself out to its output's width, and on a pipe it falls
/// back to 80 columns and wraps the sign-in URL and the token across rows. The
/// code is typed in ending with a carriage return, which is the Enter key in
/// the raw mode the child's screen puts the terminal in.
/// </para>
/// </summary>
internal static class PseudoTerminalChild
{
    /// <summary>
    /// Wide enough that neither the sign-in URL nor the token is ever wrapped
    /// across two rows, so each arrives as one run of text.
    /// </summary>
    public const short Columns = 1000;

    public const short Rows = 50;

    /// <summary>The factory the app runs with: a terminal-backed child, or the reason none could be started.</summary>
    public static LoginChildFactory Factory(ClaudeExecutable executable)
    {
        ArgumentNullException.ThrowIfNull(executable);
        return (arguments, configDirectory) =>
        {
            ProcessStartInfo startInfo = executable.StartInfo(arguments, configDirectory);
            if (OperatingSystem.IsWindows())
            {
                return WindowsPseudoConsoleChild.Start(startInfo);
            }

            return OperatingSystem.IsLinux()
                ? UnixPseudoTerminalChild.Start(startInfo)
                : Result<ILoginChild, string>.Failure("generating a CI token from the dashboard is supported on Windows and Linux; run `claude setup-token` by hand here");
        };
    }
}

/// <summary>
/// The Linux side of <see cref="PseudoTerminalChild"/>.
/// <para>
/// .NET cannot hand a child an arbitrary descriptor, so the child is started
/// through <c>/bin/sh</c>, which opens the terminal by its path as standard
/// input, output and error and then <c>exec</c>s the CLI in its own place. The
/// path and every argument reach the shell as positional parameters, never as
/// script text, so nothing in them is parsed by the shell.
/// </para>
/// <para>
/// Both descriptors this process holds are close-on-exec, so no other child it
/// starts (<c>gh</c>, a browser) inherits the terminal the code was typed into.
/// This process keeps the subsidiary side open until the child exits: a
/// controlling side whose subsidiary was never opened, or was closed, reads as
/// ended, and that close is also what ends the output once the child is gone.
/// </para>
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed partial class UnixPseudoTerminalChild : ILoginChild
{
    private const int BufferSize = 4096;

    /// <summary>Opens the terminal named by <c>$1</c> as all three standard streams, then becomes the rest of the arguments.</summary>
    internal const string Trampoline = "tty=$1; shift; exec \"$@\" <\"$tty\" >\"$tty\" 2>&1";

    private readonly System.Diagnostics.Process _process;
    private readonly FileStream _controller;
    private readonly SafeFileHandle _subsidiary;
    private readonly Channel<string> _output = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });

    private UnixPseudoTerminalChild(System.Diagnostics.Process process, FileStream controller, SafeFileHandle subsidiary)
    {
        _process = process;
        _controller = controller;
        _subsidiary = subsidiary;
        _ = Task.Factory.StartNew(ReadOutput, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        _ = WatchAsync();
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Both terminal handles are disposed on every failure path and transfer to the child returned in the result on success.")]
    public static Result<ILoginChild, string> Start(ProcessStartInfo cli)
    {
        ArgumentNullException.ThrowIfNull(cli);
        Result<(SafeFileHandle Controller, SafeFileHandle Subsidiary, string Path), string> opened = Native.OpenTerminal(PseudoTerminalChild.Columns, PseudoTerminalChild.Rows);
        if (opened.IsFailure)
        {
            return Result<ILoginChild, string>.Failure(opened.Error);
        }

        ProcessStartInfo startInfo = new("/bin/sh")
        {
            UseShellExecute = false,
            // Redirected so the child never shares this process's own streams;
            // the trampoline replaces all three with the terminal before the CLI runs.
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in new[] { "-c", Trampoline, "sh", opened.Value.Path, cli.FileName }.Concat(cli.ArgumentList))
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (KeyValuePair<string, string?> variable in cli.Environment)
        {
            startInfo.Environment[variable.Key] = variable.Value;
        }

        FileStream controller = new(opened.Value.Controller, FileAccess.ReadWrite, bufferSize: 0);
        System.Diagnostics.Process process = new() { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            process.Dispose();
            controller.Dispose();
            opened.Value.Subsidiary.Dispose();
            return Result<ILoginChild, string>.Failure("could not start " + cli.FileName + ": " + exception.Message);
        }

        return Result<ILoginChild, string>.Success(new UnixPseudoTerminalChild(process, controller, opened.Value.Subsidiary));
    }

    public async Task<string?> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _output.Reader.ReadAsync(cancellationToken);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    public async Task WriteCodeAsync(string code, CancellationToken cancellationToken)
    {
        byte[] keys = Encoding.UTF8.GetBytes(code + "\r");
        await _controller.WriteAsync(keys, cancellationToken);
        await _controller.FlushAsync(cancellationToken);
    }

    public void Kill() => ChildProcess.TryKill(_process);

    public void Dispose()
    {
        ChildProcess.TryKill(_process);
        _process.Dispose();
        _subsidiary.Dispose();
        _controller.Dispose();
    }

    private void ReadOutput()
    {
        byte[] buffer = new byte[BufferSize];
        char[] characters = new char[BufferSize + 4];
        Decoder decoder = Encoding.UTF8.GetDecoder();
        try
        {
            int read;
            while ((read = _controller.Read(buffer)) > 0)
            {
                int count = decoder.GetChars(buffer, 0, read, characters, 0);
                if (count > 0)
                {
                    _output.Writer.TryWrite(new string(characters, 0, count));
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // EIO once no process holds the subsidiary side: the end of output.
        }
        finally
        {
            _output.Writer.TryComplete();
        }
    }

    /// <summary>
    /// Drains the shell's own pipes, which carry nothing once the trampoline
    /// has run, and closes this process's subsidiary handle when the child
    /// exits, which is what lets the reader see the end.
    /// </summary>
    private async Task WatchAsync()
    {
        try
        {
            Task drainOut = _process.StandardOutput.ReadToEndAsync();
            Task drainError = _process.StandardError.ReadToEndAsync();
            await _process.WaitForExitAsync();
            await Task.WhenAll(drainOut, drainError);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
        {
            // Killed mid-read, or disposed; either way the child is gone.
        }
        finally
        {
            _subsidiary.Dispose();
        }
    }

    /// <summary>The POSIX pseudo-terminal calls, from the C library.</summary>
    private static partial class Native
    {
        private const int OpenReadWrite = 2;
        private const int NoControllingTerminal = 0x100;
        private const int CloseOnExec = 0x80000;
        private const nuint SetWindowSize = 0x5414;

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowSize
        {
            public ushort Rows;
            public ushort Columns;
            public ushort XPixels;
            public ushort YPixels;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Each handle is disposed on every failure path and transfers to the caller through the result on success.")]
        public static Result<(SafeFileHandle Controller, SafeFileHandle Subsidiary, string Path), string> OpenTerminal(short columns, short rows)
        {
            // O_NOCTTY: opening it never makes it this process's controlling
            // terminal. O_CLOEXEC: no child this process starts inherits it.
            int controller = posix_openpt(OpenReadWrite | NoControllingTerminal | CloseOnExec);
            if (controller < 0)
            {
                return Failure("posix_openpt");
            }

            SafeFileHandle controllerHandle = new(controller, ownsHandle: true);
            if (grantpt(controller) != 0 || unlockpt(controller) != 0)
            {
                controllerHandle.Dispose();
                return Failure("grantpt/unlockpt");
            }

            WindowSize size = new() { Rows = (ushort)rows, Columns = (ushort)columns };
            if (ioctl(controller, SetWindowSize, ref size) != 0)
            {
                controllerHandle.Dispose();
                return Failure("TIOCSWINSZ");
            }

            nint name = ptsname(controller);
            string? path = name == 0 ? null : Marshal.PtrToStringUTF8(name);
            if (string.IsNullOrEmpty(path))
            {
                controllerHandle.Dispose();
                return Failure("ptsname");
            }

            int subsidiary = open(path, OpenReadWrite | NoControllingTerminal | CloseOnExec);
            if (subsidiary < 0)
            {
                controllerHandle.Dispose();
                return Failure("open");
            }

            return Result<(SafeFileHandle, SafeFileHandle, string), string>.Success((controllerHandle, new SafeFileHandle(subsidiary, ownsHandle: true), path));
        }

        private static Result<(SafeFileHandle, SafeFileHandle, string), string> Failure(string call) =>
            Result<(SafeFileHandle, SafeFileHandle, string), string>.Failure(
                "could not open a pseudo-terminal (" + call + " failed with errno " + Marshal.GetLastPInvokeError() + ")");

        [LibraryImport("libc", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        private static partial int posix_openpt(int flags);

        [LibraryImport("libc", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        private static partial int grantpt(int descriptor);

        [LibraryImport("libc", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        private static partial int unlockpt(int descriptor);

        // ptsname is not reentrant, and need not be: the CI token runner
        // admits one session at a time, and this is the only caller.
        [LibraryImport("libc", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        private static partial nint ptsname(int descriptor);

        // Variadic in C. A fixed pointer argument is passed the same way on
        // Linux x64 and arm64, which are the only targets this class runs on.
        [LibraryImport("libc", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        private static partial int ioctl(int descriptor, nuint request, ref WindowSize size);

        [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        private static partial int open(string path, int flags);
    }
}
