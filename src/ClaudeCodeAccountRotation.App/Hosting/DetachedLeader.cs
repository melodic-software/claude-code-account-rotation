using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using ClaudeCodeAccountRotation.App.Adapters.Process;
using ClaudeCodeAccountRotation.App.Switching;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Configuration;

namespace ClaudeCodeAccountRotation.App.Hosting;

/// <summary>Whether a leader answers at a URL with a token: true only when that token is its own.</summary>
internal delegate Task<bool> LeaderProbe(string url, string token, CancellationToken cancellationToken);

/// <summary>
/// <c>--open</c> on Windows with no instance running: the leader is started as
/// its own windowless process that outlives this one, and this process waits
/// for it to listen, then opens its page and exits.
/// </summary>
internal static class DetachedLeader
{
    /// <summary>How long a started leader has to answer. A judgment: startup is normally well under a few seconds.</summary>
    public static readonly TimeSpan StartBound = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan _pollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Windows only, where the console window is the problem. Elsewhere the
    /// terminal owns the process group, and a follower has no page to open.
    /// </summary>
    public static bool Applies(bool open, bool isWindows, RotationRole role) => open && isWindows && role == RotationRole.Leader;

    /// <summary>The command line minus <c>--open</c>, so the child runs in the foreground of its own hidden console rather than starting another.</summary>
    public static IReadOnlyList<string> ChildArguments(IReadOnlyList<string> arguments) =>
        [.. arguments.Where(static argument => argument != "--open")];

    /// <summary>Why the child could not listen on <paramref name="port"/>, or null when the port is free now.</summary>
    public static string? PortInUse(int port)
    {
        if (port == 0)
        {
            return null;
        }

        TcpListener listener = new(IPAddress.Loopback, port);
        try
        {
            listener.Start();
            return null;
        }
        catch (SocketException)
        {
            return "port " + port.ToString(System.Globalization.CultureInfo.InvariantCulture) + " is in use; free it, or pass --port with another number";
        }
        finally
        {
            listener.Stop();
            listener.Dispose();
        }
    }

    /// <summary>
    /// Starts the child and waits until the instance file it writes names a
    /// listener that accepts that file's token. The token is returned, never
    /// put in a message.
    /// </summary>
    public static async Task<Result<(string Url, string Token), string>> StartAsync(
        LeaderChildFactory start,
        IReadOnlyList<string> arguments,
        string appDataDirectory,
        LeaderProbe probe,
        TimeSpan bound,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(probe);
        Result<ILeaderChild, string> started = start(ChildArguments(arguments));
        if (started.IsFailure)
        {
            return Result<(string, string), string>.Failure(started.Error);
        }

        using ILeaderChild child = started.Value;
        using CancellationTokenSource timeout = new(bound);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        try
        {
            while (true)
            {
                if (child.ExitCode is int code)
                {
                    return Result<(string, string), string>.Failure(Exited(code));
                }

                if (InstanceLock.ReadRunning(appDataDirectory) is var (url, token) && await probe(url, token, linked.Token))
                {
                    // Checked again: a probe that answered as the child exited was not the child.
                    return child.ExitCode is int late
                        ? Result<(string, string), string>.Failure(Exited(late))
                        : Result<(string, string), string>.Success((url, token));
                }

                await Task.Delay(_pollInterval, linked.Token);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            child.Kill();
            return Result<(string, string), string>.Failure(
                "the leader did not start listening within " + bound.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + " s and was stopped; run without --open to see its output");
        }
    }

    /// <summary>
    /// A token-gated, leader-only read that touches no peer: a listener that is
    /// not this instance refuses the token or has no such route, and a follower
    /// still starting cannot hold the answer up.
    /// </summary>
    public static async Task<bool> ProbeAsync(string url, string token, CancellationToken cancellationToken)
    {
        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(5) };
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(url + "/api/browser-profiles"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private static string Exited(int code) =>
        "the leader exited with code " + code.ToString(System.Globalization.CultureInfo.InvariantCulture) + " before it was listening; run without --open to see why";
}
