using System.Net;
using System.Net.Sockets;
using ClaudeCodeAccountRotation.App.Adapters.Process;
using ClaudeCodeAccountRotation.App.Hosting;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Configuration;

namespace ClaudeCodeAccountRotation.App.Tests.Hosting;

public sealed class DetachedLeaderTests : IDisposable
{
    private const string Token = "secret-token-value";
    private static readonly TimeSpan _bound = TimeSpan.FromSeconds(10);
    private readonly string _appData = Path.Combine(Path.GetTempPath(), "claude-code-account-rotation-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeChild _child = new();
    private IReadOnlyList<string>? _started;

    public DetachedLeaderTests() => Directory.CreateDirectory(_appData);

    public void Dispose()
    {
        _child.Dispose();
        Directory.Delete(_appData, recursive: true);
    }

    [Theory]
    [InlineData(true, true, RotationRole.Leader, true)]
    [InlineData(false, true, RotationRole.Leader, false)]
    [InlineData(true, false, RotationRole.Leader, false)]
    [InlineData(true, true, RotationRole.Follower, false)]
    [InlineData(false, false, RotationRole.Follower, false)]
    public void OnlyOpenOnWindowsAsLeaderDetaches(bool open, bool isWindows, RotationRole role, bool expected) =>
        DetachedLeader.Applies(open, isWindows, role).ShouldBe(expected);

    [Fact]
    public void TheChildGetsEveryArgumentButOpenInOrderSoItDoesNotDetachAgain() =>
        DetachedLeader.ChildArguments(["--open", "--config", "c.json", "--port", "0", "--open", "--urls", "x"])
            .ShouldBe(["--config", "c.json", "--port", "0", "--urls", "x"]);

    [Fact]
    public async Task AnUnboundUrlIsWaitedOutUntilTheTokenIsAccepted()
    {
        WriteInstanceFile("http://127.0.0.1:0");
        int probes = 0;
        Result<(string Url, string Token), string> result = await StartAsync((url, token, _) =>
        {
            if (++probes == 2)
            {
                WriteInstanceFile("http://127.0.0.1:50123");
            }

            return Task.FromResult(url == "http://127.0.0.1:50123" && token == Token);
        });

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(("http://127.0.0.1:50123", Token));
        _started.ShouldBe(["--config", "c.json"]);
        _child.Killed.ShouldBeFalse();
        _child.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task AChildThatExitsFailsWithItsExitCode()
    {
        _child.Code = 1;
        Result<(string Url, string Token), string> result = await StartAsync(static (_, _, _) => Task.FromResult(false));

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldContain("exited with code 1");
    }

    [Fact]
    public async Task AnAnswerAfterTheChildExitedIsNotTheChild()
    {
        WriteInstanceFile("http://127.0.0.1:50123");
        Result<(string Url, string Token), string> result = await StartAsync((_, _, _) =>
        {
            _child.Code = 3;
            return Task.FromResult(true);
        });

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldContain("exited with code 3");
        result.Error.ShouldNotContain(Token);
    }

    [Fact]
    public async Task ATimeoutFailsAndStopsTheChild()
    {
        WriteInstanceFile("http://127.0.0.1:50123");
        Result<(string Url, string Token), string> result = await DetachedLeader.StartAsync(
            Start, ["--open"], _appData, static (_, _, _) => Task.FromResult(false), TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldContain("did not start listening");
        result.Error.ShouldNotContain(Token);
        _child.Killed.ShouldBeTrue();
    }

    [Fact]
    public async Task AChildThatCannotStartFails()
    {
        Result<(string Url, string Token), string> result = await DetachedLeader.StartAsync(
            static _ => Result<ILeaderChild, string>.Failure("could not start the leader: nope"), [], _appData, static (_, _, _) => Task.FromResult(true), _bound, TestContext.Current.CancellationToken);

        result.Error.ShouldBe("could not start the leader: nope");
    }

    [Fact]
    public void ATakenPortIsNamed()
    {
        using TcpListener held = new(IPAddress.Loopback, 0);
        held.Start();
        int port = ((IPEndPoint)held.LocalEndpoint).Port;

        DetachedLeader.PortInUse(port).ShouldNotBeNull().ShouldContain(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        DetachedLeader.PortInUse(0).ShouldBeNull();
    }

    private Task<Result<(string Url, string Token), string>> StartAsync(LeaderProbe probe) =>
        DetachedLeader.StartAsync(Start, ["--open", "--config", "c.json"], _appData, probe, _bound, TestContext.Current.CancellationToken);

    private Result<ILeaderChild, string> Start(IReadOnlyList<string> arguments)
    {
        _started = arguments;
        return Result<ILeaderChild, string>.Success(_child);
    }

    private void WriteInstanceFile(string url) =>
        File.WriteAllText(Path.Combine(_appData, "instance.url"), url + "\n" + Token + "\n");

    private sealed class FakeChild : ILeaderChild
    {
        public int? Code { get; set; }

        public bool Killed { get; private set; }

        public bool Disposed { get; private set; }

        public int? ExitCode => Code;

        public void Kill() => Killed = true;

        public void Dispose() => Disposed = true;
    }
}
