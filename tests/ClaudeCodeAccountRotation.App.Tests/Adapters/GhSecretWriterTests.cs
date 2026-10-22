using ClaudeCodeAccountRotation.App.Adapters.Process;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Accounts;

namespace ClaudeCodeAccountRotation.App.Tests.Adapters;

/// <summary>
/// The <c>gh secret set</c> adapter: the argument list it builds, and, against
/// a stand-in <c>gh</c>, that the value travels on standard input and never on
/// the command line.
/// </summary>
[Collection(StandInScripts.Name)]
public sealed class GhSecretWriterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "claude-code-account-rotation-tests", Guid.NewGuid().ToString("N"));

    private readonly GhWriteGate _gate = new(TimeProvider.System);

    public GhSecretWriterTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        _gate.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    public static bool OnUnix => !OperatingSystem.IsWindows();

    [Fact]
    public void ARepositorySecretNamesTheRepository() =>
        GhSecretWriter.Arguments(CiTokenSecret.Parse("LANE_TOKEN", "octo/repo", null).Value, null, [])
            .ShouldBe(["secret", "set", "LANE_TOKEN", "--app", "actions", "--repo", "octo/repo"]);

    [Theory]
    [InlineData("Private", "private")]
    [InlineData("All", "all")]
    public void AnOrganizationSecretCarriesItsVisibility(string visibility, string flag) =>
        GhSecretWriter.Arguments(CiTokenSecret.Parse("TOKEN", null, "octo").Value, Enum.Parse<OrgSecretVisibility>(visibility), [])
            .ShouldBe(["secret", "set", "TOKEN", "--app", "actions", "--org", "octo", "--visibility", flag]);

    [Fact]
    public void AnOrganizationSecretWithNoVisibilityIsRefusedRatherThanMadePrivate() =>
        Should.Throw<ArgumentException>(() => GhSecretWriter.Arguments(CiTokenSecret.Parse("TOKEN", null, "octo").Value, null, []));

    [Fact]
    public void ASelectedOrganizationSecretListsItsRepositories() =>
        GhSecretWriter.Arguments(CiTokenSecret.Parse("TOKEN", null, "octo").Value, OrgSecretVisibility.Selected, ["one", "two"])
            .ShouldBe(["secret", "set", "TOKEN", "--app", "actions", "--org", "octo", "--repos", "one,two"]);

    [Fact]
    public void NoGhOnPathIsAReasonNotAThrow() =>
        GhSecretWriter.Locate(_root, OperatingSystem.IsWindows()).Error.ShouldContain("gh auth login");

    [Fact(SkipUnless = nameof(OnUnix), Skip = "The stand-in gh is a shell script")]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task TheValueArrivesOnStandardInputAndNeverAsAnArgument()
    {
        string token = SetupTokenScript.FakeToken('S', 40);
        string recorded = Path.Combine(_root, "recorded");
        string gh = StandIn(recorded, exitCode: 0);

        GhSecretWriter writer = new(gh, TimeSpan.FromSeconds(30), _gate);
        Result<Unit, string> set = await writer.SetAsync(
            CiTokenSecret.Parse("LANE_TOKEN", "octo/repo", null).Value,
            null,
            [],
            token,
            TestContext.Current.CancellationToken);

        set.IsSuccess.ShouldBeTrue();
        (await File.ReadAllTextAsync(recorded + ".stdin", TestContext.Current.CancellationToken)).ShouldBe(token);
        string arguments = await File.ReadAllTextAsync(recorded + ".args", TestContext.Current.CancellationToken);
        arguments.ShouldBe("secret set LANE_TOKEN --app actions --repo octo/repo\n");
        arguments.ShouldNotContain(token);
    }

    [Theory(SkipUnless = nameof(OnUnix), Skip = "The stand-in gh is a shell script")]
    [InlineData("CLAUDE_CODE_OAUTH_TOKEN")]
    [InlineData("REVIEWS_CLAUDE_CODE_OAUTH_TOKEN")]
    [InlineData("pr_verify_claude_api_key")]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task AClaudeRoleSecretInOneRepositoryIsRefusedBeforeGhStarts(string name)
    {
        string recorded = Path.Combine(_root, "recorded");
        GhSecretWriter writer = new(StandIn(recorded, exitCode: 0), TimeSpan.FromSeconds(30), _gate);

        Result<Unit, string> set = await writer.SetAsync(CiTokenSecret.Parse(name, "octo/repo", null).Value, null, [], "value", TestContext.Current.CancellationToken);

        set.Error.ShouldBe(CiTokenSecret.RepositoryRoleSecretRefusal);
        File.Exists(recorded + ".args").ShouldBeFalse();
    }

    /// <summary>
    /// The hunt for a way past the repository refusal: every writer in the app,
    /// with every visibility and repository list it takes, refuses before
    /// anything runs, and setting a secret is the only thing a writer does with
    /// one. A new writer fails here until it is added.
    /// </summary>
    [Fact]
    public async Task NoWriterAndNoArgumentGetsARepositoryRoleSecretWritten()
    {
        Dictionary<Type, ICiSecretWriter> writers = new()
        {
            [typeof(GhSecretWriter)] = new GhSecretWriter(Path.Combine(_root, "no-such-gh"), TimeSpan.FromSeconds(30), _gate),
            [typeof(UnavailableCiSecretWriter)] = new UnavailableCiSecretWriter("the GitHub CLI (gh) was not found on PATH"),
        };
        Type[] found = [.. typeof(ICiSecretWriter).Assembly.GetTypes().Where(static type => typeof(ICiSecretWriter).IsAssignableFrom(type) && !type.IsInterface && !type.IsAbstract)];
        found.ShouldBe(writers.Keys, ignoreOrder: true);
        found.SelectMany(static type => type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly))
            .Where(static method => method.GetParameters().Any(static parameter => parameter.ParameterType == typeof(CiTokenSecret)))
            .Select(static method => method.Name)
            .Distinct()
            .ShouldBe(["SetAsync"]);

        CiTokenSecret secret = CiTokenSecret.Parse("PR_VERIFY_CLAUDE_API_KEY", "octo/repo", null).Value;
        foreach (ICiSecretWriter writer in writers.Values)
        {
            foreach (OrgSecretVisibility? visibility in new OrgSecretVisibility?[] { null, OrgSecretVisibility.Private, OrgSecretVisibility.All, OrgSecretVisibility.Selected })
            {
                (await writer.SetAsync(secret, visibility, ["repo"], "value", TestContext.Current.CancellationToken)).Error
                    .ShouldBe(CiTokenSecret.RepositoryRoleSecretRefusal, writer.GetType().Name + " with " + visibility);
            }
        }
    }

    [Fact(SkipUnless = nameof(OnUnix), Skip = "The stand-in gh is a shell script")]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task AFailingGhIsReportedByExitCodeWithoutItsOutput()
    {
        string gh = StandIn(Path.Combine(_root, "recorded"), exitCode: 4);

        GhSecretWriter writer = new(gh, TimeSpan.FromSeconds(30), _gate);
        Result<Unit, string> set = await writer.SetAsync(
            CiTokenSecret.Parse("TOKEN", null, "octo").Value,
            OrgSecretVisibility.Private,
            [],
            "value",
            TestContext.Current.CancellationToken);

        set.Error.ShouldContain("exited with code 4");
        set.Error.ShouldNotContain("stand-in says");
    }

    [Fact(SkipUnless = nameof(OnUnix), Skip = "The stand-in gh is a shell script")]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task ASignedOutGhFailsTheCheck()
    {
        string gh = StandIn(Path.Combine(_root, "recorded"), exitCode: 1);

        GhSecretWriter writer = new(gh, TimeSpan.FromSeconds(30), _gate);
        Result<Unit, string> check = await writer.CheckAsync(TestContext.Current.CancellationToken);

        check.Error.ShouldContain("gh auth login");
    }

    [Fact(SkipUnless = nameof(OnUnix), Skip = "The stand-in gh is a shell script")]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task FourSetsInARowStartAtLeastOneSecondAfterTheLastEnded()
    {
        string log = Path.Combine(_root, "calls");
        // Each call logs when it started and when it ended, in nanoseconds.
        string gh = Script("echo \"start $(date +%s%N)\" >> '" + log + "'\ncat > /dev/null\necho \"end $(date +%s%N)\" >> '" + log + "'\n");
        GhSecretWriter writer = new(gh, TimeSpan.FromSeconds(30), _gate);

        foreach (string name in new[] { "A_CLAUDE_CODE_OAUTH_TOKEN", "A_CLAUDE_API_KEY", "B_CLAUDE_CODE_OAUTH_TOKEN", "B_CLAUDE_API_KEY" })
        {
            (await writer.SetAsync(CiTokenSecret.Parse(name, null, "octo").Value, OrgSecretVisibility.All, [], "value", TestContext.Current.CancellationToken))
                .IsSuccess.ShouldBeTrue();
        }

        string[] lines = await File.ReadAllLinesAsync(log, TestContext.Current.CancellationToken);
        lines.Length.ShouldBe(8);
        long[] stamps = [.. lines.Select(static line => long.Parse(line.Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture))];
        for (int call = 1; call < 4; call++)
        {
            lines[call * 2].ShouldStartWith("start");
            long gap = stamps[call * 2] - stamps[(call * 2) - 1];
            gap.ShouldBeGreaterThanOrEqualTo(1_000_000_000L, "call " + call + " started " + gap + " ns after the one before it ended; the log was: " + string.Join(" | ", lines));
        }
    }

    [Fact(SkipUnless = nameof(OnUnix), Skip = "The stand-in gh is a shell script")]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task ARateLimitAnswerIsNamedAsOneWithoutGhsOutput()
    {
        string gh = Script("cat > /dev/null\necho 'HTTP 403: API rate limit exceeded for user ID 1 (stand-in)' >&2\nexit 1\n");
        GhSecretWriter writer = new(gh, TimeSpan.FromSeconds(30), _gate);

        Result<Unit, string> set = await writer.SetAsync(CiTokenSecret.Parse("TOKEN", null, "octo").Value, OrgSecretVisibility.All, [], "value", TestContext.Current.CancellationToken);

        set.Error.ShouldBe(GhSecretWriter.RateLimitMessage);
    }

    /// <summary>A gh that runs <paramref name="body"/>.</summary>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private string Script(string body)
    {
        string path = Path.Combine(_root, "gh");
        File.WriteAllText(path, "#!/bin/sh\n" + body);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    /// <summary>A gh that records its arguments and standard input, prints something, and exits as told.</summary>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private string StandIn(string recorded, int exitCode)
    {
        string path = Path.Combine(_root, "gh");
        File.WriteAllText(
            path,
            "#!/bin/sh\n"
            + "echo \"$*\" > '" + recorded + ".args'\n"
            + "cat > '" + recorded + ".stdin'\n"
            + "echo 'stand-in says hello' >&2\n"
            + "exit " + exitCode + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }
}
