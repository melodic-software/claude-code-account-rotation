using System.Diagnostics;
using System.Threading.Channels;
using ClaudeCodeAccountRotation.Core;

namespace ClaudeCodeAccountRotation.App.Adapters.Process;

/// <summary>
/// The running <c>claude auth login</c> process, seen only as "text comes out,
/// a code goes in, it can be killed". The login runner talks to this and never
/// to <see cref="System.Diagnostics.Process"/>, so its own behavior is tested
/// against a scripted child rather than a real CLI.
/// </summary>
internal interface ILoginChild : IDisposable
{
    /// <summary>The next piece of the child's output, or null once it has ended and exited.</summary>
    Task<string?> ReadAsync(CancellationToken cancellationToken);

    /// <summary>Writes the one-time code and a newline to the child's standard input.</summary>
    Task WriteCodeAsync(string code, CancellationToken cancellationToken);

    void Kill();
}

/// <summary>Starts a login child with the given argument list under the given config directory.</summary>
internal delegate Result<ILoginChild, string> LoginChildFactory(IReadOnlyList<string> arguments, string configDirectory);

/// <summary>
/// A real <c>claude auth login</c> process with all three streams piped, so the
/// sign-in URL can be read and the one-time code written without a terminal.
/// <para>
/// Standard output and standard error are merged into one channel and drained
/// continuously. A redirected pipe nobody reads fills and blocks the child,
/// which the operator would see as a hang right after pasting the code.
/// </para>
/// </summary>
internal sealed class ProcessLoginChild : ILoginChild
{
    private const int BufferSize = 1024;

    private readonly System.Diagnostics.Process _process;
    private readonly Channel<string> _output = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });

    private ProcessLoginChild(System.Diagnostics.Process process)
    {
        _process = process;
        _ = DrainAsync();
    }

    /// <summary>The factory the app runs with: a real child, or the reason none could be started.</summary>
    public static LoginChildFactory Factory(ClaudeExecutable executable)
    {
        ArgumentNullException.ThrowIfNull(executable);
        return (arguments, configDirectory) =>
        {
            ProcessStartInfo startInfo = executable.StartInfo(arguments, configDirectory);
            startInfo.RedirectStandardInput = true;
            System.Diagnostics.Process process = new() { StartInfo = startInfo };
            try
            {
                process.Start();
            }
            catch (System.ComponentModel.Win32Exception exception)
            {
                process.Dispose();
                return Result<ILoginChild, string>.Failure("could not start " + executable.FileName + ": " + exception.Message);
            }

            return Result<ILoginChild, string>.Success(new ProcessLoginChild(process));
        };
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
        // A bare newline, not Environment.NewLine: the prompt reads one piped
        // line, and a carriage return would ride along inside the code.
        await _process.StandardInput.WriteAsync((code + "\n").AsMemory(), cancellationToken);
        await _process.StandardInput.FlushAsync(cancellationToken);
    }

    public void Kill() => ChildProcess.TryKill(_process);

    public void Dispose()
    {
        ChildProcess.TryKill(_process);
        _process.Dispose();
    }

    private async Task DrainAsync()
    {
        try
        {
            await Task.WhenAll(PipeAsync(_process.StandardOutput), PipeAsync(_process.StandardError));
            await _process.WaitForExitAsync();
        }
        catch (IOException)
        {
            // A killed child closes its pipes mid-read; the completion below is the signal.
        }
        catch (InvalidOperationException)
        {
            // The process object was disposed while draining.
        }
        finally
        {
            _output.Writer.TryComplete();
        }
    }

    private async Task PipeAsync(StreamReader stream)
    {
        char[] buffer = new char[BufferSize];
        while (true)
        {
            int read = await stream.ReadAsync(buffer);
            if (read <= 0)
            {
                return;
            }

            // The prompt arrives without a newline, so this reads characters
            // rather than lines: waiting for a line ending would hide it.
            _output.Writer.TryWrite(new string(buffer, 0, read));
        }
    }
}

/// <summary>Killing a child process the tool started, whatever state it is in.</summary>
internal static class ChildProcess
{
    public static void TryKill(System.Diagnostics.Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited, or never started.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Could not be killed; nothing more to do here.
        }
    }
}
