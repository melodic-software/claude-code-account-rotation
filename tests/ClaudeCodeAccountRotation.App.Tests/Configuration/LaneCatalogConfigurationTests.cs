using ClaudeCodeAccountRotation.App.Configuration;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Configuration;

namespace ClaudeCodeAccountRotation.App.Tests.Configuration;

/// <summary>
/// <c>laneCatalog</c>: optional, absent in every file an earlier release wrote,
/// and each part held to GitHub's rules before it can reach an API path.
/// </summary>
public sealed class LaneCatalogConfigurationTests : IDisposable
{
    /// <summary>The 2.5.0 template, as <c>git show v2.5.0:src/ClaudeCodeAccountRotation.App/config.template.json</c> prints it.</summary>
    private const string Release250Template = """
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
          "remoteControlTask": "claude-remote-control",
          "laneRepositories": null
        }
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "claude-code-account-rotation-tests", Guid.NewGuid().ToString("N"));
    private readonly string _home;

    public LaneCatalogConfigurationTests()
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

    private Result<Unit, string> Validate(LaneCatalogSource source) =>
        ConfigurationValidator.Validate(Defaults() with { LaneCatalog = source }, _home, static _ => "/", static _ => null);

    [Fact]
    public async Task A250FileWithoutTheKeyLoadsUnchangedWithNoCatalog()
    {
        (Result<ClaudeCodeAccountRotationConfiguration, string> loaded, string path) = await LoadAsync(Release250Template);

        loaded.IsSuccess.ShouldBeTrue(loaded.IsFailure ? loaded.Error : "");
        loaded.Value.LaneCatalog.ShouldBeNull();
        (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).ShouldBe(Release250Template);
    }

    [Fact]
    public async Task TheCatalogNamesItsRepositoryPathAndRef()
    {
        (Result<ClaudeCodeAccountRotationConfiguration, string> loaded, _) = await LoadAsync(
            """{"laneCatalog": {"repository": " melodic-software/claude-code-plugins ", "path": ".github/lane-funding.json", "ref": "feat/lane-funding-catalog"}}""");

        loaded.Value.LaneCatalog.ShouldBe(new LaneCatalogSource("melodic-software/claude-code-plugins", ".github/lane-funding.json", "feat/lane-funding-catalog"));
    }

    [Fact]
    public async Task ACatalogWithNoRefReadsTheDefaultBranch()
    {
        (Result<ClaudeCodeAccountRotationConfiguration, string> loaded, _) = await LoadAsync("""{"laneCatalog": {"repository": "octo/lanes", "path": "lane-funding.json"}}""");

        loaded.Value.LaneCatalog.ShouldBe(new LaneCatalogSource("octo/lanes", "lane-funding.json", null));
    }

    [Theory]
    [InlineData("\"octo/lanes\"")]
    [InlineData("[]")]
    [InlineData("{\"repository\": \"octo/lanes\"}")]
    [InlineData("{\"path\": \"lane-funding.json\"}")]
    [InlineData("{\"repository\": 7, \"path\": \"lane-funding.json\"}")]
    [InlineData("{\"repository\": \"octo/lanes\", \"path\": \"lane-funding.json\", \"ref\": 7}")]
    public async Task AValueThatIsNotACatalogSourceIsRefused(string value)
    {
        (Result<ClaudeCodeAccountRotationConfiguration, string> loaded, _) = await LoadAsync("{\"laneCatalog\": " + value + "}");

        loaded.Error.ShouldContain("laneCatalog that is not an object with repository and path strings");
    }

    [Theory]
    [InlineData("octo", "lane-funding.json", null)]
    [InlineData("octo/..", "lane-funding.json", null)]
    [InlineData("octo/lanes", "../lane-funding.json", null)]
    [InlineData("octo/lanes", "/lane-funding.json", null)]
    [InlineData("octo/lanes", "a/./lane-funding.json", null)]
    [InlineData("octo/lanes", "lane funding.json", null)]
    [InlineData("octo/lanes", "lane-funding.json?ref=x", null)]
    [InlineData("octo/lanes", "lane-funding.json", "main&x=1")]
    [InlineData("octo/lanes", "lane-funding.json", "a/../b")]
    [InlineData("octo/lanes", "lane-funding.json", "-main")]
    public void APartThatCouldLeaveTheContentsPathIsRefused(string repository, string path, string? reference) =>
        Validate(new LaneCatalogSource(repository, path, reference)).Error.ShouldContain("laneCatalog");

    [Theory]
    [InlineData("melodic-software/claude-code-plugins", ".github/lane-funding.json", null)]
    [InlineData("octo/lanes", "lane-funding.json", "feat/lane-funding-catalog")]
    [InlineData("octo/lanes", "a/b_c-d.json", "v2.5.0")]
    public void ACatalogSourceIsAccepted(string repository, string path, string? reference)
    {
        Result<Unit, string> verdict = Validate(new LaneCatalogSource(repository, path, reference));

        (verdict.IsFailure ? verdict.Error : string.Empty).ShouldNotContain("laneCatalog");
    }
}
