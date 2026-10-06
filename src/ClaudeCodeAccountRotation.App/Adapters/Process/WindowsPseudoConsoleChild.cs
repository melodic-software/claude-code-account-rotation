using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Channels;
using ClaudeCodeAccountRotation.Core;
using Microsoft.Win32.SafeHandles;

namespace ClaudeCodeAccountRotation.App.Adapters.Process;

/// <summary>
/// The Windows side of <see cref="PseudoTerminalChild"/>: the CLI runs under a
/// pseudoconsole (ConPTY, Windows 10 1809 and later), the same API terminal
/// windows host console programs with, so it sees a real console on every
/// standard handle.
/// <para>
/// What the documentation requires, and this class does: the two pipes are
/// synchronous; the pseudoconsole's own ends are closed once the child holds
/// them; output is read continuously on a thread of its own, because a full
/// output pipe stalls the child; no flag is passed, since inheriting the
/// cursor makes a host with no console of its own wait for an answer that
/// never comes; and <c>ClosePseudoConsole</c>, which waits for its clients to
/// leave before Windows 11 24H2, is only ever called off the reading thread
/// and after the child is gone. The child is started with
/// <c>STARTF_USESTDHANDLES</c> and empty handles, so a host whose own output is
/// redirected (as a detached dashboard's is) cannot leak those handles to it.
/// </para>
/// <para>
/// The child runs in a job object that ends every process in it when killed,
/// so an npm <c>.cmd</c> shim takes its interpreter's children with it.
/// What arrives is the console's rendering: text with cursor moves and colors
/// between the words, which the reader strips before matching anything.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsPseudoConsoleChild : ILoginChild
{
    private const int BufferSize = 4096;

    private readonly Channel<string> _output = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly FileStream _input;
    private readonly FileStream _outputPipe;
    private readonly nint _pseudoConsole;
    private readonly SafeWaitHandle _process;
    private readonly SafeWaitHandle _job;
    private int _closed;

    private WindowsPseudoConsoleChild(nint pseudoConsole, SafeFileHandle input, SafeFileHandle output, SafeWaitHandle process, SafeWaitHandle job)
    {
        _pseudoConsole = pseudoConsole;
        _input = new FileStream(input, FileAccess.Write, bufferSize: 0, isAsync: false);
        _outputPipe = new FileStream(output, FileAccess.Read, bufferSize: 0, isAsync: false);
        _process = process;
        _job = job;
        _ = Task.Factory.StartNew(ReadOutput, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        _ = Task.Factory.StartNew(CloseWhenExited, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Every handle is disposed in the finally unless the start succeeds, when ownership transfers to the child returned in the result.")]
    public static Result<ILoginChild, string> Start(ProcessStartInfo cli)
    {
        ArgumentNullException.ThrowIfNull(cli);
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            return Result<ILoginChild, string>.Failure("generating a CI token needs Windows 10 version 1809 or later (the pseudoconsole API)");
        }

        SafeFileHandle? inputRead = null, inputWrite = null, outputRead = null, outputWrite = null;
        nint pseudoConsole = 0;
        nint attributes = 0;
        SafeWaitHandle? job = null;
        bool started = false;
        try
        {
            if (!CreatePipe(out inputRead, out inputWrite, 0, 0) || !CreatePipe(out outputRead, out outputWrite, 0, 0))
            {
                return Failure("CreatePipe");
            }

            int result = CreatePseudoConsole(new Coord(PseudoTerminalChild.Columns, PseudoTerminalChild.Rows), inputRead, outputWrite, 0, out pseudoConsole);
            if (result != 0)
            {
                return Result<ILoginChild, string>.Failure("could not create a pseudoconsole (HRESULT 0x" + result.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + ")");
            }

            nint size = 0;
            _ = InitializeProcThreadAttributeList(0, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size)
                || !UpdateProcThreadAttribute(attributes, 0, ProcThreadAttributePseudoConsole, pseudoConsole, nint.Size, 0, 0))
            {
                return Failure("UpdateProcThreadAttribute");
            }

            job = CreateJobObjectW(0, null);
            if (job.IsInvalid || !KillJobOnClose(job))
            {
                return Failure("CreateJobObject");
            }

            unsafe
            {
                StartupInfoEx startup = default;
                startup.StartupInfo.cb = sizeof(StartupInfoEx);
                startup.StartupInfo.dwFlags = StartfUseStdHandles;
                startup.lpAttributeList = attributes;

                char[] commandLine = [.. CommandLine(cli), '\0'];
                char[] environment = EnvironmentBlock(cli);
                ProcessInformation information;
                fixed (char* command = commandLine)
                fixed (char* block = environment)
                {
                    if (!CreateProcessW(
                        cli.FileName,
                        command,
                        0,
                        0,
                        false,
                        ExtendedStartupInfoPresent | CreateUnicodeEnvironment | CreateSuspended,
                        block,
                        null,
                        &startup,
                        &information))
                    {
                        return Failure("CreateProcess");
                    }
                }

                SafeWaitHandle process = new(information.hProcess, ownsHandle: true);
                using SafeWaitHandle thread = new(information.hThread, ownsHandle: true);
                if (!AssignProcessToJobObject(job, process))
                {
                    // Read before the cleanup calls overwrite it.
                    int error = Marshal.GetLastPInvokeError();
                    _ = TerminateProcess(process, 1);
                    process.Dispose();
                    return Failure("AssignProcessToJobObject", error);
                }

                if (ResumeThread(thread) == uint.MaxValue)
                {
                    int error = Marshal.GetLastPInvokeError();
                    _ = TerminateJobObject(job, 1);
                    process.Dispose();
                    return Failure("ResumeThread", error);
                }

                // The child holds the pseudoconsole's ends now; this process must not.
                inputRead.Dispose();
                outputWrite.Dispose();
                started = true;
                return Result<ILoginChild, string>.Success(new WindowsPseudoConsoleChild(pseudoConsole, inputWrite, outputRead, process, job));
            }
        }
        finally
        {
            if (attributes != 0)
            {
                DeleteProcThreadAttributeList(attributes);
                Marshal.FreeHGlobal(attributes);
            }

            if (!started)
            {
                // The output's read end goes first: before Windows 11 24H2 the
                // close can wait to hand over a last frame, and nothing reads here.
                outputRead?.Dispose();
                if (pseudoConsole != 0)
                {
                    ClosePseudoConsole(pseudoConsole);
                }

                inputRead?.Dispose();
                inputWrite?.Dispose();
                outputWrite?.Dispose();
                job?.Dispose();
            }
        }
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
        // A carriage return is the Enter key at a console; the pseudoconsole
        // turns these bytes into the key presses the child reads.
        byte[] keys = Encoding.UTF8.GetBytes(code + "\r");
        await Task.Run(() => { _input.Write(keys); _input.Flush(); }, cancellationToken);
    }

    public void Kill()
    {
        try
        {
            _ = TerminateJobObject(_job, 1);
        }
        catch (ObjectDisposedException)
        {
            // The child already ended and the waiter closed the job.
        }
    }

    public void Dispose()
    {
        Kill();
        _outputPipe.Dispose();
        // The waiter closes the pseudoconsole once the child is gone; this waits
        // for neither, so a shutdown is never held by a pre-24H2 close.
        _input.Dispose();
    }

    /// <summary>The command line CreateProcess takes: the shim's prepared one, or each argument quoted by the usual rules.</summary>
    internal static string CommandLine(ProcessStartInfo cli)
    {
        ArgumentNullException.ThrowIfNull(cli);
        StringBuilder line = new();
        AppendArgument(line, cli.FileName);
        if (cli.ArgumentList.Count == 0)
        {
            if (!string.IsNullOrEmpty(cli.Arguments))
            {
                line.Append(' ').Append(cli.Arguments);
            }

            return line.ToString();
        }

        foreach (string argument in cli.ArgumentList)
        {
            line.Append(' ');
            AppendArgument(line, argument);
        }

        return line.ToString();
    }

    /// <summary>
    /// One argument as the C runtime's command-line parser will read it back:
    /// quoted when it holds a space, tab or quote, with backslashes doubled
    /// only where they precede a quote.
    /// </summary>
    private static void AppendArgument(StringBuilder line, string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '"']) < 0)
        {
            line.Append(argument);
            return;
        }

        line.Append('"');
        int backslashes = 0;
        foreach (char character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                line.Append('\\', (backslashes * 2) + 1).Append('"');
            }
            else
            {
                line.Append('\\', backslashes).Append(character);
            }

            backslashes = 0;
        }

        line.Append('\\', backslashes * 2).Append('"');
    }

    /// <summary>The environment block: <c>name=value</c> strings sorted by name, each ended by a null, then one more.</summary>
    private static char[] EnvironmentBlock(ProcessStartInfo cli)
    {
        StringBuilder block = new();
        foreach (KeyValuePair<string, string?> variable in cli.Environment.Where(static pair => pair.Value is not null).OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            block.Append(variable.Key).Append('=').Append(variable.Value).Append('\0');
        }

        block.Append('\0');
        return block.ToString().ToCharArray();
    }

    private void ReadOutput()
    {
        byte[] buffer = new byte[BufferSize];
        char[] characters = new char[BufferSize + 4];
        Decoder decoder = Encoding.UTF8.GetDecoder();
        try
        {
            int read;
            while ((read = _outputPipe.Read(buffer)) > 0)
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
            // The pseudoconsole closed, which breaks the pipe: the end of output.
        }
        finally
        {
            _output.Writer.TryComplete();
            _outputPipe.Dispose();
        }
    }

    /// <summary>
    /// Waits for the child to end, however it ends, then closes the
    /// pseudoconsole. The output pipe stays open until that close, so this is
    /// what lets the reader finish; and it runs here, never on the reader,
    /// because before Windows 11 24H2 the close waits on clients the reader
    /// may be needed to drain.
    /// </summary>
    private void CloseWhenExited()
    {
        try
        {
            _ = WaitForSingleObject(_process, Infinite);
        }
        finally
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0)
            {
                ClosePseudoConsole(_pseudoConsole);
                _process.Dispose();
                _job.Dispose();
            }
        }
    }

    private static bool KillJobOnClose(SafeWaitHandle job)
    {
        JobObjectExtendedLimitInformation limits = default;
        limits.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
        unsafe
        {
            return SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, &limits, (uint)sizeof(JobObjectExtendedLimitInformation));
        }
    }

    private static Result<ILoginChild, string> Failure(string call) => Failure(call, Marshal.GetLastPInvokeError());

    private static Result<ILoginChild, string> Failure(string call, int error) =>
        Result<ILoginChild, string>.Failure("could not start the CLI under a pseudoconsole (" + call + " failed with error " + error + ")");

    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateSuspended = 0x00000004;
    private const int StartfUseStdHandles = 0x00000100;
    private const nint ProcThreadAttributePseudoConsole = 0x00020016;
    private const uint Infinite = 0xFFFFFFFF;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const int JobObjectExtendedLimitInformationClass = 9;

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Coord(short x, short y)
    {
        public readonly short X = x;
        public readonly short Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int cb;
        public nint lpReserved;
        public nint lpDesktop;
        public nint lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public nint lpReserved2;
        public nint hStdInput;
        public nint hStdOutput;
        public nint hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public nint lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint hProcess;
        public nint hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreatePipe(out SafeFileHandle readPipe, out SafeFileHandle writePipe, nint attributes, int size);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out nint pseudoConsole);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void ClosePseudoConsole(nint pseudoConsole);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateProcThreadAttribute(nint list, uint flags, nint attribute, nint value, nint size, nint previousValue, nint returnSize);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void DeleteProcThreadAttributeList(nint list);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CreateProcessW(
        string applicationName,
        char* commandLine,
        nint processAttributes,
        nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        char* environment,
        string? currentDirectory,
        StartupInfoEx* startupInfo,
        ProcessInformation* processInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint ResumeThread(SafeWaitHandle thread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(SafeWaitHandle process, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial SafeWaitHandle CreateJobObjectW(nint attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetInformationJobObject(SafeWaitHandle job, int informationClass, void* information, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(SafeWaitHandle job, SafeWaitHandle process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateJobObject(SafeWaitHandle job, uint exitCode);
}
