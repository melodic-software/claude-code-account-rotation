using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.App.Adapters.Process;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Accounts;
using ClaudeCodeAccountRotation.Core.Identity;

namespace ClaudeCodeAccountRotation.App.Tests.Adapters;

/// <summary>
/// The CI token runner against a scripted <c>setup-token</c>: where the token
/// goes, where it never goes, and every way a session fails closed.
/// </summary>
public sealed class ClaudeCliSetupTokenRunnerTests : IDisposable
{
    private const string Account = "ci@example.com";

    private readonly string _appData = Path.Combine(Path.GetTempPath(), "claude-code-account-rotation-tests", Guid.NewGuid().ToString("N"));
    private readonly SetupTokenScript _script = new();
    private readonly RecordingSecretWriter _secrets = new();
    private readonly RosterFile _roster;
    private readonly RecordingLogger<ClaudeCliSetupTokenRunner> _logger = new();

    public ClaudeCliSetupTokenRunnerTests()
    {
        Directory.CreateDirectory(_appData);
        _roster = new RosterFile(_appData);
    }

    public void Dispose()
    {
        _roster.Dispose();
        Directory.Delete(_appData, recursive: true);
    }

    private static AccountEmail Email(string value) => AccountEmail.Parse(value).Value;

    private static CiTokenSecret RepoSecret => CiTokenSecret.Parse("CLAUDE_CODE_OAUTH_TOKEN", "octo/lane-one", null).Value;

    private static CiTokenRequest Request(CiTokenSecret? secret = null) => new(Email(Account), secret ?? RepoSecret, null, []);

    private ClaudeCliSetupTokenRunner Runner(TimeProvider? clock = null) =>
        new(_script.Start, _secrets, _roster, clock ?? TimeProvider.System, _logger, TimeSpan.FromSeconds(10));

    private async Task OnRosterAsync() =>
        await _roster.UpdateAsync(roster => roster.With(new RosterEntry(Email(Account))), TestContext.Current.CancellationToken);

    [Fact]
    public async Task ATokenGoesToTheSecretWriterAndNowhereElse()
    {
        await OnRosterAsync();
        using ClaudeCliSetupTokenRunner runner = Runner();

        Result<LoginSession, string> started = await runner.StartAsync(Request(), TestContext.Current.CancellationToken);
        started.IsSuccess.ShouldBeTrue();
        started.Value.SignInUrl!.AbsoluteUri.ShouldBe(SetupTokenScript.SignInUrl);
        _script.Last.Arguments.ShouldBe(["setup-token"]);

        Result<LoginSession, string> submitted = await runner.SubmitCodeAsync(started.Value.Id, "the-code#state", TestContext.Current.CancellationToken);
        await runner.FinishedAsync(started.Value.Id);

        submitted.Value.State.ShouldBe(LoginSessionState.Completed);
        _script.Last.CodesWritten.ShouldBe(["the-code#state"]);
        _script.Last.Killed.ShouldBeTrue();
        _secrets.Sets.Count.ShouldBe(1);
        _secrets.Sets[0].Value.ShouldBe(SetupTokenScript.Token);
        _secrets.Sets[0].Secret.ShouldBe(RepoSecret);

        LoginSession status = runner.Status(started.Value.Id)!;
        status.Message!.ShouldNotContain("sk-ant");
        _logger.Lines.ShouldAllBe(line => !line.Contains("sk-ant", StringComparison.Ordinal) && !line.Contains("the-code", StringComparison.Ordinal));
        Directory.Exists(_script.Last.ConfigDirectory).ShouldBeFalse();
    }

    [Fact]
    public async Task ASetSecretMarksTheAccountOnTheRoster()
    {
        await OnRosterAsync();
        using ClaudeCliSetupTokenRunner runner = Runner();

        LoginSession started = (await runner.StartAsync(Request(), TestContext.Current.CancellationToken)).Value;
        await runner.SubmitCodeAsync(started.Id, "code#state", TestContext.Current.CancellationToken);
        await runner.FinishedAsync(started.Id);

        RosterEntry entry = (await _roster.ReadAsync(TestContext.Current.CancellationToken)).Find(Email(Account))!;
        entry.CiTokenSecret.ShouldBe(RepoSecret);
        entry.CiTokenGeneratedOn.ShouldBe(DateOnly.FromDateTime(DateTime.Now));
    }

    [Fact]
    public async Task AWriterThatCannotSetSecretsStopsTheSessionBeforeAnyTokenIsMade()
    {
        _secrets.CheckError = "the GitHub CLI is not signed in; run `gh auth login` first";
        using ClaudeCliSetupTokenRunner runner = Runner();

        Result<LoginSession, string> started = await runner.StartAsync(Request(), TestContext.Current.CancellationToken);

        started.IsFailure.ShouldBeTrue();
        started.Error.ShouldContain("gh auth login");
        _script.Children.ShouldBeEmpty();
    }

    [Fact]
    public async Task ARejectedCodeEndsTheSessionWithTheManualFallback()
    {
        _script.OnCode = static (child, _) => child.Emit(SetupTokenScript.RetryScreen);
        using ClaudeCliSetupTokenRunner runner = Runner();

        LoginSession started = (await runner.StartAsync(Request(), TestContext.Current.CancellationToken)).Value;
        LoginSession answered = (await runner.SubmitCodeAsync(started.Id, "bad", TestContext.Current.CancellationToken)).Value;
        await runner.FinishedAsync(started.Id);

        answered.State.ShouldBe(LoginSessionState.Failed);
        answered.Message!.ShouldContain("rejected");
        answered.Message!.ShouldContain("claude setup-token");
        _secrets.Sets.ShouldBeEmpty();
        _script.Last.Killed.ShouldBeTrue();
    }

    [Fact]
    public async Task TwoDifferentTokensAreRefusedRatherThanGuessedBetween()
    {
        _script.OnCode = static (child, _) => child.Emit(
            SetupTokenScript.SuccessScreen(SetupTokenScript.Token) + SetupTokenScript.SuccessScreen(SetupTokenScript.Token[..^2] + "BB"));
        using ClaudeCliSetupTokenRunner runner = Runner();

        LoginSession started = (await runner.StartAsync(Request(), TestContext.Current.CancellationToken)).Value;
        LoginSession answered = (await runner.SubmitCodeAsync(started.Id, "code", TestContext.Current.CancellationToken)).Value;

        answered.State.ShouldBe(LoginSessionState.Failed);
        answered.Message!.ShouldContain("more than one token");
        _secrets.Sets.ShouldBeEmpty();
    }

    [Fact]
    public async Task ATokenSplitAcrossReadsIsTakenWholeOnceItsEndArrives()
    {
        string screen = SetupTokenScript.SuccessScreen(SetupTokenScript.Token);
        int cut = screen.IndexOf("sk-ant", StringComparison.Ordinal) + 50;
        _script.OnCode = (child, _) =>
        {
            child.Emit(screen[..cut]);
            child.Emit(screen[cut..]);
        };
        using ClaudeCliSetupTokenRunner runner = Runner();

        LoginSession started = (await runner.StartAsync(Request(), TestContext.Current.CancellationToken)).Value;
        await runner.SubmitCodeAsync(started.Id, "code", TestContext.Current.CancellationToken);
        await runner.FinishedAsync(started.Id);

        _secrets.Sets.Single().Value.ShouldBe(SetupTokenScript.Token);
    }

    [Fact]
    public async Task AChildThatEndsWithoutATokenFailsClosed()
    {
        _script.OnCode = static (child, _) => child.Exit();
        using ClaudeCliSetupTokenRunner runner = Runner();

        LoginSession started = (await runner.StartAsync(Request(), TestContext.Current.CancellationToken)).Value;
        LoginSession answered = (await runner.SubmitCodeAsync(started.Id, "code", TestContext.Current.CancellationToken)).Value;

        answered.State.ShouldBe(LoginSessionState.Failed);
        answered.Message!.ShouldContain("without printing a token");
        _secrets.Sets.ShouldBeEmpty();
    }

    [Fact]
    public async Task AFailedSetSaysTheTokenWasDiscardedAndLeavesTheRosterAlone()
    {
        await OnRosterAsync();
        _secrets.SetError = "gh secret set exited with code 1";
        using ClaudeCliSetupTokenRunner runner = Runner();

        LoginSession started = (await runner.StartAsync(Request(), TestContext.Current.CancellationToken)).Value;
        LoginSession answered = (await runner.SubmitCodeAsync(started.Id, "code", TestContext.Current.CancellationToken)).Value;
        await runner.FinishedAsync(started.Id);

        answered.State.ShouldBe(LoginSessionState.Failed);
        answered.Message!.ShouldContain("not kept anywhere");
        answered.Message!.ShouldNotContain("sk-ant");
        (await _roster.ReadAsync(TestContext.Current.CancellationToken)).Find(Email(Account))!.CiTokenGeneratedOn.ShouldBeNull();
    }

    [Fact]
    public async Task ACliThatPrintsNoUrlFailsTheStart()
    {
        _script.PrintsUrl = false;
        _script.OnCode = static (_, _) => { };
        TestClock clock = new(DateTimeOffset.UtcNow);
        using ClaudeCliSetupTokenRunner runner = new(_script.Start, _secrets, _roster, TimeProvider.System, _logger, TimeSpan.FromMilliseconds(200));

        Result<LoginSession, string> started = await runner.StartAsync(Request(), TestContext.Current.CancellationToken);

        started.IsFailure.ShouldBeTrue();
        started.Error.ShouldContain("no sign-in URL");
        _script.Last.Killed.ShouldBeTrue();
    }

    [Fact]
    public async Task OnlyOneSessionRunsAtATime()
    {
        using ClaudeCliSetupTokenRunner runner = Runner();

        (await runner.StartAsync(Request(), TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
        Result<LoginSession, string> second = await runner.StartAsync(Request(), TestContext.Current.CancellationToken);

        second.IsFailure.ShouldBeTrue();
        second.Error.ShouldContain("already being generated");
        _script.Children.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ASecondCodeIsRefusedWhileTheFirstIsAnswered()
    {
        _script.OnCode = static (_, _) => { };
        using ClaudeCliSetupTokenRunner runner = new(_script.Start, _secrets, _roster, TimeProvider.System, _logger, TimeSpan.FromMilliseconds(100));

        LoginSession started = (await runner.StartAsync(Request(), TestContext.Current.CancellationToken)).Value;
        await runner.SubmitCodeAsync(started.Id, "first", TestContext.Current.CancellationToken);
        Result<LoginSession, string> second = await runner.SubmitCodeAsync(started.Id, "second", TestContext.Current.CancellationToken);

        second.IsFailure.ShouldBeTrue();
        _script.Last.CodesWritten.ShouldBe(["first"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("two\nlines")]
    public async Task ACodeOfTheWrongShapeNeverReachesTheChild(string code)
    {
        using ClaudeCliSetupTokenRunner runner = Runner();

        LoginSession started = (await runner.StartAsync(Request(), TestContext.Current.CancellationToken)).Value;
        Result<LoginSession, string> submitted = await runner.SubmitCodeAsync(started.Id, code, TestContext.Current.CancellationToken);

        submitted.IsFailure.ShouldBeTrue();
        _script.Last.CodesWritten.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnExpiredSessionKillsItsChild()
    {
        _script.OnCode = static (_, _) => { };
        TestClock clock = new(DateTimeOffset.UtcNow);
        using ClaudeCliSetupTokenRunner runner = Runner(clock);

        LoginSession started = (await runner.StartAsync(Request(), TestContext.Current.CancellationToken)).Value;
        clock.Advance(ClaudeCliSetupTokenRunner.SessionLifetime);

        runner.Status(started.Id)!.State.ShouldBe(LoginSessionState.Expired);
        _script.Last.Killed.ShouldBeTrue();
    }

    [Fact]
    public void VisibleTextDropsTerminalControlSequences()
    {
        const char escape = (char)0x1b;
        string rendered = escape + "[2K" + escape + "[1;1H" + escape + "]8;;https://example.com" + (char)0x07 + "link" + escape + "]8;;" + (char)0x07 + escape + "[32mgreen" + escape + "[0m";

        // Erase and cursor-position become row breaks; the hyperlink and color go.
        ClaudeCliSetupTokenRunner.Visible(rendered).ShouldBe("\n\nlinkgreen");
    }

    [Fact]
    public void ATokenWithNothingAfterItYetIsNotTaken() =>
        ClaudeCliSetupTokenRunner.ExtractTokens(SetupTokenScript.Token).ShouldBeEmpty();

    [Fact]
    public void ACursorMoveToTheNextRowIsNotGluedOntoTheToken()
    {
        // A console repaint reaches the next row by moving the cursor, not by a line break.
        const char escape = (char)0x1b;
        string rendered = SetupTokenScript.Token + escape + "[K" + escape + "[12;1HStore this token securely.";

        ClaudeCliSetupTokenRunner.ExtractTokens(ClaudeCliSetupTokenRunner.Visible(rendered)).ShouldBe([SetupTokenScript.Token]);
    }

    [Fact]
    public void ATokenRunIntoOtherTextIsNotTaken() =>
        ClaudeCliSetupTokenRunner.ExtractTokens("xx" + SetupTokenScript.Token + " \n").ShouldBeEmpty();

    [Fact]
    public void ATokenBrokenAcrossRowsLooksWrapped()
    {
        string wrapped = SetupTokenScript.Token[..60] + "\n" + SetupTokenScript.Token[60..] + "\n\nStore this token securely.\n";

        ClaudeCliSetupTokenRunner.AnyTokenLooksWrapped(wrapped).ShouldBeTrue();
        ClaudeCliSetupTokenRunner.AnyTokenLooksWrapped(SetupTokenScript.SuccessScreen(SetupTokenScript.Token)).ShouldBeFalse();
    }

    [Fact]
    public async Task AWrappedTokenIsRefusedRatherThanStoredCutShort()
    {
        string token = SetupTokenScript.Token;
        _script.OnCode = (child, _) => child.Emit("Your OAuth token:\r\n\r\n" + token[..60] + "\r\n" + token[60..] + "\r\n\r\nStore this token securely.\r\n");
        using ClaudeCliSetupTokenRunner runner = Runner();

        LoginSession started = (await runner.StartAsync(Request(), TestContext.Current.CancellationToken)).Value;
        LoginSession answered = (await runner.SubmitCodeAsync(started.Id, "code", TestContext.Current.CancellationToken)).Value;

        answered.State.ShouldBe(LoginSessionState.Failed);
        answered.Message!.ShouldContain("broken across rows");
        _secrets.Sets.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("https://claude.com/cai/oauth/authorize?code=true&scope=user%3Ainference&code_challenge=abc \n", false)]
    [InlineData("https://claude.com/cai/oauth/authorize?code=true&scope=user%3Ainference&state=abc \n", false)]
    [InlineData("https://claude.com/cai/oauth/authorize?code=true&code_challenge=abc&state= \n", false)]
    [InlineData("https://claude.com/cai/oauth/authorize?code=true&code_challenge=abc&state=def \n", true)]
    [InlineData("https://claude.com/cai/oauth/authorize?code=true&code_challenge=abc&state=def", false)]
    public void OnlyAWholeAuthorizeUrlIsTaken(string visible, bool taken) =>
        (ClaudeCliSetupTokenRunner.ExtractAuthorizeUrl(visible) is not null).ShouldBe(taken);

    [Fact]
    public async Task ACancelledStartFreesTheSlotForTheNextOne()
    {
        _script.PrintsUrl = false;
        using ClaudeCliSetupTokenRunner runner = Runner();
        using CancellationTokenSource cancelled = new(TimeSpan.FromMilliseconds(100));

        await Should.ThrowAsync<OperationCanceledException>(() => runner.StartAsync(Request(), cancelled.Token));
        _script.PrintsUrl = true;
        Result<LoginSession, string> next = await runner.StartAsync(Request(), TestContext.Current.CancellationToken);

        _script.Children[0].Killed.ShouldBeTrue();
        next.IsSuccess.ShouldBeTrue(next.IsFailure ? next.Error : null);
    }

    [Fact]
    public async Task ACodeTheChildCanNoLongerTakeFailsTheSession()
    {
        _script.OnCode = static (_, _) => throw new IOException("broken pipe");
        using ClaudeCliSetupTokenRunner runner = Runner();

        LoginSession started = (await runner.StartAsync(Request(), TestContext.Current.CancellationToken)).Value;
        Result<LoginSession, string> submitted = await runner.SubmitCodeAsync(started.Id, "code", TestContext.Current.CancellationToken);

        submitted.IsFailure.ShouldBeTrue();
        runner.Status(started.Id)!.State.ShouldBe(LoginSessionState.Failed);
    }
}
