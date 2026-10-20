using ClaudeCodeAccountRotation.Core.Accounts;

namespace ClaudeCodeAccountRotation.Core.Tests.Accounts;

/// <summary>Roles derived from an organization's secret names, as the plan's acceptance lists them.</summary>
public sealed class RoleCatalogTests
{
    private static readonly DateTimeOffset _updated = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static RoleCatalog Catalog(params string[] names) =>
        RoleCatalog.From([new OrganizationSecrets("octo", [.. names.Select(name => new ListedSecret(name, _updated))])]);

    private static IEnumerable<(string Name, bool HasToken, bool HasKey)> Shape(RoleCatalog catalog) =>
        catalog.Roles.Values.Select(static role => (role.Role.Name, role.HasToken, role.HasKey)).OrderBy(static role => role.Name, StringComparer.Ordinal);

    [Fact]
    public void TheOrgSecretsYieldTwoPairedRolesAndLegacy()
    {
        RoleCatalog catalog = Catalog(
            "AUTOMATIONS_CLAUDE_API_KEY",
            "AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN",
            "ISSUE_LANE_CLAUDE_API_KEY",
            "ISSUE_LANE_CLAUDE_CODE_OAUTH_TOKEN",
            "CLAUDE_CODE_OAUTH_TOKEN",
            "APP_PRIVATE_KEY");

        Shape(catalog).ShouldBe([
            ("AUTOMATIONS", true, true),
            ("ISSUE_LANE", true, true),
            ("legacy (review lanes)", true, false),
        ]);
    }

    [Fact]
    public void ALoneTokenSecretYieldsATokenOnlyRole() =>
        Shape(Catalog("REVIEWS_CLAUDE_CODE_OAUTH_TOKEN")).ShouldBe([("REVIEWS", true, false)]);

    [Fact]
    public void ALoneKeySecretYieldsAKeyOnlyRole() =>
        Shape(Catalog("SPEND_CLAUDE_API_KEY")).ShouldBe([("SPEND", false, true)]);

    [Fact]
    public void ARoleIsKeyedByPrefixAndOrganizationWithoutCase()
    {
        var catalog = RoleCatalog.From([
            new OrganizationSecrets("octo", [new ListedSecret("AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN", _updated)]),
            new OrganizationSecrets("other", [new ListedSecret("AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN", _updated)]),
        ]);

        catalog.Roles.Count.ShouldBe(2);
        catalog.Roles.ContainsKey(new CiRole("automations", CiSecretScope.Organization, "OCTO")).ShouldBeTrue();
    }

    [Fact]
    public void OnlyAnOrganizationThatWasReadIsCovered()
    {
        RoleCatalog catalog = Catalog("APP_PRIVATE_KEY");

        catalog.Covers(new CiRole("AUTOMATIONS", CiSecretScope.Organization, "octo")).ShouldBeTrue();
        catalog.Covers(new CiRole("AUTOMATIONS", CiSecretScope.Organization, "elsewhere")).ShouldBeFalse();
        catalog.Covers(new CiRole("AUTOMATIONS", CiSecretScope.Repository, "octo")).ShouldBeFalse();
    }
}
