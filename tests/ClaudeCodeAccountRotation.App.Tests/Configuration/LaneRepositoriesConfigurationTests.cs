using ClaudeCodeAccountRotation.App.Configuration;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Configuration;

namespace ClaudeCodeAccountRotation.App.Tests.Configuration;

/// <summary>
/// <c>laneRepositories</c>: optional, absent in every file an earlier release
/// wrote, and each entry held to GitHub's repository rule before it can reach
/// an API path.
/// </summary>
public sealed class LaneRepositoriesConfigurationTests : IDisposable
{
    /// <summary>The 2.2.0 template, as <c>git show v2.2.0:src/ClaudeCodeAccountRotation.App/config.template.json</c> prints it.</summary>
    private const string Release220Template = """
        {
          "liveConfigDirectory": null,
          "stateFilePath": null,
          "profilesRoot": null,
          "appDataDirectory": null,
          "listenPort": 48211,
          "refreshLockWaitSeconds": 10,
          "claudeExecutable": null,
          "userAgentProductToken": "claude-code-account-rotation",
          "browserExecutables": null,
          "store": {
            "shared": false
          },
          "role": "leader",
          "mailbox": null,
          "peers": null,
          "remoteControlTask": "claude-remote-control"
        }
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "claude-code-account-rotation-tests", Guid.NewGuid().ToString("N"));
    private readonly string _home;

    public LaneRepositoriesConfigurationTests()
    {
        _home = Path.Combine(_root, "home");
        Directory.CreateDirectory(_home);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private ClaudeCodeAccountRotationConfiguration Defaults() =>
        ConfigurationDefaults.ForUser(_home, Path.Combine(_home, "AppData", "Local"), null);

    private async Task<(Result<ClaudeCodeAccountRotationConfiguration, string> Loaded, string Path)> LoadAsync(string json)
    {
        string path = Path.Combine(_root, "appdata", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, json, TestContext.Current.CancellationToken);
        return (await ConfigurationFile.LoadOrCreateAsync(path, Defaults(), TestContext.Current.CancellationToken), path);
    }

    [Fact]
    public async Task A220FileWithoutTheKeyLoadsUnchangedWithNoLaneRepositories()
    {
        (Result<ClaudeCodeAccountRotationConfiguration, string> loaded, string path) = await LoadAsync(Release220Template);

        loaded.IsSuccess.ShouldBeTrue(loaded.IsFailure ? loaded.Error : "");
        (loaded.Value.LaneRepositories ?? []).ShouldBeEmpty();
        loaded.Value.RemoteControlTask.ShouldBe("claude-remote-control");
        loaded.Value.ListenPort.ShouldBe(48211);
        (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).ShouldBe(Release220Template);
    }

    [Fact]
    public async Task EntriesAreTrimmedAndBlanksSkipped()
    {
        (Result<ClaudeCodeAccountRotationConfiguration, string> loaded, _) = await LoadAsync("""{"laneRepositories": [" octo/lanes ", "", "octo/other"]}""");

        loaded.Value.LaneRepositories.ShouldBe(["octo/lanes", "octo/other"]);
    }

    [Theory]
    [InlineData("octo")]
    [InlineData("octo/..")]
    [InlineData("octo/.")]
    [InlineData("../octo")]
    [InlineData("octo/lanes/actions")]
    [InlineData("octo/lanes?x=1")]
    public void AnEntryThatIsNotARepositoryIsRefused(string repository)
    {
        ClaudeCodeAccountRotationConfiguration configuration = Defaults() with { LaneRepositories = [repository] };

        Result<Unit, string> verdict = ConfigurationValidator.Validate(configuration, _home, static _ => "/", static _ => null);

        verdict.Error.ShouldContain("laneRepositories entries are written owner/name");
    }

    [Theory]
    [InlineData("octo/lanes")]
    [InlineData("melodic-software/claude-code-account-rotation")]
    [InlineData("octo/a.b_c-d")]
    public void ARepositoryNameIsAccepted(string repository)
    {
        ClaudeCodeAccountRotationConfiguration configuration = Defaults() with { LaneRepositories = [repository] };

        Result<Unit, string> verdict = ConfigurationValidator.Validate(configuration, _home, static _ => "/", static _ => null);

        (verdict.IsFailure ? verdict.Error : string.Empty).ShouldNotContain("laneRepositories");
    }
}
