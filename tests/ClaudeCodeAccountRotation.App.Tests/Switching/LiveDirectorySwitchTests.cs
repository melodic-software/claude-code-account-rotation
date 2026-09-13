using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.App.Quota;
using ClaudeCodeAccountRotation.App.Switching;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Ports;
using ClaudeCodeAccountRotation.Core.Switching;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeCodeAccountRotation.App.Tests.Switching;

public sealed class LiveDirectorySwitchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "claude-code-account-rotation-tests", Guid.NewGuid().ToString("N"));
    private readonly string _liveDirectory;
    private readonly string _stateFilePath;
    private readonly string _profilesRoot;
    private readonly string _appData;
    private readonly CannedAuthStatus _cli = new();
    private readonly CredentialMutationGate _gate = new();
    private readonly QuotaState _quota = new();

    public LiveDirectorySwitchTests()
    {
        _liveDirectory = Path.Combine(_root, "live");
        _stateFilePath = Path.Combine(_root, ".claude.json");
        _profilesRoot = Path.Combine(_root, "profiles");
        _appData = Path.Combine(_root, "appdata");
        Directory.CreateDirectory(_liveDirectory);
        Directory.CreateDirectory(_profilesRoot);
    }

    private static AccountEmail Email(string value) => AccountEmail.Parse(value).Value;

    private static JsonObject AccountJson(string email) => new() { ["accountUuid"] = "uuid-" + email, ["emailAddress"] = email };

    /// <summary>A temporary file name in the shape <c>AtomicBytesFile</c> forms it.</summary>
    private static string TemporaryName(string intendedFileName) =>
        "." + intendedFileName + "." + Guid.NewGuid().ToString("N") + ".tmp";

    private async Task WriteStateFileAsync(string email, int startups = 7)
    {
        JsonObject state = new() { ["numStartups"] = startups, ["oauthAccount"] = AccountJson(email), ["trailing"] = "kept" };
        await File.WriteAllTextAsync(_stateFilePath, state.ToJsonString(), TestContext.Current.CancellationToken);
    }

    private async Task<string> ParkedProfileAsync(string email, string refreshToken)
    {
        string folder = Path.Combine(_profilesRoot, email);
        await CredentialFiles.WriteAsync(folder, refreshToken, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(folder, "profile.json"), AccountJson(email).ToJsonString(), TestContext.Current.CancellationToken);
        return folder;
    }

    private LiveDirectorySwitch Switch(
        TimeSpan? lockWait = null,
        TimeSpan? gateTimeout = null,
        ICredentialPairStore? pairs = null,
        ILoginSessionRunner? logins = null)
    {
        SwitchOptions options = new(_liveDirectory, _stateFilePath, _profilesRoot, _appData, lockWait ?? TimeSpan.FromSeconds(2), gateTimeout ?? TimeSpan.FromMilliseconds(200));
        ICredentialPairStore store = pairs ?? new FileSystemCredentialPairStore(_liveDirectory, _profilesRoot, TimeProvider.System);
        ProfileFolderStore folders = new(_profilesRoot);
        return new LiveDirectorySwitch(
            store,
            new ClaudeStateFile(_stateFilePath),
            folders,
            new SwitchJournal(_appData),
            _gate,
            logins ?? new NoLoginRunning(),
            _cli,
            new ManagedLoginPolicyReader(Path.Combine(_root, "managed-settings.json"), static () => null, static () => null),
            new RecoveryFiles(options, store, folders, _quota, NullLogger<RecoveryFiles>.Instance),
            _quota,
            options,
            TimeProvider.System,
            NullLogger<LiveDirectorySwitch>.Instance);
    }

    private async Task<string?> StateFileEmailAsync()
    {
        var node = JsonNode.Parse(await File.ReadAllTextAsync(_stateFilePath, TestContext.Current.CancellationToken));
        return node?["oauthAccount"]?["emailAddress"]?.GetValue<string>();
    }

    [Fact]
    public async Task SwitchParksTheLivePairUnparksTheTargetAndPatchesTheStateFile()
    {
        await CredentialFiles.WriteAsync(_liveDirectory, "refresh-a", TestContext.Current.CancellationToken);
        await WriteStateFileAsync("a@example.com");
        await ParkedProfileAsync("b@example.com", "refresh-b");
        _cli.Email = "b@example.com";

        Result<SwitchOutcome, SwitchRefusal> result = await Switch().SwitchToAsync(Email("b@example.com"), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.ToString() : "");
        result.Value.Now.ShouldBe(Email("b@example.com"));
        result.Value.ParkedAs.ShouldBe(Email("a@example.com"));
        result.Value.IdentityMismatchWarning.ShouldBeFalse();
        (await CredentialFiles.FingerprintAsync(_liveDirectory, TestContext.Current.CancellationToken)).ShouldBe(CredentialFiles.Pair("refresh-b").Fingerprint);
        (await CredentialFiles.FingerprintAsync(Path.Combine(_profilesRoot, "a@example.com"), TestContext.Current.CancellationToken)).ShouldBe(CredentialFiles.Pair("refresh-a").Fingerprint);
        File.Exists(Path.Combine(_profilesRoot, "a@example.com", "profile.json")).ShouldBeTrue();
        (await StateFileEmailAsync()).ShouldBe("b@example.com");
        JsonNode.Parse(await File.ReadAllTextAsync(_stateFilePath, TestContext.Current.CancellationToken))!["trailing"]!.GetValue<string>().ShouldBe("kept");
        File.Exists(Path.Combine(_appData, "state", "switch-journal.json")).ShouldBeFalse();
        Directory.Exists(Path.Combine(_liveDirectory, OAuthRefreshLock.DirectoryName)).ShouldBeFalse();
    }

    [Fact]
    public async Task SwitchIsRefusedWhileARefreshPassIsInFlight()
    {
        // A pass fixes the live account's identity when it starts and reads the live
        // pair between its gated units, so a switch landing between two turns would
        // record the incoming account's figures under the outgoing account's name.
        await CredentialFiles.WriteAsync(_liveDirectory, "refresh-a", TestContext.Current.CancellationToken);
        await WriteStateFileAsync("a@example.com");
        await ParkedProfileAsync("b@example.com", "refresh-b");
        _cli.Email = "b@example.com";
        _quota.TryBeginRun().ShouldBeTrue();

        Result<SwitchOutcome, SwitchRefusal> refused = await Switch().SwitchToAsync(Email("b@example.com"), TestContext.Current.CancellationToken);

        refused.IsFailure.ShouldBeTrue();
        refused.Error.ShouldBe(SwitchRefusal.RefreshInProgress);
        (await CredentialFiles.FingerprintAsync(_liveDirectory, TestContext.Current.CancellationToken)).ShouldBe(CredentialFiles.Pair("refresh-a").Fingerprint);

        _quota.EndRun();

        Result<SwitchOutcome, SwitchRefusal> allowed = await Switch().SwitchToAsync(Email("b@example.com"), TestContext.Current.CancellationToken);

        allowed.IsSuccess.ShouldBeTrue(allowed.IsFailure ? allowed.Error.ToString() : "");
        (await CredentialFiles.FingerprintAsync(_liveDirectory, TestContext.Current.CancellationToken)).ShouldBe(CredentialFiles.Pair("refresh-b").Fingerprint);
    }

    [Fact]
    public async Task SwitchWithoutALivePairOnlyUnparks()
    {
        await WriteStateFileAsync("a@example.com");
        await ParkedProfileAsync("b@example.com", "refresh-b");
        _cli.Email = "b@example.com";

        Result<SwitchOutcome, SwitchRefusal> result = await Switch().SwitchToAsync(Email("b@example.com"), TestContext.Current.CancellationToken);

        result.Value.ParkedAs.ShouldBeNull();
        (await CredentialFiles.FingerprintAsync(_liveDirectory, TestContext.Current.CancellationToken)).ShouldBe(CredentialFiles.Pair("refresh-b").Fingerprint);
    }

    [Fact]
    public async Task ACliReportThatDisagreesIsSurfacedAsAWarning()
    {
        await CredentialFiles.WriteAsync(_liveDirectory, "refresh-a", TestContext.Current.CancellationToken);
        await WriteStateFileAsync("a@example.com");
        await ParkedProfileAsync("b@example.com", "refresh-b");
        _cli.Email = "c@example.com";

        Result<SwitchOutcome, SwitchRefusal> result = await Switch().SwitchToAsync(Email("b@example.com"), TestContext.Current.CancellationToken);

        result.Value.IdentityMismatchWarning.ShouldBeTrue();
    }

    [Fact]
    public async Task SwitchRefusesWhileAFreshRefreshLockIsHeldAndMovesNothing()
    {
        await CredentialFiles.WriteAsync(_liveDirectory, "refresh-a", TestContext.Current.CancellationToken);
        await WriteStateFileAsync("a@example.com");
        await ParkedProfileAsync("b@example.com", "refresh-b");
        Directory.CreateDirectory(Path.Combine(_liveDirectory, OAuthRefreshLock.DirectoryName));

        Result<SwitchOutcome, SwitchRefusal> result = await Switch(lockWait: TimeSpan.FromMilliseconds(300)).SwitchToAsync(Email("b@example.com"), TestContext.Current.CancellationToken);

        result.Error.ShouldBe(SwitchRefusal.RefreshLockPresent);
        (await CredentialFiles.FingerprintAsync(_liveDirectory, TestContext.Current.CancellationToken)).ShouldBe(CredentialFiles.Pair("refresh-a").Fingerprint);
        (await StateFileEmailAsync()).ShouldBe("a@example.com");
    }

    [Fact]
    public async Task SwitchRefusesAnUnknownTarget()
    {
        await CredentialFiles.WriteAsync(_liveDirectory, "refresh-a", TestContext.Current.CancellationToken);
        await WriteStateFileAsync("a@example.com");

        Result<SwitchOutcome, SwitchRefusal> result = await Switch().SwitchToAsync(Email("nobody@example.com"), TestContext.Current.CancellationToken);

        result.Error.ShouldBe(SwitchRefusal.TargetHasNoCredentials);
    }

    [Fact]
    public async Task SwitchRefusesToUnparkAFolderALoginIsRunningAgainst()
    {
        // The login child writes a fresh pair into that folder at a moment nothing
        // here chooses. Unparking the old pair out from under it leaves the account
        // holding two: the one now live, and the one the login writes afterwards.
        await CredentialFiles.WriteAsync(_liveDirectory, "refresh-a", TestContext.Current.CancellationToken);
        await WriteStateFileAsync("a@example.com");
        string folder = await ParkedProfileAsync("b@example.com", "refresh-b");

        Result<SwitchOutcome, SwitchRefusal> result =
            await Switch(logins: new NoLoginRunning(folder)).SwitchToAsync(Email("b@example.com"), TestContext.Current.CancellationToken);

        result.Error.ShouldBe(SwitchRefusal.LoginInProgress);
        (await CredentialFiles.FingerprintAsync(_liveDirectory, TestContext.Current.CancellationToken)).ShouldBe(CredentialFiles.Pair("refresh-a").Fingerprint);
        (await CredentialFiles.FingerprintAsync(folder, TestContext.Current.CancellationToken)).ShouldBe(CredentialFiles.Pair("refresh-b").Fingerprint);
    }

    [Fact]
    public async Task SwitchRefusesToParkIntoAFolderALoginIsRunningAgainst()
    {
        // The other direction: the outgoing account's folder is the one being written.
        await CredentialFiles.WriteAsync(_liveDirectory, "refresh-a", TestContext.Current.CancellationToken);
        await WriteStateFileAsync("a@example.com");
        await ParkedProfileAsync("b@example.com", "refresh-b");
        string outgoingFolder = Path.Combine(_profilesRoot, "a@example.com");

        Result<SwitchOutcome, SwitchRefusal> result =
            await Switch(logins: new NoLoginRunning(outgoingFolder)).SwitchToAsync(Email("b@example.com"), TestContext.Current.CancellationToken);

        result.Error.ShouldBe(SwitchRefusal.LoginInProgress);
        (await CredentialFiles.FingerprintAsync(_liveDirectory, TestContext.Current.CancellationToken)).ShouldBe(CredentialFiles.Pair("refresh-a").Fingerprint);
    }

    [Fact]
    public async Task StartupQuarantinesADuplicateLineageAndBlocksSwitchingUntilCleared()
    {
        await CredentialFiles.WriteAsync(_liveDirectory, "refresh-a", TestContext.Current.CancellationToken);
        await WriteStateFileAsync("a@example.com");
        await ParkedProfileAsync("b@example.com", "refresh-b");
        string duplicateFolder = await ParkedProfileAsync("c@example.com", "refresh-a");

        ReconciliationReport report = await Switch().ReconcileAsync(TestContext.Current.CancellationToken);

        report.Quarantined.ShouldHaveSingleItem().ShouldContain("c@example.com");
        File.Exists(Path.Combine(duplicateFolder, CredentialFiles.FileName)).ShouldBeFalse();
        Directory.GetFiles(Path.Combine(_appData, "quarantine"), "*", SearchOption.AllDirectories).ShouldHaveSingleItem();
        (await CredentialFiles.FingerprintAsync(_liveDirectory, TestContext.Current.CancellationToken)).ShouldBe(CredentialFiles.Pair("refresh-a").Fingerprint);

        Result<SwitchOutcome, SwitchRefusal> blocked = await Switch().SwitchToAsync(Email("b@example.com"), TestContext.Current.CancellationToken);
        blocked.Error.ShouldBe(SwitchRefusal.LiveIdentityUnverified);

        Directory.Delete(Path.Combine(_appData, "quarantine"), recursive: true);
        _cli.Email = "b@example.com";
        (await Switch().SwitchToAsync(Email("b@example.com"), TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task StartupQuarantinesACredentialPairLeftInATemporaryFile()
    {
        // What a crash between this writer's temp write and its rename leaves: a
        // refresh token in a file neither the lineage scan nor the single-holder
        // check would ever match, because both look only for .credentials.json.
        await CredentialFiles.WriteAsync(_liveDirectory, "refresh-a", TestContext.Current.CancellationToken);
        await WriteStateFileAsync("a@example.com");
        string folder = await ParkedProfileAsync("b@example.com", "refresh-b");
        string stranded = Path.Combine(folder, TemporaryName(CredentialFiles.FileName));
        await File.WriteAllTextAsync(stranded, CredentialFiles.Shape("refresh-rotated").ToJsonString(), TestContext.Current.CancellationToken);

        ReconciliationReport report = await Switch().ReconcileAsync(TestContext.Current.CancellationToken);

        File.Exists(stranded).ShouldBeFalse();
        string quarantined = report.Quarantined.ShouldHaveSingleItem();
        quarantined.ShouldContain("-temp-b@example.com");
        (await CredentialFiles.FingerprintAsync(Path.GetDirectoryName(quarantined)!, TestContext.Current.CancellationToken)).ShouldBeNull();
        JsonNode.Parse(await File.ReadAllTextAsync(quarantined, TestContext.Current.CancellationToken))!["claudeAiOauth"]!["refreshToken"]!
            .GetValue<string>().ShouldBe("refresh-rotated");
        report.SwitchingBlocked.ShouldBeTrue();
    }

    [Fact]
    public async Task ASwitchRefusesWhileACredentialTemporaryIsStrandedInPlace()
    {
        // A temporary that could not be moved to quarantine holds a refresh token
        // that neither the journal nor the quarantine knows about, so neither of
        // the switch's other guards sees it.
        await CredentialFiles.WriteAsync(_liveDirectory, "refresh-a", TestContext.Current.CancellationToken);
        await WriteStateFileAsync("a@example.com");
        await ParkedProfileAsync("b@example.com", "refresh-b");
        _cli.Email = "b@example.com";
        string stranded = Path.Combine(_liveDirectory, TemporaryName(CredentialFiles.FileName));
        await File.WriteAllTextAsync(stranded, CredentialFiles.Shape("refresh-rotated").ToJsonString(), TestContext.Current.CancellationToken);

        Result<SwitchOutcome, SwitchRefusal> blocked = await Switch().SwitchToAsync(Email("b@example.com"), TestContext.Current.CancellationToken);

        blocked.Error.ShouldBe(SwitchRefusal.LiveIdentityUnverified);
        (await CredentialFiles.FingerprintAsync(_liveDirectory, TestContext.Current.CancellationToken)).ShouldBe(CredentialFiles.Pair("refresh-a").Fingerprint);

        // Cleared by hand, as the banner tells the operator to: the switch runs.
        File.Delete(stranded);
        (await Switch().SwitchToAsync(Email("b@example.com"), TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task ANonCredentialTemporaryDoesNotBlockASwitch()
    {
        await CredentialFiles.WriteAsync(_liveDirectory, "refresh-a", TestContext.Current.CancellationToken);
        await WriteStateFileAsync("a@example.com");
        await ParkedProfileAsync("b@example.com", "refresh-b");
        _cli.Email = "b@example.com";
        await File.WriteAllTextAsync(
            Path.Combine(_liveDirectory, TemporaryName(".claude.json")),
            """{"numStartups":3}""",
            TestContext.Current.CancellationToken);

        (await Switch().SwitchToAsync(Email("b@example.com"), TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task StartupQuarantinesACredentialTemporaryThatIsTruncated()
    {
        // Power loss between the write and the flush leaves bytes that are not
        // valid JSON, and those bytes can still be the only copy of a rotated
        // refresh token. The name says what was being written; the contents do not.
        await CredentialFiles.WriteAsync(_liveDirectory, "refresh-a", TestContext.Current.CancellationToken);
        await WriteStateFileAsync("a@example.com");
        string folder = await ParkedProfileAsync("b@example.com", "refresh-b");
        string truncated = Path.Combine(folder, TemporaryName(CredentialFiles.FileName));
        await File.WriteAllTextAsync(truncated, """{"claudeAiOauth":{"accessToken":"acc","refreshTok""", TestContext.Current.CancellationToken);

        ReconciliationReport report = await Switch().ReconcileAsync(TestContext.Current.CancellationToken);

        File.Exists(truncated).ShouldBeFalse();
        string quarantined = report.Quarantined.ShouldHaveSingleItem();
        (await File.ReadAllTextAsync(quarantined, TestContext.Current.CancellationToken)).ShouldContain("refreshTok");
        report.SwitchingBlocked.ShouldBeTrue();
    }

    [Fact]
    public async Task ADirectoryThatCannotBeListedCostsOnlyItself()
    {
        // The sweep enumerates lazily, so an unlistable directory used to throw
        // from the foreach header, past every guard, and take the host's start
        // with it. Guarding the whole sweep instead would be worse than the crash:
        // one unreadable profile folder would silently skip the live directory
        // too, and a switch would proceed over a stranded credential temporary.
        await CredentialFiles.WriteAsync(_liveDirectory, "refresh-a", TestContext.Current.CancellationToken);
        await WriteStateFileAsync("a@example.com");
        string stranded = Path.Combine(_liveDirectory, TemporaryName(CredentialFiles.FileName));
        await File.WriteAllTextAsync(stranded, CredentialFiles.Shape("refresh-rotated").ToJsonString(), TestContext.Current.CancellationToken);
        string unlistable = Path.Combine(_profilesRoot, "b@example.com");
        Directory.CreateDirectory(unlistable);
        DenyListing(unlistable);
        try
        {
            // Pinned: without a real throw here the rest of this test asserts nothing.
            Should.Throw<UnauthorizedAccessException>(() => Directory.EnumerateFiles(unlistable, "*").ToList());

            ReconciliationReport report = await Switch().ReconcileAsync(TestContext.Current.CancellationToken);

            report.JournalOutcome.ShouldBe("no open journal");
            // The live directory was still swept despite the unreadable profile folder.
            File.Exists(stranded).ShouldBeFalse();
            report.Quarantined.ShouldHaveSingleItem().ShouldContain("-temp-live");
            report.SwitchingBlocked.ShouldBeTrue();
        }
        finally
        {
            // Before Dispose deletes the tree, or the cleanup fails too.
            AllowListing(unlistable);
        }
    }

    private static void DenyListing(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            DenyListingOnWindows(directory);
        }
        else
        {
            File.SetUnixFileMode(directory, UnixFileMode.None);
        }
    }

    private static void AllowListing(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            AllowListingOnWindows(directory);
        }
        else
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void DenyListingOnWindows(string directory)
    {
        DirectoryInfo info = new(directory);
        System.Security.AccessControl.DirectorySecurity security = info.GetAccessControl();
        security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            System.Security.Principal.WindowsIdentity.GetCurrent().User!,
            System.Security.AccessControl.FileSystemRights.ListDirectory,
            System.Security.AccessControl.AccessControlType.Deny));
        info.SetAccessControl(security);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void AllowListingOnWindows(string directory)
    {
        DirectoryInfo info = new(directory);
        System.Security.AccessControl.DirectorySecurity security = info.GetAccessControl();
        security.RemoveAccessRuleAll(new System.Security.AccessControl.FileSystemAccessRule(
            System.Security.Principal.WindowsIdentity.GetCurrent().User!,
            System.Security.AccessControl.FileSystemRights.ListDirectory,
            System.Security.AccessControl.AccessControlType.Deny));
        info.SetAccessControl(security);
    }

    [Fact]
    public async Task StartupDeletesATemporaryFileThatHoldsNoCredentialPair()
    {
        await CredentialFiles.WriteAsync(_liveDirectory, "refresh-a", TestContext.Current.CancellationToken);
        await WriteStateFileAsync("a@example.com");
        string residue = Path.Combine(_liveDirectory, TemporaryName(".claude.json"));
        await File.WriteAllTextAsync(residue, """{"numStartups":3}""", TestContext.Current.CancellationToken);
        // Not this writer's name shape, so it is another program's file and is left alone.
        string foreign = Path.Combine(_liveDirectory, "something.tmp");
        await File.WriteAllTextAsync(foreign, "not ours", TestContext.Current.CancellationToken);

        ReconciliationReport report = await Switch().ReconcileAsync(TestContext.Current.CancellationToken);

        File.Exists(residue).ShouldBeFalse();
        File.Exists(foreign).ShouldBeTrue();
        report.Quarantined.ShouldBeEmpty();
        report.SwitchingBlocked.ShouldBeFalse();
    }

    [Fact]
    public async Task TheSweepReachesTheLiveDirectoryTheProfilesAndAppData()
    {
        await CredentialFiles.WriteAsync(_liveDirectory, "refresh-a", TestContext.Current.CancellationToken);
        await WriteStateFileAsync("a@example.com");
        string folder = await ParkedProfileAsync("b@example.com", "refresh-b");
        Directory.CreateDirectory(Path.Combine(_appData, "recovery"));
        string[] stranded =
        [
            Path.Combine(_liveDirectory, TemporaryName(CredentialFiles.FileName)),
            Path.Combine(folder, TemporaryName(CredentialFiles.FileName)),
            Path.Combine(_appData, "recovery", TemporaryName("b.credentials.json")),
        ];
        foreach ((string path, int index) in stranded.Select(static (path, index) => (path, index)))
        {
            await File.WriteAllTextAsync(path, CredentialFiles.Shape("rotated-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToJsonString(), TestContext.Current.CancellationToken);
        }

        ReconciliationReport report = await Switch().ReconcileAsync(TestContext.Current.CancellationToken);

        report.Quarantined.Count.ShouldBe(3);
        stranded.ShouldAllBe(path => !File.Exists(path));
    }

    [Fact]
    public async Task ACrashBetweenUnparkAndPatchIsReconciledAtStartup()
    {
        // Files as the switch leaves them after the unpark: b's pair is live, a's is parked,
        // but the state file still names a and the journal is open at Unparked.
        await CredentialFiles.WriteAsync(_liveDirectory, "refresh-b", TestContext.Current.CancellationToken);
        await WriteStateFileAsync("a@example.com");
        string folderA = await ParkedProfileAsync("a@example.com", "refresh-a");
        string folderB = Path.Combine(_profilesRoot, "b@example.com");
        Directory.CreateDirectory(folderB);
        await File.WriteAllTextAsync(Path.Combine(folderB, "profile.json"), AccountJson("b@example.com").ToJsonString(), TestContext.Current.CancellationToken);
        SwitchJournal journal = new(_appData);
        await journal.WriteAsync(new SwitchJournalEntry(
            Email("a@example.com"), CredentialFiles.Pair("refresh-a").Fingerprint, folderA,
            Email("b@example.com"), CredentialFiles.Pair("refresh-b").Fingerprint, folderB,
            SwitchStep.Unparked, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);

        Result<SwitchOutcome, SwitchRefusal> beforeReconcile = await Switch().SwitchToAsync(Email("a@example.com"), TestContext.Current.CancellationToken);
        beforeReconcile.Error.ShouldBe(SwitchRefusal.LiveIdentityUnverified);

        ReconciliationReport report = await Switch().ReconcileAsync(TestContext.Current.CancellationToken);

        report.JournalOutcome.ShouldContain("completed");
        (await StateFileEmailAsync()).ShouldBe("b@example.com");
        (await journal.ReadOpenAsync(TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task ACrashAfterParkingIsUnwoundAtStartup()
    {
        // Live is empty, a's pair sits parked, b's pair still parked; journal at Parked.
        await WriteStateFileAsync("a@example.com");
        string folderA = await ParkedProfileAsync("a@example.com", "refresh-a");
        string folderB = await ParkedProfileAsync("b@example.com", "refresh-b");
        await new SwitchJournal(_appData).WriteAsync(new SwitchJournalEntry(
            Email("a@example.com"), CredentialFiles.Pair("refresh-a").Fingerprint, folderA,
            Email("b@example.com"), CredentialFiles.Pair("refresh-b").Fingerprint, folderB,
            SwitchStep.Parked, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);

        ReconciliationReport report = await Switch().ReconcileAsync(TestContext.Current.CancellationToken);

        report.JournalOutcome.ShouldContain("unwound");
        (await CredentialFiles.FingerprintAsync(_liveDirectory, TestContext.Current.CancellationToken)).ShouldBe(CredentialFiles.Pair("refresh-a").Fingerprint);
        (await CredentialFiles.FingerprintAsync(folderB, TestContext.Current.CancellationToken)).ShouldBe(CredentialFiles.Pair("refresh-b").Fingerprint);
        (await StateFileEmailAsync()).ShouldBe("a@example.com");
    }

    [Fact]
    public async Task AnUnwindWaitsForTheRefreshLockAndMovesNothingWhileASessionHoldsIt()
    {
        // Same half-done switch as above, but a session is mid-refresh: the restore must not
        // race it, so reconciliation reports the block and leaves every file where it is.
        await WriteStateFileAsync("a@example.com");
        string folderA = await ParkedProfileAsync("a@example.com", "refresh-a");
        string folderB = await ParkedProfileAsync("b@example.com", "refresh-b");
        SwitchJournal journal = new(_appData);
        await journal.WriteAsync(new SwitchJournalEntry(
            Email("a@example.com"), CredentialFiles.Pair("refresh-a").Fingerprint, folderA,
            Email("b@example.com"), CredentialFiles.Pair("refresh-b").Fingerprint, folderB,
            SwitchStep.Parked, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
        Directory.CreateDirectory(Path.Combine(_liveDirectory, OAuthRefreshLock.DirectoryName));

        ReconciliationReport report = await Switch(lockWait: TimeSpan.FromMilliseconds(300)).ReconcileAsync(TestContext.Current.CancellationToken);

        report.SwitchingBlocked.ShouldBeTrue();
        report.JournalOutcome.ShouldContain("not unwound yet");
        (await CredentialFiles.FingerprintAsync(_liveDirectory, TestContext.Current.CancellationToken)).ShouldBeNull();
        (await CredentialFiles.FingerprintAsync(folderA, TestContext.Current.CancellationToken)).ShouldBe(CredentialFiles.Pair("refresh-a").Fingerprint);
        (await journal.ReadOpenAsync(TestContext.Current.CancellationToken)).ShouldNotBeNull();
    }

    [Fact]
    public async Task ConcurrentSwitchesSerializeAndLeaveOneHolderPerLineage()
    {
        await CredentialFiles.WriteAsync(_liveDirectory, "refresh-a", TestContext.Current.CancellationToken);
        await WriteStateFileAsync("a@example.com");
        await ParkedProfileAsync("b@example.com", "refresh-b");
        await ParkedProfileAsync("c@example.com", "refresh-c");
        // A zero gate wait is the endpoint's posture: a second switch during one is a 409, not a queue.
        LiveDirectorySwitch executor = Switch(gateTimeout: TimeSpan.Zero);
        _cli.Email = "b@example.com";

        Task<Result<SwitchOutcome, SwitchRefusal>> toB = executor.SwitchToAsync(Email("b@example.com"), TestContext.Current.CancellationToken);
        Task<Result<SwitchOutcome, SwitchRefusal>> toC = executor.SwitchToAsync(Email("c@example.com"), TestContext.Current.CancellationToken);
        Result<SwitchOutcome, SwitchRefusal>[] results = await Task.WhenAll(toB, toC);

        results.Count(static result => result.IsSuccess).ShouldBe(1);
        results.Single(static result => result.IsFailure).Error.ShouldBe(SwitchRefusal.MutationInProgress);
        List<RefreshTokenFingerprint> fingerprints = [];
        foreach (string directory in new[] { _liveDirectory, Path.Combine(_profilesRoot, "a@example.com"), Path.Combine(_profilesRoot, "b@example.com"), Path.Combine(_profilesRoot, "c@example.com") })
        {
            if (await CredentialFiles.FingerprintAsync(directory, TestContext.Current.CancellationToken) is RefreshTokenFingerprint fingerprint)
            {
                fingerprints.Add(fingerprint);
            }
        }

        fingerprints.Count.ShouldBe(3);
        fingerprints.Distinct().Count().ShouldBe(3);
    }

    [Fact]
    public async Task ARequestAbortedAfterTheParkStillCompletesTheUnpark()
    {
        // The browser can abort the request at any moment (tab closed, navigation); once the
        // first move has happened, the switch must run to completion rather than leave the
        // live directory with no credential file.
        await CredentialFiles.WriteAsync(_liveDirectory, "refresh-a", TestContext.Current.CancellationToken);
        await WriteStateFileAsync("a@example.com");
        await ParkedProfileAsync("b@example.com", "refresh-b");
        _cli.Email = "b@example.com";
        using CancellationTokenSource request = new();
        DelegatingPairStore pairs = new(new FileSystemCredentialPairStore(_liveDirectory, _profilesRoot, TimeProvider.System))
        {
            AfterPark = request.Cancel,
        };

        Result<SwitchOutcome, SwitchRefusal> result = await Switch(pairs: pairs).SwitchToAsync(Email("b@example.com"), request.Token);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.ToString() : "");
        (await CredentialFiles.FingerprintAsync(_liveDirectory, TestContext.Current.CancellationToken)).ShouldBe(CredentialFiles.Pair("refresh-b").Fingerprint);
        (await StateFileEmailAsync()).ShouldBe("b@example.com");
        File.Exists(Path.Combine(_appData, "state", "switch-journal.json")).ShouldBeFalse();
    }

    [Fact]
    public async Task TheOwnerRecordIsReboundWhenTheLivePairRotatesUnderTheSameAccount()
    {
        // The CLI rotates the refresh token on its next refresh, which on a real machine
        // happened within seconds of every unpark. The record must follow the rotation
        // while the state file still names the recorded owner, or the guard it feeds
        // silently stops applying.
        await CredentialFiles.WriteAsync(_liveDirectory, "refresh-a", TestContext.Current.CancellationToken);
        await WriteStateFileAsync("a@example.com");
        await ParkedProfileAsync("b@example.com", "refresh-b");
        _cli.Email = "b@example.com";
        (await Switch().SwitchToAsync(Email("b@example.com"), TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
        await CredentialFiles.WriteAsync(_liveDirectory, "refresh-b-rotated", TestContext.Current.CancellationToken);

        Result<SwitchOutcome, SwitchRefusal> refused = await Switch().SwitchToAsync(Email("b@example.com"), TestContext.Current.CancellationToken);

        refused.Error.ShouldBe(SwitchRefusal.AlreadyOnTarget);
        JsonObject record = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_appData, "state", "live-owner.json"), TestContext.Current.CancellationToken))!.AsObject();
        record["fingerprint"]!.GetValue<string>().ShouldBe(CredentialFiles.Pair("refresh-b-rotated").Fingerprint.Sha256Hex);
        record["email"]!.GetValue<string>().ShouldBe("b@example.com");
    }

    [Fact]
    public async Task AStaleBlockASessionWroteBackIsRepatchedFromTheOwnersProfile()
    {
        // Seen on the real machine: minutes after a switch, a running session rewrote the state
        // file from memory, naming the outgoing account with a block stamped before the switch.
        await CredentialFiles.WriteAsync(_liveDirectory, "refresh-a", TestContext.Current.CancellationToken);
        await WriteStateFileAsync("a@example.com");
        await ParkedProfileAsync("b@example.com", "refresh-b");
        _cli.Email = "b@example.com";
        (await Switch().SwitchToAsync(Email("b@example.com"), TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
        await WriteStateFileAsync("a@example.com", startups: 8);

        IdentityRepair outcome = await Switch().RepairStaleIdentityAsync(TestContext.Current.CancellationToken);

        outcome.ShouldBe(IdentityRepair.Repatched);
        (await StateFileEmailAsync()).ShouldBe("b@example.com");
        JsonNode.Parse(await File.ReadAllTextAsync(_stateFilePath, TestContext.Current.CancellationToken))!["numStartups"]!.GetValue<int>().ShouldBe(8);
        (await Switch().RepairStaleIdentityAsync(TestContext.Current.CancellationToken)).ShouldBe(IdentityRepair.NotNeeded);
    }

    [Fact]
    public async Task ABlockTheCliStampedAfterTheSwitchIsALoginAndIsAdopted()
    {
        await CredentialFiles.WriteAsync(_liveDirectory, "refresh-a", TestContext.Current.CancellationToken);
        await WriteStateFileAsync("a@example.com");
        await ParkedProfileAsync("b@example.com", "refresh-b");
        _cli.Email = "b@example.com";
        (await Switch().SwitchToAsync(Email("b@example.com"), TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
        // The user logged in as c through the CLI: a new pair and a block fetched after the record.
        await CredentialFiles.WriteAsync(_liveDirectory, "refresh-c", TestContext.Current.CancellationToken);
        JsonObject fresh = AccountJson("c@example.com");
        fresh["profileFetchedAt"] = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds();
        JsonObject state = new() { ["numStartups"] = 9, ["oauthAccount"] = fresh };
        await File.WriteAllTextAsync(_stateFilePath, state.ToJsonString(), TestContext.Current.CancellationToken);

        IdentityRepair outcome = await Switch().RepairStaleIdentityAsync(TestContext.Current.CancellationToken);

        outcome.ShouldBe(IdentityRepair.NotNeeded);
        (await StateFileEmailAsync()).ShouldBe("c@example.com");
        File.Exists(Path.Combine(_appData, "state", "live-owner.json")).ShouldBeFalse("a login releases the record");
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"fingerprint": 12, "email": ["x"], "at": 5}""")]
    [InlineData("""{"fingerprint": "0000", "email": "a@example.com", "at": 5}""")]
    public async Task ACorruptOwnerRecordNeverFailsTheSwitch(string record)
    {
        // A hand-edited or truncated record reads as no recorded owner (or, when its strings are
        // intact and name the live account, as a plain rotation); it never throws out of the switch.
        await CredentialFiles.WriteAsync(_liveDirectory, "refresh-a", TestContext.Current.CancellationToken);
        await WriteStateFileAsync("a@example.com");
        await ParkedProfileAsync("b@example.com", "refresh-b");
        Directory.CreateDirectory(Path.Combine(_appData, "state"));
        await File.WriteAllTextAsync(Path.Combine(_appData, "state", "live-owner.json"), record, TestContext.Current.CancellationToken);
        _cli.Email = "b@example.com";

        Result<SwitchOutcome, SwitchRefusal> result = await Switch().SwitchToAsync(Email("b@example.com"), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.ToString() : "");
        (await CredentialFiles.FingerprintAsync(_liveDirectory, TestContext.Current.CancellationToken)).ShouldBe(CredentialFiles.Pair("refresh-b").Fingerprint);
    }

    [Fact]
    public async Task AJournaledSwitchWhoseLivePairHasRotatedIsStillCompletedAtStartup()
    {
        // As on the real machine: the unpark happened, a session refreshed and rotated the
        // live token before the tool came back, so the fingerprints no longer match the
        // journal, but the folder layout is unambiguous (live holds a pair, b's folder
        // holds none, a's folder holds a's pair).
        await CredentialFiles.WriteAsync(_liveDirectory, "refresh-b-rotated", TestContext.Current.CancellationToken);
        await WriteStateFileAsync("a@example.com");
        string folderA = await ParkedProfileAsync("a@example.com", "refresh-a");
        string folderB = Path.Combine(_profilesRoot, "b@example.com");
        Directory.CreateDirectory(folderB);
        await File.WriteAllTextAsync(Path.Combine(folderB, "profile.json"), AccountJson("b@example.com").ToJsonString(), TestContext.Current.CancellationToken);
        SwitchJournal journal = new(_appData);
        await journal.WriteAsync(new SwitchJournalEntry(
            Email("a@example.com"), CredentialFiles.Pair("refresh-a").Fingerprint, folderA,
            Email("b@example.com"), CredentialFiles.Pair("refresh-b").Fingerprint, folderB,
            SwitchStep.Unparked, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);

        ReconciliationReport report = await Switch().ReconcileAsync(TestContext.Current.CancellationToken);

        report.JournalOutcome.ShouldContain("completed");
        report.SwitchingBlocked.ShouldBeFalse();
        (await StateFileEmailAsync()).ShouldBe("b@example.com");
        (await journal.ReadOpenAsync(TestContext.Current.CancellationToken)).ShouldBeNull();
        JsonObject record = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_appData, "state", "live-owner.json"), TestContext.Current.CancellationToken))!.AsObject();
        record["fingerprint"]!.GetValue<string>().ShouldBe(CredentialFiles.Pair("refresh-b-rotated").Fingerprint.Sha256Hex);
    }

    [Fact]
    public async Task ASwitchToAFolderStrandedInRecoveryIsRefused()
    {
        await CredentialFiles.WriteAsync(_liveDirectory, "refresh-a", TestContext.Current.CancellationToken);
        await WriteStateFileAsync("a@example.com");
        await ParkedProfileAsync("b@example.com", "refresh-b");
        // A refresh rotated b's pair and could not write it back, so the file still
        // in b's folder holds the refresh token the token endpoint already killed.
        Directory.CreateDirectory(Path.Combine(_appData, "recovery"));
        await File.WriteAllTextAsync(
            Path.Combine(_appData, "recovery", "b@example.com.credentials.json"),
            "{}",
            TestContext.Current.CancellationToken);

        Result<SwitchOutcome, SwitchRefusal> result = await Switch().SwitchToAsync(Email("b@example.com"), TestContext.Current.CancellationToken);

        result.Error.ShouldBe(SwitchRefusal.TargetStrandedInRecovery);
        // Nothing moved: the restore still has the parked pair to compare against.
        (await CredentialFiles.FingerprintAsync(_liveDirectory, TestContext.Current.CancellationToken))
            .ShouldBe(CredentialFiles.Pair("refresh-a").Fingerprint);
        (await CredentialFiles.FingerprintAsync(Path.Combine(_profilesRoot, "b@example.com"), TestContext.Current.CancellationToken))
            .ShouldBe(CredentialFiles.Pair("refresh-b").Fingerprint);
    }

    public void Dispose()
    {
        _gate.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>Forwards to the real store and runs a hook after the park, the seam a request abort needs.</summary>
    private sealed class DelegatingPairStore(ICredentialPairStore inner) : ICredentialPairStore
    {
        public Action? AfterPark { get; init; }

        public Task<CredentialPair?> ReadLiveAsync(CancellationToken cancellationToken) => inner.ReadLiveAsync(cancellationToken);

        public Task<CredentialPair?> ReadParkedAsync(string folderPath, CancellationToken cancellationToken) => inner.ReadParkedAsync(folderPath, cancellationToken);

        public async Task MoveLiveToParkedAsync(string folderPath, CancellationToken cancellationToken)
        {
            await inner.MoveLiveToParkedAsync(folderPath, cancellationToken);
            AfterPark?.Invoke();
        }

        public Task MoveParkedToLiveAsync(string folderPath, CancellationToken cancellationToken) => inner.MoveParkedToLiveAsync(folderPath, cancellationToken);

        public Task MoveParkedToQuarantineAsync(string folderPath, string destinationDirectory, CancellationToken cancellationToken) => inner.MoveParkedToQuarantineAsync(folderPath, destinationDirectory, cancellationToken);

        public Task<Result<Unit, string>> WriteParkedAsync(string folderPath, CredentialPair pair, RefreshTokenFingerprint expected, CancellationToken cancellationToken) => inner.WriteParkedAsync(folderPath, pair, expected, cancellationToken);

        public Task<Result<IAsyncDisposable, string>> AcquireRefreshLockAsync(TimeSpan waitBound, CancellationToken cancellationToken) => inner.AcquireRefreshLockAsync(waitBound, cancellationToken);

        public string? FreshLockFileName(TimeSpan maxAge) => inner.FreshLockFileName(maxAge);
    }

    /// <summary>A runner that owns the folders named, and none other.</summary>
    private sealed class NoLoginRunning(params string[] folders) : ILoginSessionRunner
    {
        public Task<Result<LoginSession, string>> StartAsync(AccountEmail email, string folderPath, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<LoginSession, string>> SubmitCodeAsync(LoginSessionId id, string code, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public LoginSession? Status(LoginSessionId id) => null;

        public bool IsRunningAgainst(string folderPath) =>
            folders.Contains(Path.GetFullPath(folderPath), StringComparer.OrdinalIgnoreCase);
    }

    private sealed class CannedAuthStatus : IClaudeCliAuthStatus
    {
        public string? Email { get; set; }

        public Task<Result<ClaudeAuthStatus, string>> ReadAsync(string? configDirectory, CancellationToken cancellationToken)
        {
            // Like the real adapter, honor the token: a switch that verifies under an aborted
            // request token would throw here instead of returning its outcome.
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Result<ClaudeAuthStatus, string>.Success(new ClaudeAuthStatus(true, Email, "claude.ai", "Personal", "max", null)));
        }
    }
}
