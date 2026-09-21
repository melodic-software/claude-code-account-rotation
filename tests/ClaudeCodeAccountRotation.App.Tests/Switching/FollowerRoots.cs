using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.App.Switching;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Peers;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeCodeAccountRotation.App.Tests.Switching;

/// <summary>
/// The two temp volumes a follower test runs against: a "WSL" side (live
/// directory and app data) and a "Windows" side (the store and its
/// <c>.transit/wsl</c> mailbox). They are two directories on one real volume,
/// which is enough for every fact in this phase — the cross-volume
/// <c>EXDEV</c> behavior itself is phase 4's in-distro acceptance — and it
/// means no test here ever reaches a real store, a real live directory, or a
/// real login.
/// </summary>
internal sealed class FollowerRoots : IDisposable
{
    public FollowerRoots()
    {
        Root = Path.Combine(Path.GetTempPath(), "claude-code-account-rotation-tests", Guid.NewGuid().ToString("N"));
        LiveDirectory = Path.Combine(Root, "wsl", "live");
        AppData = Path.Combine(Root, "wsl", "appdata");
        StateFilePath = Path.Combine(Root, "wsl", ".claude.json");
        Store = Path.Combine(Root, "windows", "store");
        Mailbox = Path.Combine(Store, ".transit", "wsl");
        Directory.CreateDirectory(LiveDirectory);
        Directory.CreateDirectory(AppData);
        Directory.CreateDirectory(Mailbox);
    }

    public string Root { get; }

    public string LiveDirectory { get; }

    public string AppData { get; }

    public string StateFilePath { get; }

    public string Store { get; }

    public string Mailbox { get; }

    public TestClock Clock { get; } = new(new DateTimeOffset(2026, 9, 20, 9, 0, 0, TimeSpan.Zero));

    /// <summary>The follower's own mutation gate, owned here so the roots dispose it with everything else.</summary>
    public CredentialMutationGate Gate { get; } = new();

    public string LivePath => Path.Combine(LiveDirectory, CredentialFiles.FileName);

    public string StagingPath => Path.Combine(LiveDirectory, StagedImportCredentialPairStore.StagingFileName);

    public string ClaimedPath(string email) => Path.Combine(Mailbox, email + ".credentials.json");

    public string ExportPath(string email) => Path.Combine(Mailbox, email + ".credentials.json.incoming");

    public string RefreshLockDirectory => Path.Combine(LiveDirectory, OAuthRefreshLock.DirectoryName);

    public SwitchOptions Options(string? failAfterStep = null) => new(
        LiveDirectory,
        StateFilePath,
        ProfilesRoot: Store,
        AppData,
        RefreshLockWaitBound: TimeSpan.FromSeconds(1),
        MutationGateTimeout: TimeSpan.FromSeconds(1),
        Mailbox,
        failAfterStep);

    public ImportJournal Journal() => new(AppData);

    public StagedImportCredentialPairStore Pairs() => new(LiveDirectory, Clock);

    public ClaudeStateFile StateFile() => new(StateFilePath);

    public ImportReconciler Reconciler(SwitchOptions? options = null) => new(
        options ?? Options(),
        Pairs(),
        Journal(),
        StateFile(),
        Clock,
        NullLogger<ImportReconciler>.Instance);

    /// <summary>
    /// A follower built the way the container builds one, with the two timing
    /// bounds shortened so the lock budget and the idle self-abort are asserted
    /// by moving the clock rather than by waiting.
    /// </summary>
    public FollowerImport Follower(
        SwitchOptions? options = null,
        TimeSpan? commitBudget = null,
        TimeSpan? idleTimeout = null,
        TimeSpan? heartbeatInterval = null,
        CredentialMutationGate? gate = null,
        ClaudeStateFile? stateFile = null)
    {
        SwitchOptions resolved = options ?? Options();
        return new FollowerImport(
            resolved,
            Pairs(),
            Journal(),
            Reconciler(resolved),
            stateFile ?? StateFile(),
            gate ?? Gate,
            Clock,
            NullLogger<FollowerImport>.Instance)
        {
            CommitBudget = commitBudget ?? TimeSpan.FromSeconds(20),
            IdleTimeout = idleTimeout ?? TimeSpan.FromSeconds(120),
            HeartbeatInterval = heartbeatInterval ?? TimeSpan.FromMilliseconds(50),
        };
    }

    public static JsonObject AccountJson(string email) => new() { ["accountUuid"] = "uuid-" + email, ["emailAddress"] = email };

    public async Task WriteStateFileAsync(string email, CancellationToken cancellationToken)
    {
        JsonObject state = new() { ["numStartups"] = 4, ["oauthAccount"] = AccountJson(email) };
        await File.WriteAllTextAsync(StateFilePath, state.ToJsonString(), cancellationToken);
    }

    /// <summary>Puts a pair in the WSL live directory: the outgoing account A.</summary>
    public async Task<RefreshTokenFingerprint> WriteLiveAsync(string email, string refreshToken, CancellationToken cancellationToken)
    {
        await CredentialFiles.WriteAsync(LiveDirectory, refreshToken, cancellationToken);
        await WriteStateFileAsync(email, cancellationToken);
        return RefreshTokenFingerprint.FromRefreshToken(refreshToken);
    }

    /// <summary>Puts a pair in the mailbox the way the leader's L2 claim does: the incoming account B.</summary>
    public async Task<RefreshTokenFingerprint> WriteClaimedAsync(string email, string refreshToken, CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(ClaimedPath(email), CredentialFiles.Shape(refreshToken).ToJsonString(), cancellationToken);
        return RefreshTokenFingerprint.FromRefreshToken(refreshToken);
    }

    /// <summary>
    /// The request as the leader's L3a sends it: the claimed file is named for
    /// the incoming account and the export path for the outgoing one, which the
    /// leader learned from the follower's dashboard at L1.
    /// </summary>
    public ImportRequest Request(string email, RefreshTokenFingerprint fingerprint, string outgoingEmail = "a@example.com") => new(
        new AccountEmail(email),
        ClaimedPath(email),
        fingerprint,
        AccountJson(email),
        ExportPath(outgoingEmail));

    public static async Task<RefreshTokenFingerprint?> FingerprintOfAsync(string path, CancellationToken cancellationToken)
    {
        CredentialPair? pair = await StagedImportCredentialPairStore.ReadFreshAsync(path, cancellationToken);
        return pair?.Fingerprint;
    }

    /// <summary>
    /// Every file under both roots that parses as a credential pair with this
    /// fingerprint, staging files excluded. The staging name is the one place
    /// the design allows a second copy to sit while a hand-off is in flight;
    /// everywhere else, a fingerprint appearing twice is the defect the whole
    /// protocol exists to prevent.
    /// </summary>
    public async Task<IReadOnlyList<string>> NonStagingFilesHoldingAsync(RefreshTokenFingerprint fingerprint, CancellationToken cancellationToken)
    {
        List<string> found = [];
        foreach (string path in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(path) == StagedImportCredentialPairStore.StagingFileName)
            {
                continue;
            }

            CredentialPair? pair;
            try
            {
                pair = await StagedImportCredentialPairStore.ReadFreshAsync(path, cancellationToken);
            }
            catch (IOException)
            {
                // A file no other process will let this one open is not a reachable
                // copy of anything: a running follower holds instance.lock exclusively.
                continue;
            }

            if (pair?.Fingerprint == fingerprint)
            {
                found.Add(path);
            }
        }

        return found;
    }

    /// <summary>
    /// Best effort, and deliberately so: a follower this test killed holds its
    /// <c>instance.lock</c> open with delete-on-close, and on Windows the name
    /// survives in a delete-pending state for a moment after the process goes.
    /// A temp directory that outlives the run by a few seconds is not a defect
    /// worth failing an assertion over.
    /// </summary>
    public void Dispose()
    {
        Gate.Dispose();
        for (int attempt = 0; attempt < 10 && Directory.Exists(Root); attempt++)
        {
            try
            {
                Directory.Delete(Root, recursive: true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }
}
