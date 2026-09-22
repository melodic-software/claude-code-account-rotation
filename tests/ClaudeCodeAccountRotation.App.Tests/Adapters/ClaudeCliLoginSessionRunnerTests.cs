using System.Diagnostics;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.App.Adapters.Process;
using ClaudeCodeAccountRotation.App.Switching;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeCodeAccountRotation.App.Tests.Adapters;

/// <summary>
/// What the runner does when two calls arrive at once, and what it re-reads
/// before it lets a child near a folder. The endpoint tests drive the happy
/// path; these drive the races, so they hold the real runner directly.
/// </summary>
public sealed class ClaudeCliLoginSessionRunnerTests : IDisposable
{
    private const string ParkedEmail = "parked@example.com";
    private const string LiveEmail = "live@example.com";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "claude-code-account-rotation-tests", Guid.NewGuid().ToString("N"));
    private readonly string _profilesRoot;
    private readonly string _stateFilePath;
    private readonly CredentialMutationGate _gate = new();
    private readonly LoginChildScript _script = new();
    private readonly AppFactory.CannedCli _cli = new();

    public ClaudeCliLoginSessionRunnerTests()
    {
        _profilesRoot = Path.Combine(_root, "profiles");
        _stateFilePath = Path.Combine(_root, ".claude.json");
        Directory.CreateDirectory(_profilesRoot);
    }

    private string Folder(string email)
    {
        string folder = Path.Combine(_profilesRoot, email);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static AccountEmail Email(string value) => AccountEmail.Parse(value).Value;

    private ClaudeCliLoginSessionRunner Runner(
        LoginChildFactory? start = null,
        TimeProvider? clock = null,
        ILogger<ClaudeCliLoginSessionRunner>? logger = null) => new(
        start ?? _script.Start,
        new ProfileFolderStore(_profilesRoot),
        new ClaudeStateFile(_stateFilePath),
        _gate,
        _cli,
        _cli,
        clock ?? TimeProvider.System,
        logger ?? NullLogger<ClaudeCliLoginSessionRunner>.Instance);

    private async Task WriteStateFileAsync(string email) =>
        await File.WriteAllTextAsync(
            _stateFilePath,
            new JsonObject { ["numStartups"] = 1, ["oauthAccount"] = AppFactory.AccountJson(email) }.ToJsonString(),
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task TwoStartsAgainstOneFolderSpawnOneChild()
    {
        // A double-submit from the page. The check for a login already running and
        // the registration that makes this one visible to it have to be one step,
        // or both calls pass the check and two CLIs write one CLAUDE_CONFIG_DIR.
        using ManualResetEventSlim insideTheFactory = new();
        using ManualResetEventSlim release = new();
        int started = 0;
        using ClaudeCliLoginSessionRunner runner = Runner((arguments, folder) =>
        {
            if (Interlocked.Increment(ref started) == 1)
            {
                insideTheFactory.Set();
                release.Wait(TimeSpan.FromSeconds(30));
            }

            return _script.Start(arguments, folder);
        });
        string folder = Folder(ParkedEmail);

        Task<Result<LoginSession, string>> first = Task.Run(
            () => runner.StartAsync(Email(ParkedEmail), folder, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
        insideTheFactory.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken).ShouldBeTrue();
        Task<Result<LoginSession, string>> second = Task.Run(
            () => runner.StartAsync(Email(ParkedEmail), folder, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
        release.Set();
        Result<LoginSession, string>[] results = await Task.WhenAll(first, second);

        results.Count(static result => result.IsSuccess).ShouldBe(1);
        results.Single(static result => result.IsFailure).Error.ShouldContain("already running");
        started.ShouldBe(1);
        _script.Children.Count.ShouldBe(1);
    }

    [Fact]
    public async Task TheMutationGateIsHeldWhileTheChildIsSpawned()
    {
        // Which is what stops a switch from renaming that folder's pair away while
        // the child is writing a fresh one into it.
        bool gateWasHeld = false;
        using ClaudeCliLoginSessionRunner runner = Runner((arguments, folder) =>
        {
            try
            {
                using IDisposable permit = _gate.AcquireAsync(TimeSpan.Zero, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (TimeoutException)
            {
                gateWasHeld = true;
            }

            return _script.Start(arguments, folder);
        });

        Result<LoginSession, string> started = await runner.StartAsync(Email(ParkedEmail), Folder(ParkedEmail), TestContext.Current.CancellationToken);

        started.IsSuccess.ShouldBeTrue(started.IsFailure ? started.Error : "");
        gateWasHeld.ShouldBeTrue();
    }

    [Fact]
    public async Task AStartIsRefusedWhenTheStateFileNamesThatAccountLive()
    {
        // The endpoint reads the live account once, before the gate. A switch that
        // completes in between would leave this login writing a second pair for the
        // account already live, so the authority is this read, under the gate.
        await WriteStateFileAsync(ParkedEmail);
        using ClaudeCliLoginSessionRunner runner = Runner();

        Result<LoginSession, string> started = await runner.StartAsync(Email(ParkedEmail), Folder(ParkedEmail), TestContext.Current.CancellationToken);

        started.IsFailure.ShouldBeTrue();
        started.Error.ShouldContain("live on this machine");
        _script.Children.ShouldBeEmpty();
    }

    [Fact]
    public async Task AStartIsAllowedWhenAnotherAccountIsLive()
    {
        await WriteStateFileAsync(LiveEmail);
        using ClaudeCliLoginSessionRunner runner = Runner();

        Result<LoginSession, string> started = await runner.StartAsync(Email(ParkedEmail), Folder(ParkedEmail), TestContext.Current.CancellationToken);

        started.IsSuccess.ShouldBeTrue(started.IsFailure ? started.Error : "");
        runner.IsRunningAgainst(Folder(ParkedEmail)).ShouldBeTrue();
        runner.IsRunningAgainst(Folder(LiveEmail)).ShouldBeFalse();
    }

    [Fact]
    public async Task ASecondCodeSubmittedWhileOneIsInFlightIsRefusedRatherThanLosingTheAnswer()
    {
        // The pump answers through one field per session. A second submit that
        // replaced it left the first caller waiting out its whole reply budget for
        // a signal that had already been sent to the field it no longer holds.
        using ManualResetEventSlim writing = new();
        using ManualResetEventSlim release = new();
        _script.OnCode = (child, code) =>
        {
            writing.Set();
            release.Wait(TimeSpan.FromSeconds(30));
            child.Emit("Invalid code. Please make sure the full code was copied.\r\n");
            return Task.CompletedTask;
        };
        using ClaudeCliLoginSessionRunner runner = Runner();
        LoginSession session = (await runner.StartAsync(Email(ParkedEmail), Folder(ParkedEmail), TestContext.Current.CancellationToken)).Value;

        Task<Result<LoginSession, string>> first = Task.Run(
            () => runner.SubmitCodeAsync(session.Id, "111111", TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
        writing.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken).ShouldBeTrue();
        Result<LoginSession, string> second = await runner.SubmitCodeAsync(session.Id, "222222", TestContext.Current.CancellationToken);
        release.Set();
        Result<LoginSession, string> answered = await first;

        second.IsFailure.ShouldBeTrue();
        second.Error.ShouldContain("still checking");
        answered.IsSuccess.ShouldBeTrue(answered.IsFailure ? answered.Error : "");
        answered.Value.Message.ShouldNotBeNull().ShouldContain("rejected");
        _script.Last.CodesWritten.ShouldBe(["111111"]);
    }

    [Fact]
    public async Task AnUnexpectedPumpExceptionFailsTheSessionBeforeItsExpiry()
    {
        // A fault the read loop does not treat as the child ending used to leave
        // the pump task unobserved and the session pending until the ten-minute
        // expiry. The clock is not moved, so a failure here is the fault path.
        var clock = new TestClock(new DateTimeOffset(2026, 9, 22, 0, 0, 0, TimeSpan.Zero));
        RecordingLogger<ClaudeCliLoginSessionRunner> logger = new();
        using ClaudeCliLoginSessionRunner runner = Runner(
            (_, _) => Result<ILoginChild, string>.Success(new FaultingLoginChild()),
            clock,
            logger);
        string folder = Folder(ParkedEmail);

        Result<LoginSession, string> started = await runner.StartAsync(Email(ParkedEmail), folder, TestContext.Current.CancellationToken);

        started.IsSuccess.ShouldBeTrue(started.IsFailure ? started.Error : "");
        await runner.FinishedAsync(started.Value.Id).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        LoginSession status = runner.Status(started.Value.Id).ShouldNotBeNull();
        status.State.ShouldBe(LoginSessionState.Failed);
        status.ExpiresAt.ShouldBeGreaterThan(clock.GetUtcNow());
        status.Message.ShouldNotBeNull().ShouldContain("credential file");
        status.Message.ShouldNotContain(FaultingLoginChild.Sentinel);
        logger.Lines.ShouldContain(line => line.Contains(nameof(NotSupportedException), StringComparison.Ordinal));
    }

    [Fact]
    public async Task DisposeAfterThePumpHasFinishedDoesNotThrowAndCanBeCalledTwice()
    {
        using ClaudeCliLoginSessionRunner runner = Runner();
        Result<LoginSession, string> started = await runner.StartAsync(Email(ParkedEmail), Folder(ParkedEmail), TestContext.Current.CancellationToken);
        started.IsSuccess.ShouldBeTrue(started.IsFailure ? started.Error : "");
        _script.Last.Exit();
        await runner.FinishedAsync(started.Value.Id).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var watch = Stopwatch.StartNew();
        runner.Dispose();
        runner.Dispose();

        watch.Elapsed.ShouldBeLessThan(ClaudeCliLoginSessionRunner.DisposeWait);
    }

    [Fact]
    public async Task DisposeWaitsABoundedTimeForAPumpThatDoesNotFinish()
    {
        // The read ignores cancellation and Kill does not unblock it, so the
        // only way Dispose returns is the bound. A second call must not wait again.
        using var child = new HungLoginChild();
        using ClaudeCliLoginSessionRunner runner = Runner((_, _) => Result<ILoginChild, string>.Success(child));
        Result<LoginSession, string> started = await runner.StartAsync(Email(ParkedEmail), Folder(ParkedEmail), TestContext.Current.CancellationToken);
        started.IsSuccess.ShouldBeTrue(started.IsFailure ? started.Error : "");

        var watch = Stopwatch.StartNew();
        runner.Dispose();
        watch.Elapsed.ShouldBeGreaterThan(ClaudeCliLoginSessionRunner.DisposeWait - TimeSpan.FromMilliseconds(500));
        watch.Elapsed.ShouldBeLessThan(ClaudeCliLoginSessionRunner.DisposeWait + TimeSpan.FromSeconds(10));
        var second = Stopwatch.StartNew();
        runner.Dispose();
        second.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1));
        child.Release();
    }

    [Fact]
    public async Task AFinishedSessionStaysReadableForItsLifetimeAndIsThenEvicted()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 9, 22, 0, 0, 0, TimeSpan.Zero));
        using ClaudeCliLoginSessionRunner runner = Runner(clock: clock);
        Result<LoginSession, string> started = await runner.StartAsync(Email(ParkedEmail), Folder(ParkedEmail), TestContext.Current.CancellationToken);
        started.IsSuccess.ShouldBeTrue(started.IsFailure ? started.Error : "");
        _script.Last.Exit();
        await runner.FinishedAsync(started.Value.Id).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        runner.Status(started.Value.Id).ShouldNotBeNull().State.ShouldBe(LoginSessionState.Failed);

        clock.Advance(ClaudeCliLoginSessionRunner.CompletedSessionRetention - TimeSpan.FromTicks(1));
        runner.EvictFinishedSessions();
        runner.Status(started.Value.Id).ShouldNotBeNull();

        clock.Advance(TimeSpan.FromTicks(1));
        runner.EvictFinishedSessions();
        runner.Status(started.Value.Id).ShouldBeNull();
    }

    [Fact]
    public async Task AFinishedSessionDropsItselfWhenTheWindowEndsAndNobodyAsksAgain()
    {
        // The page does not poll a finished login. The timer armed when the
        // session leaves pending is what drops it; this test never calls the sweep.
        var clock = new CapturingClock(new DateTimeOffset(2026, 9, 22, 0, 0, 0, TimeSpan.Zero));
        using ClaudeCliLoginSessionRunner runner = Runner(clock: clock);
        Result<LoginSession, string> started = await runner.StartAsync(Email(ParkedEmail), Folder(ParkedEmail), TestContext.Current.CancellationToken);
        started.IsSuccess.ShouldBeTrue(started.IsFailure ? started.Error : "");
        int timersAtStart = clock.Created.Count;
        _script.Last.Exit();
        await runner.FinishedAsync(started.Value.Id).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        runner.SessionCount.ShouldBe(1);
        clock.Created.Count.ShouldBe(timersAtStart + 1);
        (TimerCallback callback, _, TimeSpan due) = clock.Created[^1];
        due.ShouldBe(ClaudeCliLoginSessionRunner.CompletedSessionRetention);

        clock.Advance(ClaudeCliLoginSessionRunner.CompletedSessionRetention - TimeSpan.FromTicks(1));
        callback(null);
        runner.SessionCount.ShouldBe(1);

        clock.Advance(TimeSpan.FromTicks(1));
        callback(null);
        runner.SessionCount.ShouldBe(0);
    }

    [Fact]
    public async Task ARunningSessionIsNotEvictedAfterItsLifetime()
    {
        // Still pending when the sweep runs, even though both the login's own
        // lifetime and the readable window after a finish have passed. Expiry
        // is a separate step and happens when something next reads the session.
        var clock = new TestClock(new DateTimeOffset(2026, 9, 22, 0, 0, 0, TimeSpan.Zero));
        using ClaudeCliLoginSessionRunner runner = Runner(clock: clock);
        Result<LoginSession, string> started = await runner.StartAsync(Email(ParkedEmail), Folder(ParkedEmail), TestContext.Current.CancellationToken);
        started.IsSuccess.ShouldBeTrue(started.IsFailure ? started.Error : "");

        clock.Advance(ClaudeCliLoginSessionRunner.SessionLifetime + ClaudeCliLoginSessionRunner.CompletedSessionRetention);
        runner.EvictFinishedSessions();
        LoginSession status = runner.Status(started.Value.Id).ShouldNotBeNull();
        status.State.ShouldBe(LoginSessionState.Expired);
    }

    [Fact]
    public async Task ASessionWhoseReaderIsStillFinishingIsNotEvicted()
    {
        // Expiry settles the session in the request; the folder is finished on
        // the pump afterwards. That pump holds the mutation gate here, so the
        // readable window can elapse while the reader is still in the finish.
        var clock = new TestClock(new DateTimeOffset(2026, 9, 22, 0, 0, 0, TimeSpan.Zero));
        using ClaudeCliLoginSessionRunner runner = Runner(clock: clock);
        Result<LoginSession, string> started = await runner.StartAsync(Email(ParkedEmail), Folder(ParkedEmail), TestContext.Current.CancellationToken);
        started.IsSuccess.ShouldBeTrue(started.IsFailure ? started.Error : "");
        using IDisposable hold = await _gate.AcquireAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        clock.Advance(ClaudeCliLoginSessionRunner.SessionLifetime);
        runner.Status(started.Value.Id).ShouldNotBeNull().State.ShouldBe(LoginSessionState.Expired);
        clock.Advance(ClaudeCliLoginSessionRunner.CompletedSessionRetention);
        runner.EvictFinishedSessions();
        runner.Status(started.Value.Id).ShouldNotBeNull();

        hold.Dispose();
        await runner.FinishedAsync(started.Value.Id).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        runner.EvictFinishedSessions();
        runner.Status(started.Value.Id).ShouldBeNull();
    }

    public void Dispose()
    {
        _gate.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>
    /// Prints the authorize URL, then throws <see cref="NotSupportedException"/> from
    /// the next read and from Kill, which is neither cancellation nor a closed
    /// pipe. A finish that ran only from a <c>finally</c> after Kill would be skipped.
    /// </summary>
    private sealed class FaultingLoginChild : ILoginChild
    {
        public const string Sentinel = "pump-fault-sentinel";

        private int _reads;
        private int _kills;

        public Task<string?> ReadAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _reads) == 1)
            {
                return Task.FromResult<string?>(LoginChildScript.AuthorizeBase + "?code=true");
            }

            throw new NotSupportedException(Sentinel);
        }

        public Task WriteCodeAsync(string code, CancellationToken cancellationToken) => Task.CompletedTask;

        public void Kill()
        {
            // The pump's own kill is the fault. Shutdown kills again on the way
            // out, and that call has to be safe.
            if (Interlocked.Increment(ref _kills) == 1)
            {
                throw new NotSupportedException(Sentinel);
            }
        }

        public void Dispose()
        {
        }
    }

    /// <summary>A read that neither cancellation nor Kill unblocks.</summary>
    private sealed class HungLoginChild : ILoginChild
    {
        private readonly TaskCompletionSource<string?> _hung = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _reads;

        public void Release() => _hung.TrySetResult(null);

        public Task<string?> ReadAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _reads) == 1)
            {
                return Task.FromResult<string?>(LoginChildScript.AuthorizeBase + "?code=true");
            }

            return _hung.Task;
        }

        public Task WriteCodeAsync(string code, CancellationToken cancellationToken) => Task.CompletedTask;

        public void Kill()
        {
        }

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// Records timers instead of waiting on the wall clock. The lifetime source
    /// and the eviction each create one; the test fires the one it cares about.
    /// </summary>
    private sealed class CapturingClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public List<(TimerCallback Callback, object? State, TimeSpan Due)> Created { get; } = [];

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan amount) => _now += amount;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Created.Add((callback, state, dueTime));
            return new IdleTimer();
        }

        private sealed class IdleTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
