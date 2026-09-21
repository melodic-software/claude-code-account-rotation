using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.App.Hosting;
using ClaudeCodeAccountRotation.App.Quota;
using ClaudeCodeAccountRotation.App.Switching;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Peers;
using ClaudeCodeAccountRotation.Core.Ports;
using ClaudeCodeAccountRotation.Core.Switching;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeCodeAccountRotation.App.Tests.Switching;

/// <summary>
/// The leader's side of a hand-off over temp roots: its own live directory and
/// state file, the shared store with its <c>.transit/wsl</c> mailbox, and a
/// scripted other side. The coordinator is built directly rather than through
/// HTTP so a test can say what the follower answers and when.
/// </summary>
internal sealed class WslSwitchHarness : IDisposable
{
    public WslSwitchHarness()
    {
        Root = Path.Combine(Path.GetTempPath(), "claude-code-account-rotation-tests", Guid.NewGuid().ToString("N"));
        LiveDirectory = Path.Combine(Root, "live");
        StateFilePath = Path.Combine(Root, ".claude.json");
        Store = Path.Combine(Root, "store");
        AppData = Path.Combine(Root, "appdata");
        Directory.CreateDirectory(LiveDirectory);
        Directory.CreateDirectory(Store);
        Directory.CreateDirectory(AppData);
        Mailbox = FileSystemCredentialPairStore.MailboxPath(Store, SideName.Wsl);
        Side = new FakePeerRotationInstance(Mailbox);
    }

    public string Root { get; }

    public string LiveDirectory { get; }

    public string StateFilePath { get; }

    /// <summary>The profiles root, which for this lane is the store both sides share.</summary>
    public string Store { get; }

    public string AppData { get; }

    public string Mailbox { get; }

    public FakePeerRotationInstance Side { get; }

    public TestClock Clock { get; } = new(new DateTimeOffset(2026, 9, 20, 9, 0, 0, TimeSpan.Zero));

    public CredentialMutationGate Gate { get; } = new();

    public QuotaState Quota { get; } = new();

    public WslSwitchJournal Journal => new(AppData);

    public string JournalPath => Path.Combine(AppData, "state", "wsl-switch-journal.json");

    public string FolderFor(string email) => Path.Combine(Store, email);

    public string ClaimedPath(string email) => Path.Combine(Mailbox, email + FileSystemCredentialPairStore.FileName);

    public string ExportPath(string email) => ClaimedPath(email) + FileSystemCredentialPairStore.IncomingSuffix;

    public string RecordPath(string email) => HolderRecordFile.PathIn(FolderFor(email));

    public string PairPath(string email) => Path.Combine(FolderFor(email), FileSystemCredentialPairStore.FileName);

    public SwitchOptions Options => new(
        LiveDirectory,
        StateFilePath,
        Store,
        AppData,
        RefreshLockWaitBound: TimeSpan.FromMilliseconds(200),
        MutationGateTimeout: TimeSpan.FromSeconds(1));

    /// <summary>
    /// A coordinator over these roots. Built fresh on every call because that is
    /// what a restart is: the crash-table facts write a journal by hand and then
    /// ask a coordinator that has never seen this hand-off to finish it.
    /// </summary>
    public WslSwitch Coordinator(bool withPeer = true, bool sharedStore = true)
    {
        SwitchOptions options = Options;
        var pairs = new FileSystemCredentialPairStore(LiveDirectory, Store, Clock);
        ProfileFolderStore profiles = new(Store);
        // The registry disposes the hosts it owns, and these facts configure
        // none, so the analyzer's scope rule is satisfied by saying so once.
#pragma warning disable CA2000
        PeerRegistry peers = new(withPeer ? [new Peer(Side, null, Store)] : []);
#pragma warning restore CA2000
        return new WslSwitch(
            peers,
            pairs,
            profiles,
            new ClaudeStateFile(StateFilePath),
            new SharedStoreSlots(Store, sharedStore, Gate, NullLogger<SharedStoreSlots>.Instance),
            new WslSwitchJournal(AppData),
            new SwitchJournal(AppData),
            new NoLoginsRunning(),
            new ManagedLoginPolicyReader(Path.Combine(Root, "managed-settings.json"), static () => null, static () => null),
            new RecoveryFiles(options, pairs, profiles, Quota, NullLogger<RecoveryFiles>.Instance, Clock),
            Quota,
            Gate,
            options,
            Clock,
            NullLogger<WslSwitch>.Instance);
    }

    public static AccountEmail Email(string value) => AccountEmail.Parse(value).Value;

    public static JsonObject AccountJson(string email) => new() { ["accountUuid"] = "uuid-" + email, ["emailAddress"] = email };

    /// <summary>The leader's own live pair, which every refused hand-off must leave exactly where it is.</summary>
    public async Task WriteLeaderLiveAsync(string email, string refreshToken, CancellationToken cancellationToken)
    {
        await CredentialFiles.WriteAsync(LiveDirectory, refreshToken, cancellationToken);
        JsonObject state = new() { ["numStartups"] = 5, ["oauthAccount"] = AccountJson(email) };
        await File.WriteAllTextAsync(StateFilePath, state.ToJsonString(), cancellationToken);
    }

    /// <summary>A slot holding its pair: the shape the incoming account's slot has before L2.</summary>
    public async Task<RefreshTokenFingerprint> ParkedSlotAsync(string email, string refreshToken, CancellationToken cancellationToken)
    {
        await IdentifiedSlotAsync(email, cancellationToken);
        await File.WriteAllTextAsync(PairPath(email), CredentialFiles.Shape(refreshToken).ToJsonString(), cancellationToken);
        return CredentialFiles.Pair(refreshToken).Fingerprint;
    }

    /// <summary>A slot with an identity and no pair: what an account live on the other side leaves here.</summary>
    public async Task IdentifiedSlotAsync(string email, CancellationToken cancellationToken)
    {
        string folder = FolderFor(email);
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, ProfileFolderStore.ProfileFileName), AccountJson(email).ToJsonString(), cancellationToken);
    }

    /// <summary>L2's disk effect, written by hand for the crash-table facts.</summary>
    public async Task ClaimBySideAsync(string email, string refreshToken, CancellationToken cancellationToken)
    {
        await IdentifiedSlotAsync(email, cancellationToken);
        Directory.CreateDirectory(Mailbox);
        await File.WriteAllTextAsync(ClaimedPath(email), CredentialFiles.Shape(refreshToken).ToJsonString(), cancellationToken);
        await HolderRecordFile.WriteAsync(
            FolderFor(email),
            new HolderRecord(SideName.Wsl, CredentialFiles.Pair(refreshToken).Fingerprint, Clock.GetUtcNow()),
            cancellationToken);
    }

    /// <summary>What the follower's F4 leaves in the mailbox for the outgoing account.</summary>
    public async Task WriteExportAsync(string email, string refreshToken, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Mailbox);
        await File.WriteAllTextAsync(ExportPath(email), CredentialFiles.Shape(refreshToken).ToJsonString(), cancellationToken);
    }

    public IReadOnlyList<string> MailboxFiles() =>
        Directory.Exists(Mailbox) ? Directory.GetFiles(Mailbox) : [];

    public void Dispose()
    {
        Gate.Dispose();
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}

/// <summary>
/// The other side, scripted. Every call is recorded in order, so a test can
/// say not only what the coordinator asked but what it did <i>not</i> ask —
/// which is how "no commit was sent" and "no import was sent" are asserted.
/// <para>
/// The defaults behave as the follower does: the import copies the outgoing
/// pair into the export and stops, the commit consumes the claimed file the
/// way a swap does, and the abort deletes the export it wrote. A test that
/// wants another answer sets the matching hook.
/// </para>
/// </summary>
internal sealed class FakePeerRotationInstance(string mailbox) : IPeerRotationInstance
{
    public SideName Side => SideName.Wsl;

    public List<string> Calls { get; } = [];

    /// <summary>What this side holds, which the leader's L1 reads and its plan is built on.</summary>
    public string? OutgoingEmail { get; set; } = "a@example.com";

    public string? OutgoingRefreshToken { get; set; } = "refresh-a";

    /// <summary>The version the dashboard reports; the leader refuses anything but its own.</summary>
    public string Version { get; set; } = AppComposition.Version;

    public string? DashboardError { get; set; }

    public Func<ImportRequest, Task<Result<ImportAnswer, string>>>? OnImport { get; set; }

    public Func<Task<Result<ImportResult, string>>>? OnCommit { get; set; }

    public Func<Task<Result<Unit, string>>>? OnAbort { get; set; }

    public ImportStatus Status { get; set; } = new(false, null, null, null, "not imported: no record of it here");

    public RefreshTokenFingerprint? OutgoingFingerprint =>
        OutgoingRefreshToken is string token ? CredentialFiles.Pair(token).Fingerprint : null;

    public Task<Result<PeerDashboard, string>> ReadDashboardAsync(CancellationToken cancellationToken)
    {
        Calls.Add("Dashboard");
        return Task.FromResult(DashboardError is string error
            ? Result<PeerDashboard, string>.Failure(error)
            : Result<PeerDashboard, string>.Success(new PeerDashboard(
                SideName.Wsl,
                OutgoingEmail is null ? null : WslSwitchHarness.Email(OutgoingEmail),
                OutgoingFingerprint,
                null,
                Version,
                OutgoingEmail is null ? null : WslSwitchHarness.AccountJson(OutgoingEmail))));
    }

    public async Task<Result<ImportAnswer, string>> ImportAsync(ImportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Calls.Add("Import");
        if (OnImport is not null)
        {
            return await OnImport(request);
        }

        if (OutgoingRefreshToken is not string token)
        {
            // Exported {none}: this side held nothing, so F4 wrote no export.
            return Result<ImportAnswer, string>.Success(new ImportAnswer(null, null, false, null));
        }

        await File.WriteAllTextAsync(request.ExportPath, CredentialFiles.Shape(token).ToJsonString(), cancellationToken);
        return Result<ImportAnswer, string>.Success(new ImportAnswer(
            CredentialFiles.Pair(token).Fingerprint,
            OutgoingEmail is null ? null : WslSwitchHarness.Email(OutgoingEmail),
            false,
            null));
    }

    public async Task<Result<ImportResult, string>> CommitImportAsync(AccountEmail email, CancellationToken cancellationToken)
    {
        Calls.Add("Commit");
        if (OnCommit is not null)
        {
            return await OnCommit();
        }

        // The swap: the claimed pair becomes this side's live pair, so its only
        // copy leaves the mailbox exactly as the follower's F6 rename does.
        StagedImportCredentialPairStore.DeleteIfPresent(Path.Combine(mailbox, email.Value + FileSystemCredentialPairStore.FileName));
        return Result<ImportResult, string>.Success(Result());
    }

    public async Task<Result<Unit, string>> AbortImportAsync(AccountEmail email, CancellationToken cancellationToken)
    {
        Calls.Add("Abort");
        if (OnAbort is not null)
        {
            return await OnAbort();
        }

        if (OutgoingEmail is string outgoing)
        {
            StagedImportCredentialPairStore.DeleteIfPresent(
                Path.Combine(mailbox, outgoing + FileSystemCredentialPairStore.FileName + FileSystemCredentialPairStore.IncomingSuffix));
        }

        return Result<Unit, string>.Success(Unit.Value);
    }

    public Task<Result<ImportStatus, string>> ImportStatusAsync(AccountEmail email, CancellationToken cancellationToken)
    {
        Calls.Add("Status");
        return Task.FromResult(Result<ImportStatus, string>.Success(Status));
    }

    /// <summary>The commit's answer: which account left this side, and the block the leader's park needs.</summary>
    public ImportResult Result() => new(
        OutgoingEmail is null ? null : WslSwitchHarness.Email(OutgoingEmail),
        OutgoingFingerprint,
        OutgoingEmail is null ? null : WslSwitchHarness.AccountJson(OutgoingEmail),
        false);
}

/// <summary>A login runner that owns nothing, so no plan is ever refused for a login in flight.</summary>
internal sealed class NoLoginsRunning : ILoginSessionRunner
{
    public Task<Result<LoginSession, string>> StartAsync(AccountEmail email, string folderPath, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<Result<LoginSession, string>> SubmitCodeAsync(LoginSessionId id, string code, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public LoginSession? Status(LoginSessionId id) => null;

    public bool IsRunningAgainst(string folderPath) => false;
}
