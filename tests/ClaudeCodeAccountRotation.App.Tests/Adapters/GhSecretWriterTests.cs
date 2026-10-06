using ClaudeCodeAccountRotation.App.Adapters.Process;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Accounts;

namespace ClaudeCodeAccountRotation.App.Tests.Adapters;

/// <summary>
/// The <c>gh secret set</c> adapter: the argument list it builds, and, against
/// a stand-in <c>gh</c>, that the value travels on standard input and never on
/// the command line.
/// </summary>
public sealed class GhSecretWriterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "claude-code-account-rotation-tests", Guid.NewGuid().ToString("N"));

    public GhSecretWriterTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    public static bool OnUnix => !OperatingSystem.IsWindows();

    [Fact]
    public void ARepositorySecretNamesTheRepository() =>
        GhSecretWriter.Arguments(CiTokenSecret.Parse("CLAUDE_CODE_OAUTH_TOKEN", "octo/repo", null).Value, null, [])
            .ShouldBe(["secret", "set", "CLAUDE_CODE_OAUTH_TOKEN", "--app", "actions", "--repo", "octo/repo"]);

    [Theory]
    [InlineData("Private", "private")]
    [InlineData("All", "all")]
    public void AnOrganizationSecretCarriesItsVisibility(string visibility, string flag) =>
        GhSecretWriter.Arguments(CiTokenSecret.Parse("TOKEN", null, "octo").Value, Enum.Parse<OrgSecretVisibility>(visibility), [])
            .ShouldBe(["secret", "set", "TOKEN", "--app", "actions", "--org", "octo", "--visibility", flag]);

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

        Result<Unit, string> set = await new GhSecretWriter(gh, TimeSpan.FromSeconds(30)).SetAsync(
            CiTokenSecret.Parse("CLAUDE_CODE_OAUTH_TOKEN", "octo/repo", null).Value,
            null,
            [],
            token,
            TestContext.Current.CancellationToken);

        set.IsSuccess.ShouldBeTrue();
        (await File.ReadAllTextAsync(recorded + ".stdin", TestContext.Current.CancellationToken)).ShouldBe(token);
        string arguments = await File.ReadAllTextAsync(recorded + ".args", TestContext.Current.CancellationToken);
        arguments.ShouldBe("secret set CLAUDE_CODE_OAUTH_TOKEN --app actions --repo octo/repo\n");
        arguments.ShouldNotContain(token);
    }

    [Fact(SkipUnless = nameof(OnUnix), Skip = "The stand-in gh is a shell script")]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task AFailingGhIsReportedByExitCodeWithoutItsOutput()
    {
        string gh = StandIn(Path.Combine(_root, "recorded"), exitCode: 4);

        Result<Unit, string> set = await new GhSecretWriter(gh, TimeSpan.FromSeconds(30)).SetAsync(
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

        Result<Unit, string> check = await new GhSecretWriter(gh, TimeSpan.FromSeconds(30)).CheckAsync(TestContext.Current.CancellationToken);

        check.Error.ShouldContain("gh auth login");
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
