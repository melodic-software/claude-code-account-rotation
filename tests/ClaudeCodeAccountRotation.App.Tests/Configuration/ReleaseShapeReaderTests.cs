using System.Globalization;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.App.Configuration;
using ClaudeCodeAccountRotation.App.Quota;
using ClaudeCodeAccountRotation.App.Switching;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Accounts;
using ClaudeCodeAccountRotation.Core.Configuration;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Ports;
using ClaudeCodeAccountRotation.Core.Switching;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeCodeAccountRotation.App.Tests.Configuration;

/// <summary>
/// A v1.0.0 file, loaded by the readers a later release ships. The upgrade
/// procedure promises that load does not rewrite <c>config.json</c> and that
/// an owner record and an import journal written before
/// <c>RequestedFingerprint</c> existed still answer.
/// </summary>
public sealed class ReleaseShapeReaderTests : IDisposable
{
    private const string LiveEmail = "a@example.com";
    private const string OwnerEmail = "b@example.com";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "claude-code-account-rotation-tests", Guid.NewGuid().ToString("N"));
    private readonly CredentialMutationGate _gate = new();

    public void Dispose()
    {
        _gate.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task AVersionOneFileLoadsWithoutChangingItsBytesAndStillNamesTheOwner()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string home = Path.Combine(_root, "home");
        Directory.CreateDirectory(home);
        ClaudeCodeAccountRotationConfiguration defaults = ConfigurationDefaults.ForUser(home, Path.Combine(home, "AppData", "Local"), claudeConfigDirectory: null);

        string liveDirectory = Path.Combine(_root, "configured", "live");
        string stateFilePath = Path.Combine(_root, "configured", "state", ".claude.json");
        string profilesRoot = Path.Combine(_root, "configured", "profiles");
        string appData = Path.Combine(_root, "configured", "appdata");
        Directory.CreateDirectory(liveDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(stateFilePath)!);
        Directory.CreateDirectory(profilesRoot);
        Directory.CreateDirectory(Path.Combine(appData, "state"));

        string configPath = Path.Combine(appData, "config.json");
        JsonObject config = new()
        {
            ["liveConfigDirectory"] = liveDirectory,
            ["stateFilePath"] = stateFilePath,
            ["profilesRoot"] = profilesRoot,
            ["appDataDirectory"] = appData,
            ["listenPort"] = 48211,
            ["refreshLockWaitSeconds"] = 10,
            ["userAgentProductToken"] = "claude-code-account-rotation",
            ["browserExecutables"] = new JsonObject(),
            ["store"] = new JsonObject { ["shared"] = false },
            ["role"] = "leader",
            ["peers"] = new JsonArray(),
        };
        await File.WriteAllTextAsync(configPath, config.ToJsonString(), cancellationToken);
        byte[] configBytes = await File.ReadAllBytesAsync(configPath, cancellationToken);

        JsonObject roster = new()
        {
            ["accounts"] = new JsonArray
            {
                new JsonObject
                {
                    ["email"] = OwnerEmail,
                    ["alias"] = "Work",
                    ["browser"] = "edge",
                    ["browserProfileDirectory"] = "Profile 1",
                    ["paused"] = true,
                    ["notes"] = "desk",
                },
            },
        };
        await File.WriteAllTextAsync(Path.Combine(appData, RosterFile.FileName), roster.ToJsonString(), cancellationToken);

        await CredentialFiles.WriteAsync(liveDirectory, "refresh-owner", cancellationToken);
        RefreshTokenFingerprint liveFingerprint = CredentialFiles.Pair("refresh-owner").Fingerprint;
        DateTimeOffset recordedAt = new(2026, 9, 22, 0, 0, 0, TimeSpan.Zero);
        JsonObject owner = new()
        {
            ["fingerprint"] = liveFingerprint.Sha256Hex,
            ["email"] = OwnerEmail,
            ["at"] = recordedAt.ToString("O", CultureInfo.InvariantCulture),
        };
        string ownerPath = Path.Combine(appData, "state", "live-owner.json");
        await File.WriteAllTextAsync(ownerPath, owner.ToJsonString(), cancellationToken);
        byte[] ownerBytes = await File.ReadAllBytesAsync(ownerPath, cancellationToken);

        // Newer than the owner record. A reader that ignored the fingerprint and
        // treated this as a later login would release the record; the match has
        // to win first for the owner to still be supplied.
        JsonObject staleAccount = new()
        {
            ["accountUuid"] = "uuid-" + LiveEmail,
            ["emailAddress"] = LiveEmail,
            ["profileFetchedAt"] = recordedAt.AddHours(1).ToUnixTimeMilliseconds(),
        };
        await File.WriteAllTextAsync(
            stateFilePath,
            new JsonObject { ["oauthAccount"] = staleAccount }.ToJsonString(),
            cancellationToken);
        string ownerFolder = Path.Combine(profilesRoot, ProfileFolderName.FromEmail(AccountEmail.Parse(OwnerEmail).Value));
        Directory.CreateDirectory(ownerFolder);
        await File.WriteAllTextAsync(
            Path.Combine(ownerFolder, "profile.json"),
            new JsonObject { ["accountUuid"] = "uuid-" + OwnerEmail, ["emailAddress"] = OwnerEmail }.ToJsonString(),
            cancellationToken);

        JsonObject journal = new()
        {
            ["Incoming"] = OwnerEmail,
            ["IncomingFingerprint"] = liveFingerprint.Sha256Hex,
            ["ClaimedPath"] = Path.Combine(_root, "configured", "claimed"),
            ["ExportPath"] = Path.Combine(_root, "configured", "export"),
            ["Outgoing"] = LiveEmail,
            ["OutgoingFingerprint"] = CredentialFiles.Pair("refresh-other").Fingerprint.Sha256Hex,
            ["IncomingAccount"] = new JsonObject { ["emailAddress"] = OwnerEmail },
            ["OutgoingAccount"] = new JsonObject { ["emailAddress"] = LiveEmail },
            ["StepReached"] = "Exported",
            ["StartedAt"] = recordedAt.ToString("O", CultureInfo.InvariantCulture),
        };
        string journalPath = Path.Combine(appData, "state", "import-journal.json");
        string journalText = journal.ToJsonString();
        journalText.ShouldNotContain("RequestedFingerprint");
        await File.WriteAllTextAsync(journalPath, journalText, cancellationToken);
        byte[] journalBytes = await File.ReadAllBytesAsync(journalPath, cancellationToken);

        Result<ClaudeCodeAccountRotationConfiguration, string> loaded = await ConfigurationFile.LoadOrCreateAsync(configPath, defaults, cancellationToken);

        loaded.IsSuccess.ShouldBeTrue(loaded.IsFailure ? loaded.Error : string.Empty);
        loaded.Value.LiveConfigDirectory.ShouldBe(liveDirectory);
        loaded.Value.StateFilePath.ShouldBe(stateFilePath);
        loaded.Value.ProfilesRoot.ShouldBe(profilesRoot);
        loaded.Value.AppDataDirectory.ShouldBe(appData);
        loaded.Value.ListenPort.ShouldBe(48211);
        loaded.Value.Role.ShouldBe(RotationRole.Leader);
        loaded.Value.SharedStore.ShouldBeFalse();
        (await File.ReadAllBytesAsync(configPath, cancellationToken)).ShouldBe(configBytes);

        using RosterFile rosterFile = new(appData);
        RosterEntry entry = (await rosterFile.ReadAsync(cancellationToken)).Entries.ShouldHaveSingleItem();
        entry.Email.Value.ShouldBe(OwnerEmail);
        entry.Alias.ShouldBe("Work");
        entry.Browser.ShouldBe(BrowserFamily.Edge);
        entry.BrowserProfileDirectory.ShouldBe("Profile 1");
        entry.Paused.ShouldBeTrue();
        entry.Notes.ShouldBe("desk");

        ImportJournalEntry? open = await new ImportJournal(appData).ReadOpenAsync(cancellationToken);
        open.ShouldNotBeNull();
        open.RequestedFingerprint.ShouldBeNull();
        open.Incoming.ShouldBe(AccountEmail.Parse(OwnerEmail).Value);
        open.StepReached.ShouldBe(ImportStep.Exported);
        (await File.ReadAllBytesAsync(journalPath, cancellationToken)).ShouldBe(journalBytes);

        SwitchOptions options = new(liveDirectory, stateFilePath, profilesRoot, appData, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(200));
        ICredentialPairStore pairs = new FileSystemCredentialPairStore(liveDirectory, profilesRoot, TimeProvider.System);
        ProfileFolderStore folders = new(profilesRoot);
        NoLoginRunning logins = new();
        LiveDirectorySwitch repair = new(
            pairs,
            new ClaudeStateFile(stateFilePath),
            folders,
            new SwitchJournal(appData),
            _gate,
            logins,
            new CannedAuthStatus(),
            new ManagedLoginPolicyReader(Path.Combine(_root, "managed-settings.json"), static () => null, static () => null),
            new RecoveryFiles(options, pairs, folders, new QuotaState(), NullLogger<RecoveryFiles>.Instance),
            new QuotaState(),
            new SharedStoreSlots(profilesRoot, enabled: false, _gate, logins, NullLogger<SharedStoreSlots>.Instance),
            options,
            TimeProvider.System,
            NullLogger<LiveDirectorySwitch>.Instance);

        IdentityRepair outcome = await repair.RepairStaleIdentityAsync(cancellationToken);

        outcome.ShouldBe(IdentityRepair.Repatched);
        JsonNode.Parse(await File.ReadAllTextAsync(stateFilePath, cancellationToken))!["oauthAccount"]!["emailAddress"]!.GetValue<string>().ShouldBe(OwnerEmail);
        (await File.ReadAllBytesAsync(ownerPath, cancellationToken)).ShouldBe(ownerBytes);
        (await File.ReadAllBytesAsync(configPath, cancellationToken)).ShouldBe(configBytes);
    }

    private sealed class NoLoginRunning : ILoginSessionRunner
    {
        public Task<Result<LoginSession, string>> StartAsync(AccountEmail email, string folderPath, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<LoginSession, string>> SubmitCodeAsync(LoginSessionId id, string code, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public LoginSession? Status(LoginSessionId id) => throw new NotSupportedException();

        public bool IsRunningAgainst(string folderPath) => false;
    }

    private sealed class CannedAuthStatus : IClaudeCliAuthStatus
    {
        public Task<Result<ClaudeAuthStatus, string>> ReadAsync(string? configDirectory, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
