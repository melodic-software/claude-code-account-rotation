using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.App.Adapters.Process;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Accounts;
using ClaudeCodeAccountRotation.Core.Identity;

namespace ClaudeCodeAccountRotation.App.Tests.Adapters;

/// <summary>
/// The pseudo-terminal host against real processes, on whichever OS runs the
/// suite: a Node script that, like <c>claude setup-token</c>, refuses to draw
/// anything unless its standard input is a terminal, and the real CLI itself.
/// <para>
/// Node and the CLI are found on PATH. Either one missing skips its tests,
/// unless <c>CCAR_REQUIRE_REAL_CLI_TESTS=1</c> is set, as CI sets it, in which
/// case a missing one is a failure: a check that silently stopped running is
/// worse than one that fails.
/// </para>
/// </summary>
public sealed class PseudoTerminalChildTests : IDisposable
{
    private const string RequireVariable = "CCAR_REQUIRE_REAL_CLI_TESTS";

    // Stands in for setup-token: no terminal on stdin, no screen. Under one, it
    // goes raw, turns on bracketed paste, prints a sign-in URL, masks what is
    // typed, and on Enter prints either the retry screen (for "bad") or a token
    // built from the code. Like the real CLI, it takes one unbracketed read of
    // 64 characters or more as pasted text, Enter included, and does not submit.
    private const string Probe = """
        if (!process.stdin.isTTY) { process.stdout.write("stdin is not a terminal\n"); process.exit(3); }
        process.stdin.setRawMode(true);
        process.stdout.write("\x1b[?2004h\x1b[1mWelcome\x1b[22m\n\n https://claude.com/cai/oauth/authorize?code=true&scope=user%3Ainference&code_challenge=probe&state=probe\n\n Paste code here if prompted > ");
        let code = "";
        let pasting = false;
        let pending = "";
        process.stdin.on("data", (chunk) => {
          let text = pending + chunk.toString("utf8");
          pending = "";
          if (!pasting && !text.includes("\x1b[") && text.length >= 64) {
            code += text.replace(/[\r\n]/g, "");
            process.stdout.write("*".repeat(text.length));
            return;
          }
          // The paste markers, matched across reads: a marker cut at the end of
          // one read waits for the rest of it in the next.
          while (text.length > 0) {
            const marker = pasting ? "\x1b[201~" : "\x1b[200~";
            const at = text.indexOf(marker);
            const plain = at < 0 ? text : text.slice(0, at);
            let cut = 0;
            for (let n = marker.length - 1; at < 0 && n > 0 && cut === 0; n--) {
              if (plain.endsWith(marker.slice(0, n))) cut = n;
            }
            pending = plain.slice(plain.length - cut);
            take(plain.slice(0, plain.length - cut));
            if (at < 0) break;
            pasting = !pasting;
            text = text.slice(at + marker.length);
          }
        });
        function take(text) {
          if (pasting) {
            code += text;
            process.stdout.write("*".repeat(text.length));
            return;
          }
          for (const ch of text) {
            if (ch === "\r" || ch === "\n") {
              if (code === "bad") {
                process.stdout.write("\n OAuth error: Request failed with status code 400\n\n Press Enter to retry.\n");
              } else {
                process.stdout.write("\n Your OAuth token (valid for 1 year):\n\n sk-ant-" + "oat01-" + "Q".repeat(60) + code.replace(/[^A-Za-z0-9_-]/g, "") + "AA\n\n Store this token securely.\n");
              }
              code = "";
            } else {
              code += ch;
              process.stdout.write("*");
            }
          }
        }
        """;

    /// <summary>
    /// The shape and length of a real sign-in code: 86 characters, <c>#</c>, then
    /// the 43-character state. Made up, so it can never sign anything in.
    /// </summary>
    private const string RealLengthCode = "ccar-not-a-real-code-xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx#ccar-state-yyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyy";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "claude-code-account-rotation-tests", Guid.NewGuid().ToString("N"));

    public PseudoTerminalChildTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static bool Required => Environment.GetEnvironmentVariable(RequireVariable) == "1";

    private static string? OnPath(string name)
    {
        string[] names = OperatingSystem.IsWindows() ? [name + ".exe", name + ".cmd"] : [name];
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string candidate in names)
            {
                string path = Path.Combine(directory, candidate);
                if (File.Exists(path))
                {
                    return path;
                }
            }
        }

        return null;
    }

    private static string Require(string name)
    {
        string? path = OnPath(name);
        if (path is null)
        {
            Assert.False(Required, name + " is not on PATH, and " + RequireVariable + "=1 says these tests must run");
            Assert.Skip(name + " is not on PATH");
        }

        return path!;
    }

    private ClaudeExecutable ProbeExecutable()
    {
        string node = Require("node");
        string script = Path.Combine(_root, "probe.cjs");
        File.WriteAllText(script, Probe);
        return new ClaudeExecutable(node, [script]);
    }

    private static async Task<string> ReadUntilAsync(ILoginChild child, Func<string, bool> done, TimeSpan budget)
    {
        using CancellationTokenSource timeout = new(budget);
        string seen = string.Empty;
        try
        {
            while (!done(ClaudeCliSetupTokenRunner.Visible(seen)))
            {
                string? chunk = await child.ReadAsync(timeout.Token);
                if (chunk is null)
                {
                    break;
                }

                seen += chunk;
            }
        }
        catch (OperationCanceledException)
        {
            // Reported below with whatever arrived.
        }

        return ClaudeCliSetupTokenRunner.Visible(seen);
    }

    [Fact]
    public async Task TheChildSeesATerminalAndItsCodeArrivesAsKeys()
    {
        LoginChildFactory start = PseudoTerminalChild.Factory(ProbeExecutable());
        Result<ILoginChild, string> started = start(["setup-token"], _root);
        started.IsSuccess.ShouldBeTrue(started.IsFailure ? started.Error : null);
        using ILoginChild child = started.Value;

        string greeting = await ReadUntilAsync(child, static text => text.Contains("Paste code", StringComparison.Ordinal), TimeSpan.FromSeconds(30));
        greeting.ShouldNotContain("not a terminal");
        ClaudeCliSetupTokenRunner.ExtractAuthorizeUrl(greeting)!.Query.ShouldContain("scope=user%3Ainference");

        await child.WriteCodeAsync("code-42#state", TestContext.Current.CancellationToken);
        string answer = await ReadUntilAsync(child, static text => text.Contains("securely", StringComparison.Ordinal), TimeSpan.FromSeconds(30));

        ClaudeCliSetupTokenRunner.ExtractTokens(answer).ShouldBe([SetupTokenScript.TokenPrefix + new string('Q', 60) + "code-42stateAA"]);
        answer.ShouldNotContain("code-42#state");
    }

    /// <summary>
    /// A code as long as a real one (#334): typed as one burst ending in Enter,
    /// the CLI takes the whole burst for pasted text and never submits it.
    /// </summary>
    [Fact]
    public async Task ACodeAsLongAsARealOneIsSubmitted()
    {
        LoginChildFactory start = PseudoTerminalChild.Factory(ProbeExecutable());
        using ILoginChild child = start(["setup-token"], _root).Value;
        _ = await ReadUntilAsync(child, static text => text.Contains("Paste code", StringComparison.Ordinal), TimeSpan.FromSeconds(30));

        await child.WriteCodeAsync(RealLengthCode, TestContext.Current.CancellationToken);
        string answer = await ReadUntilAsync(child, static text => text.Contains("securely", StringComparison.Ordinal), TimeSpan.FromSeconds(30));

        ClaudeCliSetupTokenRunner.ExtractTokens(answer).ShouldBe([SetupTokenScript.TokenPrefix + new string('Q', 60) + RealLengthCode.Replace("#", string.Empty, StringComparison.Ordinal) + "AA"]);
    }


    [Fact]
    public async Task TheRunnerDeliversTheTokenThroughARealTerminal()
    {
        LoginChildFactory start = PseudoTerminalChild.Factory(ProbeExecutable());
        RecordingSecretWriter secrets = new();
        using RosterFile roster = new(_root);
        AccountEmail email = AccountEmail.Parse("ci@example.com").Value;
        await roster.UpdateAsync(current => current.With(new RosterEntry(email)), TestContext.Current.CancellationToken);
        using ClaudeCliSetupTokenRunner runner = new(start, secrets, roster, TimeProvider.System, new RecordingLogger<ClaudeCliSetupTokenRunner>(), TimeSpan.FromSeconds(30));
        CiTokenSecret secret = CiTokenSecret.Parse("CLAUDE_CODE_OAUTH_TOKEN", null, "octo").Value;

        LoginSession session = (await runner.StartAsync(new CiTokenRequest(email, secret, OrgSecretVisibility.Private, []), TestContext.Current.CancellationToken)).Value;
        LoginSession answered = (await runner.SubmitCodeAsync(session.Id, "abc#def", TestContext.Current.CancellationToken)).Value;
        await runner.FinishedAsync(session.Id);

        answered.State.ShouldBe(LoginSessionState.Completed, answered.Message);
        secrets.Sets.Single().Value.ShouldBe(SetupTokenScript.TokenPrefix + new string('Q', 60) + "abcdefAA");
    }

    [Fact]
    public async Task ARejectedCodeIsSeenThroughARealTerminal()
    {
        LoginChildFactory start = PseudoTerminalChild.Factory(ProbeExecutable());
        RecordingSecretWriter secrets = new();
        using RosterFile roster = new(_root);
        using ClaudeCliSetupTokenRunner runner = new(start, secrets, roster, TimeProvider.System, new RecordingLogger<ClaudeCliSetupTokenRunner>(), TimeSpan.FromSeconds(30));
        CiTokenSecret secret = CiTokenSecret.Parse("CLAUDE_CODE_OAUTH_TOKEN", "octo/repo", null).Value;

        LoginSession session = (await runner.StartAsync(new CiTokenRequest(AccountEmail.Parse("ci@example.com").Value, secret, null, []), TestContext.Current.CancellationToken)).Value;
        LoginSession answered = (await runner.SubmitCodeAsync(session.Id, "bad", TestContext.Current.CancellationToken)).Value;

        answered.State.ShouldBe(LoginSessionState.Failed);
        answered.Message!.ShouldContain("rejected");
        secrets.Sets.ShouldBeEmpty();
    }

    [Fact]
    public async Task KillEndsTheChildAndItsOutput()
    {
        LoginChildFactory start = PseudoTerminalChild.Factory(ProbeExecutable());
        using ILoginChild child = start(["setup-token"], _root).Value;
        _ = await ReadUntilAsync(child, static text => text.Contains("Paste code", StringComparison.Ordinal), TimeSpan.FromSeconds(30));

        child.Kill();

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        while (await child.ReadAsync(timeout.Token) is not null)
        {
        }
    }

    /// <summary>
    /// The contract with the real CLI: under this host, <c>claude setup-token</c>
    /// draws its screen and prints an inference-only sign-in URL. No code is
    /// sent, so nothing here signs in or calls Anthropic's token endpoint. A
    /// CLI release that stops drawing under a pseudo-terminal, or moves the URL,
    /// fails here rather than on the operator's machine.
    /// </summary>
    [Fact]
    public async Task TheRealSetupTokenDrawsItsSignInUrlUnderThisHost()
    {
        string path = Require("claude");
        Result<ClaudeExecutable, string> cli = ClaudeExecutableLocator.Locate(path, null, OperatingSystem.IsWindows(), Environment.SystemDirectory);
        cli.IsSuccess.ShouldBeTrue();
        string config = Path.Combine(_root, "config");
        Directory.CreateDirectory(config);

        Result<ILoginChild, string> started = PseudoTerminalChild.Factory(cli.Value)(ClaudeCliSetupTokenRunner.Arguments, config);
        started.IsSuccess.ShouldBeTrue(started.IsFailure ? started.Error : null);
        using ILoginChild child = started.Value;
        string screen = await ReadUntilAsync(child, static text => ClaudeCliSetupTokenRunner.ExtractAuthorizeUrl(text) is not null, TimeSpan.FromSeconds(60));
        child.Kill();

        Uri? url = ClaudeCliSetupTokenRunner.ExtractAuthorizeUrl(screen);
        url.ShouldNotBeNull("no sign-in URL in what setup-token drew:\n" + screen);
        url.Query.ShouldContain("scope=user%3Ainference");
        // Whole, not cut at a terminal edge: the parameters after scope are there too.
        url.Query.ShouldContain("code_challenge_method=S256");
        // Both are 32 random bytes in base64url: exactly 43 characters each.
        url.Query.ShouldMatch("[?&]code_challenge=[A-Za-z0-9_-]{43}&");
        url.Query.ShouldMatch("[?&]state=[A-Za-z0-9_-]{43}$");
    }

    /// <summary>
    /// The code-entry contract with the real CLI, through the runner as the
    /// dashboard drives it: a made-up code is typed into the real
    /// <c>claude setup-token</c>, which sends it to Anthropic's token endpoint
    /// and draws its own refusal (today "OAuth error: Request failed with status
    /// code 400"). The code is as long as a real one: a shorter one passed here
    /// while every real code was taken for pasted text and never submitted
    /// (#334). Any refusal the CLI draws after the code proves the code
    /// arrived and was submitted, whatever the endpoint answered (a 400, a rate
    /// limit, a network error), so the exact status is not asserted; a code the
    /// CLI never received ends instead in "did not answer the code", with the
    /// screen it was left on. Nothing signs in and no token can be made.
    /// </summary>
    [Fact]
    public async Task TheRealSetupTokenReceivesTheCodeTypedThroughThisHost()
    {
        string path = Require("claude");
        Result<ClaudeExecutable, string> cli = ClaudeExecutableLocator.Locate(path, null, OperatingSystem.IsWindows(), Environment.SystemDirectory);
        cli.IsSuccess.ShouldBeTrue();
        RecordingSecretWriter secrets = new();
        using RosterFile roster = new(_root);
        using ClaudeCliSetupTokenRunner runner = new(PseudoTerminalChild.Factory(cli.Value), secrets, roster, TimeProvider.System, new RecordingLogger<ClaudeCliSetupTokenRunner>(), TimeSpan.FromSeconds(60));
        CiTokenSecret secret = CiTokenSecret.Parse("CLAUDE_CODE_OAUTH_TOKEN", null, "octo").Value;

        Result<LoginSession, string> started = await runner.StartAsync(new CiTokenRequest(AccountEmail.Parse("ci@example.com").Value, secret, OrgSecretVisibility.Private, []), TestContext.Current.CancellationToken);
        started.IsSuccess.ShouldBeTrue(started.IsFailure ? started.Error : null);
        LoginSession answered = (await runner.SubmitCodeAsync(started.Value.Id, RealLengthCode, TestContext.Current.CancellationToken)).Value;
        await runner.FinishedAsync(started.Value.Id);

        answered.State.ShouldBe(LoginSessionState.Failed, answered.Message);
        answered.Message!.ShouldContain("rejected; claude setup-token said:", customMessage: answered.Message);
        answered.Message!.ShouldNotContain("did not answer the code", customMessage: answered.Message);
        answered.Message!.ShouldNotContain("not-a-real-code");
        secrets.Sets.ShouldBeEmpty();
    }
}
