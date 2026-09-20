using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Configuration;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Configuration;
using Shouldly;

namespace ClaudeCodeAccountRotation.App.Tests.Configuration;

/// <summary>
/// The follower's own configuration keys and the validator branch they select.
/// The leader's rules are unchanged, which the last fact here asserts rather
/// than assumes.
/// </summary>
public sealed class FollowerConfigurationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "claude-code-account-rotation-tests", Guid.NewGuid().ToString("N"));

    public static bool OnUnix => !OperatingSystem.IsWindows();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public FollowerConfigurationTests()
    {
        Directory.CreateDirectory(Live);
        Directory.CreateDirectory(AppData);
        Directory.CreateDirectory(Mailbox);
    }

    private string Live => Path.Combine(_root, "live");

    private string AppData => Path.Combine(_root, "appdata");

    private string Mailbox => Path.Combine(_root, "mailbox");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private ClaudeCodeAccountRotationConfiguration Follower(string? liveDirectory = null, string? mailbox = null) =>
        ConfigurationDefaults.ForUser(_root, _root, Live) with
        {
            LiveConfigDirectory = liveDirectory ?? Live,
            AppDataDirectory = AppData,
            ProfilesRoot = Path.Combine(_root, "profiles"),
            Role = RotationRole.Follower,
            Mailbox = mailbox ?? Mailbox,
        };

    private static Result<Unit, string> Validate(ClaudeCodeAccountRotationConfiguration configuration) =>
        ConfigurationValidator.Validate(
            configuration,
            Path.GetTempPath(),
            ConfigurationValidator.VolumeOf,
            static _ => null);

    [Fact]
    public void AFollowerWithAnExistingWritableMailboxIsAccepted() =>
        Validate(Follower()).IsSuccess.ShouldBeTrue();

    [Fact]
    public void AFollowerWhoseLiveDirectoryIsUnderTheWindowsMountIsRefusedWithANamedReason()
    {
        Result<Unit, string> verdict = Validate(Follower(liveDirectory: "/mnt/c/claude-live"));

        verdict.IsFailure.ShouldBeTrue();
        verdict.Error.ShouldContain("/mnt/");
        verdict.Error.ShouldContain("live config directory");
    }

    /// <summary>
    /// Spelled so the raw prefix check misses it and normalization lands it on
    /// the mount. Unix only: on Windows the same spelling normalizes to a
    /// drive-rooted path, which is not a WSL view of anything.
    /// </summary>
    [Fact(SkipUnless = nameof(OnUnix), Skip = "Resolving .. onto /mnt/ is a Unix path behavior")]
    public void AFollowerLiveDirectoryThatOnlyNormalizesOntoTheWindowsMountIsRefused()
    {
        Result<Unit, string> verdict = Validate(Follower(liveDirectory: "/tmp/../mnt/c/claude-live"));

        verdict.IsFailure.ShouldBeTrue();
        verdict.Error.ShouldContain("/mnt/");
    }

    /// <summary>
    /// Spelled under the temp root, so both the raw and the normalized checks
    /// miss it, while a link along the way puts the live pair on the mount.
    /// </summary>
    [Fact(SkipUnless = nameof(OnUnix), Skip = "Symbolic links onto /mnt/ are a WSL layout")]
    public void AFollowerLiveDirectoryReachedThroughALinkOntoTheWindowsMountIsRefused()
    {
        string link = Path.Combine(_root, "linked");
        Directory.CreateSymbolicLink(link, "/mnt");

        Result<Unit, string> verdict = Validate(Follower(liveDirectory: Path.Combine(link, "c", "claude-live")));

        verdict.IsFailure.ShouldBeTrue();
        verdict.Error.ShouldContain("/mnt/");
    }

    /// <summary>The outer link's target is itself reached through a link onto the mount.</summary>
    [Fact(SkipUnless = nameof(OnUnix), Skip = "Symbolic links onto /mnt/ are a WSL layout")]
    public void AFollowerLiveDirectoryReachedThroughALinkInsideALinkTargetIsRefused()
    {
        string inner = Path.Combine(_root, "inner");
        string outer = Path.Combine(_root, "outer");
        Directory.CreateSymbolicLink(inner, "/mnt");
        Directory.CreateSymbolicLink(outer, Path.Combine(inner, "c"));

        Result<Unit, string> verdict = Validate(Follower(liveDirectory: Path.Combine(outer, "claude-live")));

        verdict.IsFailure.ShouldBeTrue();
        verdict.Error.ShouldContain("/mnt/");
    }

    [Fact]
    public void AFollowerWhoseMailboxDoesNotExistIsRefusedWithANamedReason()
    {
        Result<Unit, string> verdict = Validate(Follower(mailbox: Path.Combine(_root, "absent")));

        verdict.IsFailure.ShouldBeTrue();
        verdict.Error.ShouldContain("mailbox");
        verdict.Error.ShouldContain("does not exist");
    }

    [Fact]
    public void AFollowerWithNoMailboxAtAllIsRefused()
    {
        Result<Unit, string> verdict = Validate(Follower() with { Mailbox = null });

        verdict.IsFailure.ShouldBeTrue();
        verdict.Error.ShouldContain("mailbox");
    }

    /// <summary>
    /// Two mount points every Linux has and no Windows machine does. Phase 3
    /// left this rule untested for want of a portable second volume; there is
    /// one on the operating system the follower actually runs on, because
    /// <c>/dev/shm</c> and <c>/tmp</c> are separate tmpfs mounts and
    /// <see cref="ConfigurationValidator.VolumeOf"/> resolves a Unix path to
    /// the longest mount point that owns it.
    /// </summary>
    /// <remarks>
    /// Gated on the product's own resolver rather than on the two directories
    /// existing: a leg where it cannot tell the two mounts apart would fail
    /// this fact for a reason that is not the rule under test.
    /// </remarks>
    public static bool HasTwoVolumes =>
        !OperatingSystem.IsWindows()
        && Directory.Exists("/dev/shm")
        && Directory.Exists("/tmp")
        && ConfigurationValidator.VolumeOf("/dev/shm") != ConfigurationValidator.VolumeOf("/tmp");

    [Fact(SkipUnless = nameof(HasTwoVolumes), Skip = "Needs two mount points, which only the Unix legs have")]
    public void AFollowerWhoseAppDataIsOnAnotherVolumeThanItsLiveDirectoryIsRefused()
    {
        // The journal is what decides, after a crash, whether the swap happened,
        // so a journal that can be present when the live file it describes is not
        // (or the reverse) is the one arrangement the crash table cannot survive.
        string live = Path.Combine("/tmp", "ccar-volume-" + Guid.NewGuid().ToString("N"));
        string appData = Path.Combine("/dev/shm", "ccar-volume-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(live);
        Directory.CreateDirectory(appData);
        try
        {
            Result<Unit, string> verdict = Validate(Follower(liveDirectory: live) with { AppDataDirectory = appData });

            verdict.IsFailure.ShouldBeTrue();
            verdict.Error.ShouldContain("app data directory");
            verdict.Error.ShouldContain("same volume");
        }
        finally
        {
            Directory.Delete(live, recursive: true);
            Directory.Delete(appData, recursive: true);
        }
    }

    [Fact]
    public void AFollowerIsNotHeldToTheLeadersProfilesRootRules()
    {
        // A profiles root inside the live directory is the leader's first refusal
        // and means nothing to a follower, which never parks a pair.
        Result<Unit, string> verdict = Validate(Follower() with { ProfilesRoot = Path.Combine(Live, "profiles") });

        verdict.IsSuccess.ShouldBeTrue(verdict.IsFailure ? verdict.Error : null);
    }

    [Fact]
    public void TheLeadersRulesAreUnchanged()
    {
        ClaudeCodeAccountRotationConfiguration leader = Follower() with
        {
            Role = RotationRole.Leader,
            ProfilesRoot = Path.Combine(Live, "profiles"),
        };

        Result<Unit, string> verdict = Validate(leader);

        verdict.IsFailure.ShouldBeTrue();
        verdict.Error.ShouldContain("profiles root");
    }

    [Fact]
    public async Task TheRoleAndMailboxKeysRoundTripThroughTheConfigurationFile()
    {
        string path = Path.Combine(_root, "config.json");
        JsonObject written = new()
        {
            ["role"] = "follower",
            ["liveConfigDirectory"] = Live,
            ["appDataDirectory"] = AppData,
            ["mailbox"] = Mailbox,
        };
        await File.WriteAllTextAsync(path, written.ToJsonString(), Token);

        Result<ClaudeCodeAccountRotationConfiguration, string> loaded = await ConfigurationFile.LoadOrCreateAsync(
            path,
            ConfigurationDefaults.ForUser(_root, _root, Live),
            Token);

        loaded.IsSuccess.ShouldBeTrue();
        loaded.Value.Role.ShouldBe(RotationRole.Follower);
        loaded.Value.Mailbox.ShouldBe(Mailbox);
    }

    [Fact]
    public async Task AnAbsentOrUnrecognizedRoleLeavesTheProcessTheLeader()
    {
        string path = Path.Combine(_root, "config.json");
        await File.WriteAllTextAsync(path, new JsonObject { ["role"] = "peer" }.ToJsonString(), Token);

        Result<ClaudeCodeAccountRotationConfiguration, string> loaded = await ConfigurationFile.LoadOrCreateAsync(
            path,
            ConfigurationDefaults.ForUser(_root, _root, Live),
            Token);

        loaded.Value.Role.ShouldBe(RotationRole.Leader);
    }
}
