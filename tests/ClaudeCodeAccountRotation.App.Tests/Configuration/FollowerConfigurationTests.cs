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
    public async Task AnAbsentOrMisspelledRoleLeavesTheProcessTheLeader()
    {
        string path = Path.Combine(_root, "config.json");
        await File.WriteAllTextAsync(path, new JsonObject { ["role"] = "folower" }.ToJsonString(), Token);

        Result<ClaudeCodeAccountRotationConfiguration, string> loaded = await ConfigurationFile.LoadOrCreateAsync(
            path,
            ConfigurationDefaults.ForUser(_root, _root, Live),
            Token);

        loaded.Value.Role.ShouldBe(RotationRole.Leader);
    }
}
