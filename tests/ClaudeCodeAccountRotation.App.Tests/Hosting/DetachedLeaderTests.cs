using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
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

    public static bool OnWindows => OperatingSystem.IsWindows();

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
        _child.StopRequested.ShouldBeFalse();
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
    public async Task AChildThatLostTheLockToAnotherLaunchOpensTheLeaderThatWon()
    {
        // The other launch's leader holds the lock and wrote the file; the child exited 1 on it.
        _child.Code = 1;
        WriteInstanceFile("http://127.0.0.1:50123");

        Result<(string Url, string Token), string> result = await StartAsync(static (_, token, _) => Task.FromResult(token == Token));

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(("http://127.0.0.1:50123", Token));
    }

    [Fact]
    public async Task AnExitIsReportedOnlyAfterOneMoreProbe()
    {
        WriteInstanceFile("http://127.0.0.1:50123");
        int probes = 0;
        Result<(string Url, string Token), string> result = await StartAsync((_, _, _) =>
        {
            probes++;
            _child.Code = 3;
            return Task.FromResult(false);
        });

        probes.ShouldBe(2);
        result.Error.ShouldContain("exited with code 3");
        result.Error.ShouldNotContain(Token);
    }

    [Theory]
    [InlineData(true, "was stopped")]
    [InlineData(false, "is still running as process 4242")]
    public async Task ATimeoutStopsTheChildAndSaysWhetherItDid(bool stops, string expected)
    {
        _child.Stops = stops;
        WriteInstanceFile("http://127.0.0.1:50123");
        Result<(string Url, string Token), string> result = await DetachedLeader.StartAsync(
            Start, ["--open"], _appData, static (_, _, _) => Task.FromResult(false), TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldContain("did not start listening");
        result.Error.ShouldContain(expected);
        result.Error.ShouldNotContain(Token);
        _child.StopRequested.ShouldBeTrue();
    }

    [Fact(SkipUnless = nameof(OnWindows), Skip = "Handle inheritance is a Windows behavior")]
    [SupportedOSPlatform("windows")]
    public void AnInheritableHandleStopsBeingInherited()
    {
        using AnonymousPipeServerStream pipe = new(PipeDirection.Out, HandleInheritability.Inheritable);
        nint handle = pipe.SafePipeHandle.DangerousGetHandle();
        ProcessLeaderChild.IsInheritable(handle).ShouldBeTrue();

        ProcessLeaderChild.StopInheriting(handle).ShouldBeTrue();

        ProcessLeaderChild.IsInheritable(handle).ShouldBeFalse();
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

        public bool Stops { get; set; } = true;

        public bool StopRequested { get; private set; }

        public bool Disposed { get; private set; }

        public int? ExitCode => Code;

        public int Id => 4242;

        public bool Stop(TimeSpan wait)
        {
            StopRequested = true;
            return Stops;
        }

        public void Dispose() => Disposed = true;
    }
}
