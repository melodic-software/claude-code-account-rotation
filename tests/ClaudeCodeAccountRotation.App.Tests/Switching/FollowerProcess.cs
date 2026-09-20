using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Security;
using ClaudeCodeAccountRotation.App.Switching;

namespace ClaudeCodeAccountRotation.App.Tests.Switching;

/// <summary>
/// A real follower, out of process, over a temp layout.
/// <para>
/// Crash injection needs a process that actually dies. An in-process abort
/// would still run the <c>finally</c> blocks that release the lock, delete the
/// staging file and clear the journal — which is exactly the code a crash
/// skips, and therefore exactly the code the crash table exists to stand in
/// for. So these tests launch the published entry point, let it kill itself
/// with <see cref="Process.Kill()"/> at the injected step, and then launch a
/// second one over the same roots to reconcile.
/// </para>
/// </summary>
internal sealed class FollowerProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly StringBuilder _output = new();

    private FollowerProcess(Process process, int port)
    {
        _process = process;
        Port = port;
    }

    public int Port { get; }

    public bool HasExited => _process.HasExited;

    public int ExitCode => _process.ExitCode;

    public string Output => _output.ToString();

    /// <summary>
    /// The app's own entry point, beside the test assembly: a project reference
    /// copies the apphost into this output directory, so nothing has to be
    /// published to run one.
    /// </summary>
    public static string ExecutablePath
    {
        get
        {
            string directory = AppContext.BaseDirectory;
            string name = OperatingSystem.IsWindows() ? "claude-code-account-rotation.exe" : "claude-code-account-rotation";
            return Path.Combine(directory, name);
        }
    }

    public static async Task<FollowerProcess> StartAsync(string configPath, int port, string? failAfterStep, CancellationToken cancellationToken)
    {
        ProcessStartInfo start = new(ExecutablePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        start.ArgumentList.Add("--config");
        start.ArgumentList.Add(configPath);
        start.ArgumentList.Add("--port");
        start.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.Environment[CrashInjection.EnvironmentVariableName] = failAfterStep ?? string.Empty;
        // The launcher must not inherit this developer's own live directory.
        start.Environment["CLAUDE_CONFIG_DIR"] = Path.GetDirectoryName(configPath)!;

        Process process = Process.Start(start) ?? throw new InvalidOperationException("the follower did not start");
        FollowerProcess follower = new(process, port);
        process.OutputDataReceived += follower.Collect;
        process.ErrorDataReceived += follower.Collect;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await follower.WaitUntilServingAsync(cancellationToken);
        return follower;
    }

    /// <summary>A loopback port nothing is listening on right now.</summary>
    public static int FreePort()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public HttpClient Client()
    {
        HttpClient client = new() { BaseAddress = new Uri("http://127.0.0.1:" + Port.ToString(System.Globalization.CultureInfo.InvariantCulture)), Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.Add(SameOriginMutationFilter.HeaderName, "1");
        return client;
    }

    /// <summary>
    /// A request whose answer may never arrive because the process kills itself
    /// while serving it. Either outcome is a pass for the caller; what the test
    /// asserts is the process's exit and what is on disk.
    /// </summary>
    public static async Task<HttpResponseMessage?> PostOrDieAsync(HttpClient client, string route, JsonObject body, CancellationToken cancellationToken)
    {
        using StringContent content = new(body.ToJsonString(), Encoding.UTF8, "application/json");
        try
        {
            return await client.PostAsync(new Uri(route, UriKind.Relative), content, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException)
        {
            return null;
        }
    }

    public async Task<bool> WaitForExitAsync(TimeSpan bound, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(bound);
        try
        {
            await _process.WaitForExitAsync(timeout.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private void Collect(object sender, DataReceivedEventArgs eventArgs)
    {
        if (eventArgs.Data is string line)
        {
            lock (_output)
            {
                _output.AppendLine(line);
            }
        }
    }

    private async Task WaitUntilServingAsync(CancellationToken cancellationToken)
    {
        using HttpClient client = new() { BaseAddress = new Uri("http://127.0.0.1:" + Port.ToString(System.Globalization.CultureInfo.InvariantCulture)), Timeout = TimeSpan.FromSeconds(5) };
        DateTime deadline = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < deadline)
        {
            if (_process.HasExited)
            {
                throw new InvalidOperationException("the follower exited before it served: " + Output);
            }

            try
            {
                using HttpResponseMessage response = await client.GetAsync(new Uri("/healthz", UriKind.Relative), cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Kestrel is not listening yet.
            }
            catch (TaskCanceledException)
            {
                // The probe timed out; try again until the deadline.
            }

            await Task.Delay(50, cancellationToken);
        }

        throw new TimeoutException("the follower never answered /healthz: " + Output);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync(CancellationToken.None);
        }

        _process.Dispose();
    }
}
