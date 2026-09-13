using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.App.Quota;
using ClaudeCodeAccountRotation.App.Switching;
using ClaudeCodeAccountRotation.Core.Accounts;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Quota;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeCodeAccountRotation.App.Tests.Quota;

/// <summary>
/// One refresh engine over a temp layout: the real credential store, the real
/// budget, the real recovery files, the real switch executor, and scripted
/// doubles for the two things that would otherwise leave this machine. Nothing
/// here touches the operator's live directories and no test reaches the network.
/// <para>
/// The clock is frozen and moved by hand, and the pacer records rather than
/// waits, so a pass that spends a minute of its own time costs the test
/// microseconds. Every expiry a test sets up is derived from
/// <see cref="Clock"/>: the credential fixture's defaults come from the wall
/// clock, which means nothing to an engine reading a frozen one.
/// </para>
/// </summary>
internal sealed class RefreshHarness : IDisposable
{
    public RefreshHarness()
    {
        Root = Path.Combine(Path.GetTempPath(), "claude-code-account-rotation-tests", Guid.NewGuid().ToString("N"));
        LiveDirectory = Path.Combine(Root, "live");
        StateFilePath = Path.Combine(Root, ".claude.json");
        ProfilesRoot = Path.Combine(Root, "profiles");
        AppData = Path.Combine(Root, "appdata");
        Directory.CreateDirectory(LiveDirectory);
        Directory.CreateDirectory(ProfilesRoot);
        Directory.CreateDirectory(AppData);

        // The gate timeout production runs with: a second mutation is refused at
        // once rather than queued. The refresh unit's own two-second wait is the
        // engine's, not this.
        SwitchOptions options = new(LiveDirectory, StateFilePath, ProfilesRoot, AppData, TimeSpan.FromSeconds(2), MutationGateTimeout: TimeSpan.Zero);
        // The store keeps the system clock even here: its refresh lock polls
        // against GetUtcNow and would never terminate under a frozen one.
        Store = new FaultyPairStore(new FileSystemCredentialPairStore(LiveDirectory, ProfilesRoot, TimeProvider.System));
        Profiles = new ProfileFolderStore(ProfilesRoot);
        _roster = new RosterFile(AppData);
        ClaudeStateFile stateFile = new(StateFilePath);
        Recovery = new RecoveryFiles(options, Store, Profiles, State, RecoveryLog, Clock);
        Executor = new LiveDirectorySwitch(
            Store,
            stateFile,
            Profiles,
            new SwitchJournal(AppData),
            Gate,
            Logins,
            new CannedIdentity(),
            new ManagedLoginPolicyReader(Path.Combine(Root, "managed-settings.json"), static () => null, static () => null),
            Recovery,
            State,
            options,
            Clock,
            NullLogger<LiveDirectorySwitch>.Instance);
        Budget = new RefreshBudget(Clock);
        Cache = new UsageSnapshotCache(options);
        Engine = new QuotaRefresh(
            Store,
            Profiles,
            _roster,
            stateFile,
            Executor,
            () => Usage,
            () => Tokens,
            Budget,
            State,
            Cache,
            Gate,
            Logins,
            Recovery,
            Clock,
            Waits.Record,
            RefreshLog,
            // The gate wait production spends two seconds on. A test that holds
            // the gate against a pass asserts the refusal, not the patience, and
            // the two seconds would be two seconds of a test suite's life.
            GateWait);
    }

    /// <summary>How long this harness's engine waits for the mutation gate before it gives the turn up.</summary>
    public static TimeSpan GateWait => TimeSpan.FromMilliseconds(50);

    public string Root { get; }

    public string LiveDirectory { get; }

    public string StateFilePath { get; }

    public string ProfilesRoot { get; }

    public string AppData { get; }

    public TestClock Clock { get; } = new(new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero));

    public ScriptedUsage Usage { get; } = new();

    public ScriptedTokens Tokens { get; } = new();

    public ScriptedLogins Logins { get; } = new();

    public AppFactory.Pacer Waits { get; } = new();

    public CredentialMutationGate Gate { get; } = new();

    public QuotaState State { get; } = new();

    public FaultyPairStore Store { get; }

    public ProfileFolderStore Profiles { get; }

    public RecoveryFiles Recovery { get; }

    public LiveDirectorySwitch Executor { get; }

    public RefreshBudget Budget { get; }

    /// <summary>The real cache file under this harness's app data, so a pass's saves are the ones a test reads back.</summary>
    public UsageSnapshotCache Cache { get; }

    public QuotaRefresh Engine { get; }

    public RecordingLogger<QuotaRefresh> RefreshLog { get; } = new();

    public RecordingLogger<RecoveryFiles> RecoveryLog { get; } = new();

    /// <summary>An access token the endpoint would reject: the ordinary state of a parked pair.</summary>
    public DateTimeOffset Expired => Clock.GetUtcNow() - TimeSpan.FromHours(1);

    /// <summary>An access token still good, so the pass reads without refreshing first.</summary>
    public DateTimeOffset Valid => Clock.GetUtcNow() + TimeSpan.FromHours(1);

    public static AccountEmail Email(string value) => AccountEmail.Parse(value).Value;

    public string FolderFor(string email) => Path.Combine(ProfilesRoot, email);

    /// <summary>
    /// Parks one account with a credential pair and a profile, both expiries
    /// stated outright. <paramref name="withoutLoginExpiry"/> writes the pair the
    /// way a CLI that never recorded a login lifetime wrote it: the key absent
    /// rather than zero, which is the one shape a renewal must never act on.
    /// </summary>
    public async Task<string> ParkAsync(
        string email,
        string refreshToken,
        DateTimeOffset accessTokenExpiresAt,
        CancellationToken cancellationToken,
        DateTimeOffset? loginExpiresAt = null,
        bool withoutLoginExpiry = false)
    {
        string folder = FolderFor(email);
        Directory.CreateDirectory(folder);
        JsonObject shape = CredentialFiles.Shape(refreshToken, accessTokenExpiresAt, loginExpiresAt ?? Clock.GetUtcNow().AddDays(28));
        if (withoutLoginExpiry)
        {
            shape["claudeAiOauth"]!.AsObject().Remove("refreshTokenExpiresAt");
        }

        await File.WriteAllTextAsync(
            Path.Combine(folder, CredentialFiles.FileName),
            shape.ToJsonString(),
            cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(folder, "profile.json"),
            AppFactory.AccountJson(email).ToJsonString(),
            cancellationToken);
        return folder;
    }

    public Task PauseAsync(string email, CancellationToken cancellationToken) =>
        _roster.UpdateAsync(roster => roster.With(new RosterEntry(Email(email), null, null, null, Paused: true, null)), cancellationToken);

    public Task AddToRosterAsync(string email, CancellationToken cancellationToken) =>
        _roster.UpdateAsync(roster => roster.With(new RosterEntry(Email(email), null, null, null, Paused: false, null)), cancellationToken);

    /// <summary>What the state file says is live, which is the only thing that decides it.</summary>
    public Task WriteLiveIdentityAsync(string email, CancellationToken cancellationToken) =>
        File.WriteAllTextAsync(
            StateFilePath,
            new JsonObject { ["numStartups"] = 3, ["oauthAccount"] = AppFactory.AccountJson(email) }.ToJsonString(),
            cancellationToken);

    public Task WriteLivePairAsync(string refreshToken, DateTimeOffset accessTokenExpiresAt, CancellationToken cancellationToken) =>
        File.WriteAllTextAsync(
            Path.Combine(LiveDirectory, CredentialFiles.FileName),
            CredentialFiles.Shape(refreshToken, accessTokenExpiresAt, Clock.GetUtcNow().AddDays(28)).ToJsonString(),
            cancellationToken);

    /// <summary>The parked credential file as it now stands on disk, for the assertions about what a write-back changed.</summary>
    public static async Task<JsonObject> ParkedOAuthAsync(string folder, CancellationToken cancellationToken) =>
        JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(folder, CredentialFiles.FileName), cancellationToken))!
            .AsObject()["claudeAiOauth"]!
            .AsObject();

    public RefreshOutcome? OutcomeFor(string email) => State.OutcomeFor(Email(email));

    public void Dispose()
    {
        _roster.Dispose();
        Gate.Dispose();
        Cache.Dispose();
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }

        // Last, after the temp directory is gone: a test whose script had a gap
        // fails here rather than reading the engine's own catch-all as the
        // engine's answer.
        Usage.Unscripted.ShouldBeFalse("the pass made a usage read this test did not script");
        Tokens.Unscripted.ShouldBeFalse("the pass made a token refresh this test did not script");
    }

    private readonly RosterFile _roster;
}
