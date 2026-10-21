using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.Core.Accounts;
using ClaudeCodeAccountRotation.Core.Identity;

namespace ClaudeCodeAccountRotation.App.Tests.Adapters;

/// <summary>
/// The CI roles in <c>roster.json</c>: what a file the 2.1.0 and 2.2.0 writers
/// made reads as, and that the new shape comes back exactly as written.
/// </summary>
public sealed class RosterFileCiRoleTests : IDisposable
{
    private readonly string _appData = Path.Combine(Path.GetTempPath(), "claude-code-account-rotation-tests", Guid.NewGuid().ToString("N"));

    private static AccountEmail Email(string value) => AccountEmail.Parse(value).Value;

    private string RosterPath => Path.Combine(_appData, RosterFile.FileName);

    public void Dispose()
    {
        if (Directory.Exists(_appData))
        {
            Directory.Delete(_appData, recursive: true);
        }
    }

    // The shape the v2.1.0 RosterFile.ToJson wrote, field for field.
    private async Task<RosterEntry> ReadOneWrittenBy210Async(string secret)
    {
        Directory.CreateDirectory(_appData);
        await File.WriteAllTextAsync(
            RosterPath,
            """{ "accounts": [ { "email": "a@example.com", "alias": null, "browser": null, "browserProfileDirectory": null, "paused": false, "notes": null, "ciTokenGeneratedOn": "2026-03-04", "ciTokenSecret": """
            + secret + " } ] }",
            TestContext.Current.CancellationToken);
        using RosterFile file = new(_appData);
        return (await file.ReadAsync(TestContext.Current.CancellationToken)).Find(Email("a@example.com"))!;
    }

    [Fact]
    public async Task AnOrganizationSecretFrom210ReadsAsItsPrefixRoleWithItsTokenHalfSet()
    {
        RosterEntry entry = await ReadOneWrittenBy210Async("""{ "name": "AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN", "organization": "octo" }""");

        CiRoleAssignment role = entry.CiRoles.Single();
        role.Role.ShouldBe(new CiRole("AUTOMATIONS", CiSecretScope.Organization, "octo"));
        role.TokenSetOn.ShouldBe(new DateOnly(2026, 3, 4));
        role.TokenSetAt.ShouldBeNull();
        role.KeySetOn.ShouldBeNull();
        role.KeySetAt.ShouldBeNull();
        entry.CiTokenGeneratedOn.ShouldBe(new DateOnly(2026, 3, 4));
    }

    [Fact]
    public async Task TheBareSecretFrom210ReadsAsTheLegacyRole()
    {
        RosterEntry entry = await ReadOneWrittenBy210Async("""{ "name": "CLAUDE_CODE_OAUTH_TOKEN", "organization": "octo" }""");

        CiRoleAssignment role = entry.CiRoles.Single();
        role.Role.Name.ShouldBe("legacy (review lanes)");
        role.Role.TokenSecretName.ShouldBe("CLAUDE_CODE_OAUTH_TOKEN");
        role.Role.KeySecretName.ShouldBeNull();
        role.TokenSetOn.ShouldBe(new DateOnly(2026, 3, 4));
    }

    [Fact]
    public async Task AnyOtherSecretFrom210ReadsAsATokenOnlyRoleUnderItsOwnName()
    {
        RosterEntry entry = await ReadOneWrittenBy210Async("""{ "name": "MY_TOKEN", "repository": "octo/lane-one" }""");

        CiRoleAssignment role = entry.CiRoles.Single();
        role.Role.Name.ShouldBe("MY_TOKEN");
        role.Role.TokenSecretName.ShouldBe("MY_TOKEN");
        role.Role.KeySecretName.ShouldBeNull();
        role.Role.Scope.ShouldBe(CiSecretScope.Repository);
        role.Role.Owner.ShouldBe("octo/lane-one");
        role.TokenSetOn.ShouldBe(new DateOnly(2026, 3, 4));
    }

    [Fact]
    public async Task ARosterWithThreeRolesRoundTripsUnchanged()
    {
        RosterEntry entry = new(Email("a@example.com"), CiTokenGeneratedOn: new DateOnly(2026, 10, 6))
        {
            ConsoleApiKeySetOn = new DateOnly(2026, 10, 1),
            CiRoles =
            [
                new CiRoleAssignment(
                    new CiRole("AUTOMATIONS", CiSecretScope.Organization, "octo"),
                    new DateOnly(2026, 10, 6),
                    new DateTimeOffset(2026, 10, 6, 14, 30, 5, TimeSpan.Zero),
                    new DateOnly(2026, 10, 2),
                    new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero)),
                new CiRoleAssignment(new CiRole(string.Empty, CiSecretScope.Repository, "octo/lane-one"), new DateOnly(2026, 9, 1)),
                new CiRoleAssignment(new CiRole(string.Empty, CiSecretScope.Organization, "octo", "MY_TOKEN"), new DateOnly(2026, 8, 1)),
            ],
        };
        using RosterFile file = new(_appData);

        await file.UpdateAsync(roster => roster.With(entry), TestContext.Current.CancellationToken);
        string written = await File.ReadAllTextAsync(RosterPath, TestContext.Current.CancellationToken);
        await file.UpdateAsync(static roster => new Roster(roster.Entries), TestContext.Current.CancellationToken);

        RosterEntry stored = (await file.ReadAsync(TestContext.Current.CancellationToken)).Find(Email("a@example.com"))!;
        stored.ShouldBe(entry);
        stored.CiRoles.ShouldBe(entry.CiRoles);
        (await File.ReadAllTextAsync(RosterPath, TestContext.Current.CancellationToken)).ShouldBe(written);
        JsonObject account = JsonNode.Parse(written)!["accounts"]![0]!.AsObject();
        // The newest token half's secret, for a 2.2.0 downgrade: AUTOMATIONS, set 2026-10-06.
        account["ciTokenSecret"]!["name"]!.GetValue<string>().ShouldBe("AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN");
        account["ciTokenSecret"]!["organization"]!.GetValue<string>().ShouldBe("octo");
        account["ciTokenGeneratedOn"]!.GetValue<string>().ShouldBe("2026-10-06");
        account["ciRoles"]!.AsArray().Count.ShouldBe(3);
    }

    [Fact]
    public async Task AFileWithBothKeysReadsItsRolesAndNotTheSingleSecret()
    {
        Directory.CreateDirectory(_appData);
        await File.WriteAllTextAsync(
            RosterPath,
            """
            { "accounts": [ { "email": "a@example.com", "ciTokenGeneratedOn": "2026-10-06",
              "ciTokenSecret": { "name": "CLAUDE_CODE_OAUTH_TOKEN", "repository": "octo/lane-one" },
              "ciRoles": [ { "prefix": "REVIEWS", "organization": "octo", "tokenSetOn": "2026-10-06" } ] } ] }
            """,
            TestContext.Current.CancellationToken);
        using RosterFile file = new(_appData);

        RosterEntry stored = (await file.ReadAsync(TestContext.Current.CancellationToken)).Find(Email("a@example.com"))!;

        stored.CiRoles.ShouldBe([new CiRoleAssignment(new CiRole("REVIEWS", CiSecretScope.Organization, "octo"), new DateOnly(2026, 10, 6))]);
    }

    [Fact]
    public async Task AFileA220DowngradeRewroteGivesBackItsNewestTokenRoleOnReUpgrade()
    {
        RosterEntry entry = new RosterEntry(Email("a@example.com"))
            .ClaimingToken(new CiRole("AUTOMATIONS", CiSecretScope.Organization, "octo"), new DateOnly(2026, 9, 1), null)
            .ClaimingToken(new CiRole(string.Empty, CiSecretScope.Repository, "octo/lane-one"), new DateOnly(2026, 10, 6), null)
            .ClaimingKey(new CiRole("REVIEWS", CiSecretScope.Organization, "octo"), new DateOnly(2026, 10, 7), null);
        using RosterFile file = new(_appData);
        await file.UpdateAsync(roster => roster.With(entry), TestContext.Current.CancellationToken);

        // 2.2.0 knows no ciRoles key and drops it on its next write; everything it keeps it writes back as read.
        JsonObject root = JsonNode.Parse(await File.ReadAllTextAsync(RosterPath, TestContext.Current.CancellationToken))!.AsObject();
        root["accounts"]![0]!.AsObject().Remove("ciRoles");
        await File.WriteAllTextAsync(RosterPath, root.ToJsonString(), TestContext.Current.CancellationToken);

        RosterEntry stored = (await file.ReadAsync(TestContext.Current.CancellationToken)).Find(Email("a@example.com"))!;
        stored.CiRoles.ShouldBe([new CiRoleAssignment(new CiRole(string.Empty, CiSecretScope.Repository, "octo/lane-one"), new DateOnly(2026, 10, 6))]);
        stored.CiTokenGeneratedOn.ShouldBe(new DateOnly(2026, 10, 6));
    }

    [Fact]
    public async Task ARecordedRoleWithNeitherHalfOrABadPrefixIsDropped()
    {
        Directory.CreateDirectory(_appData);
        await File.WriteAllTextAsync(
            RosterPath,
            """
            { "accounts": [ { "email": "a@example.com", "ciRoles": [
              { "prefix": "AUTOMATIONS", "organization": "octo" },
              { "prefix": "9LIVES", "organization": "octo", "tokenSetOn": "2026-10-06" },
              { "prefix": "REVIEWS", "organization": "octo", "keySetOn": "2026-10-06" }
            ] } ] }
            """,
            TestContext.Current.CancellationToken);
        using RosterFile file = new(_appData);

        RosterEntry stored = (await file.ReadAsync(TestContext.Current.CancellationToken)).Find(Email("a@example.com"))!;

        stored.CiRoles.Single().Role.Prefix.ShouldBe("REVIEWS");
    }
}
