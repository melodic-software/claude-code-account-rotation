using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Switching;

namespace ClaudeCodeAccountRotation.App.Tests.Adapters;

public sealed class ProfileFolderStoreTests : IDisposable
{
    private readonly string _profilesRoot = Path.Combine(Path.GetTempPath(), "claude-code-account-rotation-tests", Guid.NewGuid().ToString("N"));
    private readonly ProfileFolderStore _store;

    public ProfileFolderStoreTests()
    {
        Directory.CreateDirectory(_profilesRoot);
        _store = new ProfileFolderStore(_profilesRoot);
    }

    private static JsonObject AccountJson(string email) => new() { ["accountUuid"] = "uuid-" + email, ["emailAddress"] = email };

    private async Task<string> FolderWithProfileAsync(string folderName, string email)
    {
        string folder = Path.Combine(_profilesRoot, folderName);
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "profile.json"), AccountJson(email).ToJsonString(), TestContext.Current.CancellationToken);
        return folder;
    }

    [Fact]
    public async Task ListReadsIdentityFromTheProfileFileNotTheFolderName()
    {
        string folder = await FolderWithProfileAsync("renamed-by-hand", "a@example.com");

        IReadOnlyList<ParkedProfile> profiles = await _store.ListAsync(TestContext.Current.CancellationToken);

        ParkedProfile profile = profiles.ShouldHaveSingleItem();
        profile.Email.ShouldBe(AccountEmail.Parse("a@example.com").Value);
        profile.FolderPath.ShouldBe(folder);
        profile.HasCredentials.ShouldBeFalse();
        profile.Account!.Raw["accountUuid"]!.GetValue<string>().ShouldBe("uuid-a@example.com");
    }

    [Fact]
    public async Task ListReadsAFreshLoginsStateFileAndReportsCredentials()
    {
        string folder = Path.Combine(_profilesRoot, "b@example.com");
        Directory.CreateDirectory(folder);
        JsonObject stateFile = new() { ["numStartups"] = 1, ["oauthAccount"] = AccountJson("b@example.com") };
        await File.WriteAllTextAsync(Path.Combine(folder, ".claude.json"), stateFile.ToJsonString(), TestContext.Current.CancellationToken);
        await CredentialFiles.WriteAsync(folder, "refresh-b", TestContext.Current.CancellationToken);

        ParkedProfile profile = (await _store.ListAsync(TestContext.Current.CancellationToken)).ShouldHaveSingleItem();

        profile.Email.ShouldBe(AccountEmail.Parse("b@example.com").Value);
        profile.HasCredentials.ShouldBeTrue();
    }

    [Fact]
    public async Task ListSkipsFoldersWithoutAnIdentity()
    {
        Directory.CreateDirectory(Path.Combine(_profilesRoot, "nothing-here"));
        await File.WriteAllTextAsync(Path.Combine(_profilesRoot, "stray.txt"), "", TestContext.Current.CancellationToken);
        await FolderWithProfileAsync("a@example.com", "a@example.com");

        (await _store.ListAsync(TestContext.Current.CancellationToken)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task EnsureFolderCreatesTheSanitizedFolder()
    {
        AccountEmail email = AccountEmail.Parse("Dev@Example.com").Value;

        ParkedProfile profile = await _store.EnsureFolderAsync(email, TestContext.Current.CancellationToken);

        profile.FolderPath.ShouldBe(Path.Combine(_profilesRoot, "dev@example.com"));
        Directory.Exists(profile.FolderPath).ShouldBeTrue();
        profile.HasCredentials.ShouldBeFalse();
        profile.Account.ShouldBeNull();
    }

    [Fact]
    public async Task WriteProfileWritesTheAccountBlockBesideThePair()
    {
        ParkedProfile profile = await _store.EnsureFolderAsync(AccountEmail.Parse("a@example.com").Value, TestContext.Current.CancellationToken);

        await _store.WriteProfileAsync(profile.FolderPath, OAuthAccountBlock.FromJson(AccountJson("a@example.com")), TestContext.Current.CancellationToken);

        JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(profile.FolderPath, "profile.json"), TestContext.Current.CancellationToken))!["emailAddress"]!.GetValue<string>().ShouldBe("a@example.com");
    }

    [Fact]
    public async Task DeleteFolderRefusesAPathItDidNotDiscover()
    {
        string folder = await FolderWithProfileAsync("a@example.com", "a@example.com");

        await Should.ThrowAsync<InvalidOperationException>(() => _store.DeleteFolderAsync(folder, TestContext.Current.CancellationToken));
        Directory.Exists(folder).ShouldBeTrue();

        _ = await _store.ListAsync(TestContext.Current.CancellationToken);
        await _store.DeleteFolderAsync(folder, TestContext.Current.CancellationToken);
        Directory.Exists(folder).ShouldBeFalse();
    }

    [Fact]
    public async Task PruneLoginResidueKeepsThePairAndTheProfileOnly()
    {
        string folder = Path.Combine(_profilesRoot, "b@example.com");
        Directory.CreateDirectory(Path.Combine(folder, "projects"));
        JsonObject stateFile = new() { ["numStartups"] = 1, ["oauthAccount"] = AccountJson("b@example.com") };
        await File.WriteAllTextAsync(Path.Combine(folder, ".claude.json"), stateFile.ToJsonString(), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(folder, "settings.json"), "{}", TestContext.Current.CancellationToken);
        await CredentialFiles.WriteAsync(folder, "refresh-b", TestContext.Current.CancellationToken);

        await _store.PruneLoginResidueAsync(folder, TestContext.Current.CancellationToken);

        Directory.GetFileSystemEntries(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal)
            .ShouldBe([".credentials.json", "profile.json"]);
        JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "profile.json"), TestContext.Current.CancellationToken))!["emailAddress"]!.GetValue<string>().ShouldBe("b@example.com");
    }

    [Fact]
    public async Task PruneLoginResidueKeepsTheSupersededRecordTheEscapeHatchWroteJustBeforeIt()
    {
        // The escape hatch writes the record and then logs in into this very
        // folder, so the prune that follows the login is what would delete the
        // one statement that the other side still holds a family of it.
        string folder = Path.Combine(_profilesRoot, "b@example.com");
        Directory.CreateDirectory(folder);
        await SupersededFamilyFile.WriteAsync(
            folder,
            new HolderRecord(SideName.Wsl, CredentialFiles.Pair("refresh-b-on-wsl").Fingerprint, DateTimeOffset.UnixEpoch),
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(folder, "settings.json"), "{}", TestContext.Current.CancellationToken);
        await CredentialFiles.WriteAsync(folder, "refresh-b-fresh", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(folder, "profile.json"), AccountJson("b@example.com").ToJsonString(), TestContext.Current.CancellationToken);

        await _store.PruneLoginResidueAsync(folder, TestContext.Current.CancellationToken);

        (await SupersededFamilyFile.ReadAsync(folder, TestContext.Current.CancellationToken))!.Side.ShouldBe(SideName.Wsl);
        File.Exists(Path.Combine(folder, "settings.json")).ShouldBeFalse();
    }

    [Fact]
    public async Task AdoptingAFreshLoginRewritesTheProfileBeforeItPrunes()
    {
        // A folder logged in once as one account and just logged in again as
        // another: the stale profile is exactly what must not survive.
        string folder = await FolderWithProfileAsync("c@example.com", "stale@example.com");
        JsonObject stateFile = new() { ["numStartups"] = 1, ["oauthAccount"] = AccountJson("c@example.com") };
        await File.WriteAllTextAsync(Path.Combine(folder, ".claude.json"), stateFile.ToJsonString(), TestContext.Current.CancellationToken);
        await CredentialFiles.WriteAsync(folder, "refresh-c", TestContext.Current.CancellationToken);

        bool adopted = await _store.AdoptFreshLoginAsync(folder, TestContext.Current.CancellationToken);

        adopted.ShouldBeTrue();
        JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "profile.json"), TestContext.Current.CancellationToken))!["emailAddress"]!
            .GetValue<string>().ShouldBe("c@example.com");
        // The state file is gone, so the rewrite can only have read it first.
        Directory.GetFileSystemEntries(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal)
            .ShouldBe([".credentials.json", "profile.json"]);
    }

    [Fact]
    public async Task AdoptingALoginThatNamesNoAccountWritesNothingAndPrunesNothing()
    {
        // Better a folder still holding its residue than one whose only copy of the
        // identity was deleted on the way to writing nothing.
        string folder = await FolderWithProfileAsync("d@example.com", "stale@example.com");
        await File.WriteAllTextAsync(Path.Combine(folder, ".claude.json"), "{\"numStartups\":1}", TestContext.Current.CancellationToken);

        bool adopted = await _store.AdoptFreshLoginAsync(folder, TestContext.Current.CancellationToken);

        adopted.ShouldBeFalse();
        File.Exists(Path.Combine(folder, ".claude.json")).ShouldBeTrue();
        JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "profile.json"), TestContext.Current.CancellationToken))!["emailAddress"]!
            .GetValue<string>().ShouldBe("stale@example.com");
    }

    public void Dispose()
    {
        if (Directory.Exists(_profilesRoot))
        {
            Directory.Delete(_profilesRoot, recursive: true);
        }
    }
}
