using ClaudeCodeAccountRotation.Core.Accounts;

namespace ClaudeCodeAccountRotation.Core.Tests.Accounts;

/// <summary>
/// The lane catalog, against <c>Fixtures/lane-funding.json</c>: claude-code-plugins'
/// <c>.github/lane-funding.json</c> as its pull request #9234 wrote it at
/// 6ace56521427502cd367752d253a7bd5e62a7830.
/// </summary>
public sealed class LaneCatalogTests
{
    private static string Fixture => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "lane-funding.json"));

    private static LaneCatalog Catalog => LaneCatalog.Parse(Fixture).Value;

    private static CiRole Org(string prefix, string organization = "melodic-software") => new(prefix, CiSecretScope.Organization, organization);

    private static string Minimal(string roles, string version = "1", string org = "\"octo\"") =>
        "{ \"version\": " + version + ", \"org\": " + org + ", \"roles\": [" + roles + "] }";

    private static string Role(string prefix, string keyed = "true", string? token = null, string? key = null, string currentSecrets = "") =>
        "{ \"id\": \"r\", \"secretPrefix\": \"" + prefix + "\", \"tokenSecret\": \"" + (token ?? prefix + "_CLAUDE_CODE_OAUTH_TOKEN")
        + "\", \"keySecret\": \"" + (key ?? prefix + "_CLAUDE_API_KEY") + "\", \"keyed\": " + keyed + ", \"category\": \"c\", \"lanes\": [ { \"id\": \"l\", \"workflow\": \"w.yml\", \"launcher\": \"run-claude-cli\", \"currentSecrets\": [" + currentSecrets + "] } ] }";

    [Fact]
    public void TheCatalogNamesItsOrganizationAndEachRoleInOrder()
    {
        LaneCatalog catalog = Catalog;

        catalog.Organization.ShouldBe("melodic-software");
        catalog.Roles.Select(static role => role.Prefix).ShouldBe(["REVIEWS", "REVIEWS_SECURITY", "ISSUE_LANE", "INTAKE", "PR_UNBLOCK", "PR_VERIFY", "AUTOMATIONS"]);
        catalog.Roles.Single(static role => role.Prefix == "PR_UNBLOCK").Lanes.Count.ShouldBe(9);
    }

    // The keyed rule: the catalog says keyed, or a lane still reads the role's
    // key secret. AUTOMATIONS says keyed:false, but the intake, pr-unblock and
    // pr-verify lanes list AUTOMATIONS_CLAUDE_API_KEY.
    [Theory]
    [InlineData("INTAKE", true)]
    [InlineData("PR_UNBLOCK", true)]
    [InlineData("pr_verify", true)]
    [InlineData("AUTOMATIONS", true)]
    [InlineData("REVIEWS", false)]
    [InlineData("REVIEWS_SECURITY", false)]
    [InlineData("ISSUE_LANE", false)]
    [InlineData("", false)]
    [InlineData("NOT_IN_CATALOG", false)]
    public void ARoleIsKeyedWhenTheCatalogSaysSoOrALaneStillReadsItsKey(string prefix, bool keyed) =>
        Catalog.IsKeyed(Org(prefix)).ShouldBe(keyed);

    [Fact]
    public void ARoleNoLaneReadsTheKeyOfIsNotKeyedWhenTheCatalogSaysSo()
    {
        LaneCatalog catalog = LaneCatalog.Parse(Minimal(Role("AUTOMATIONS", keyed: "false") + "," + Role("INTAKE", currentSecrets: "\"INTAKE_CLAUDE_API_KEY\""))).Value;

        catalog.IsKeyed(Org("AUTOMATIONS", "octo")).ShouldBe(false);
        catalog.IsKeyed(Org("INTAKE", "octo")).ShouldBe(true);
    }

    [Fact]
    public void TheCatalogSpeaksOnlyForItsOwnOrganization()
    {
        Catalog.IsKeyed(Org("INTAKE", "other-org")).ShouldBeNull();
        Catalog.IsKeyed(new CiRole("INTAKE", CiSecretScope.Repository, "melodic-software/repo")).ShouldBeNull();
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{ \"org\": \"octo\", \"roles\": [] }")]
    public void AFileThatIsNoCatalogIsRefused(string json) =>
        LaneCatalog.Parse(json).IsFailure.ShouldBeTrue();

    [Fact]
    public void AnUnknownVersionIsRefused() =>
        LaneCatalog.Parse(Minimal(Role("INTAKE"), version: "2")).Error.ShouldContain("version 2");

    [Theory]
    [InlineData("\"not a login!\"")]
    [InlineData("null")]
    public void AnOrganizationThatIsNoGitHubLoginIsRefused(string org) =>
        LaneCatalog.Parse(Minimal(Role("INTAKE"), org: org)).IsFailure.ShouldBeTrue();

    [Fact]
    public void ATokenSecretThatIsNotThePrefixsIsRefused() =>
        LaneCatalog.Parse(Minimal(Role("INTAKE", token: "OTHER_CLAUDE_CODE_OAUTH_TOKEN"))).Error.ShouldContain("INTAKE_CLAUDE_CODE_OAUTH_TOKEN");

    [Fact]
    public void AKeySecretThatIsNotThePrefixsIsRefused() =>
        LaneCatalog.Parse(Minimal(Role("INTAKE", key: "INTAKE_KEY"))).Error.ShouldContain("INTAKE_CLAUDE_API_KEY");

    [Theory]
    [InlineData("9LIVES")]
    [InlineData("GITHUB")]
    [InlineData("WITH SPACE")]
    public void APrefixGitHubWouldRefuseIsRefused(string prefix) =>
        LaneCatalog.Parse(Minimal(Role(prefix))).IsFailure.ShouldBeTrue();

    [Fact]
    public void APrefixNamedTwiceIsRefused() =>
        LaneCatalog.Parse(Minimal(Role("INTAKE") + "," + Role("intake"))).Error.ShouldContain("twice");

    [Fact]
    public void AKeyedFlagThatIsNotABooleanIsRefused() =>
        LaneCatalog.Parse(Minimal(Role("INTAKE", keyed: "\"yes\""))).IsFailure.ShouldBeTrue();

    [Fact]
    public void ACurrentSecretThatIsNoSecretNameIsRefused() =>
        LaneCatalog.Parse(Minimal(Role("INTAKE", currentSecrets: "\"not a name\""))).IsFailure.ShouldBeTrue();
}
