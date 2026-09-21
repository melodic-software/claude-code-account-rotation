using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Configuration;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Configuration;

namespace ClaudeCodeAccountRotation.App.Tests.Configuration;

public sealed class ConfigurationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "claude-code-account-rotation-tests", Guid.NewGuid().ToString("N"));
    private readonly string _home;

    public ConfigurationTests()
    {
        _home = Path.Combine(_root, "home");
        Directory.CreateDirectory(_home);
    }

    private ClaudeCodeAccountRotationConfiguration Defaults(string? claudeConfigDirectory = null) =>
        ConfigurationDefaults.ForUser(_home, Path.Combine(_home, "AppData", "Local"), claudeConfigDirectory);

    [Fact]
    public void DefaultsDeriveEveryPathFromTheUserProfile()
    {
        ClaudeCodeAccountRotationConfiguration defaults = Defaults();

        defaults.LiveConfigDirectory.ShouldBe(Path.Combine(_home, ".claude"));
        defaults.StateFilePath.ShouldBe(Path.Combine(_home, ".claude.json"));
        defaults.ProfilesRoot.ShouldBe(Path.Combine(_home, ".claude-profiles"));
        defaults.AppDataDirectory.ShouldBe(Path.Combine(_home, "AppData", "Local", "claude-code-account-rotation"));
        defaults.ListenPort.ShouldBe(48211);
        defaults.RefreshLockWaitBound.ShouldBe(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void AClaudeConfigDirectoryMovesTheLiveDirectoryAndTheStateFile()
    {
        string configured = Path.Combine(_root, "elsewhere");

        ClaudeCodeAccountRotationConfiguration defaults = Defaults(configured);

        defaults.LiveConfigDirectory.ShouldBe(configured);
        defaults.StateFilePath.ShouldBe(Path.Combine(configured, ".claude.json"));
    }

    [Fact]
    public async Task FirstRunWritesTheConfigurationWithResolvedDefaults()
    {
        string path = Path.Combine(_root, "appdata", "config.json");

        Result<ClaudeCodeAccountRotationConfiguration, string> loaded = await ConfigurationFile.LoadOrCreateAsync(path, Defaults(), TestContext.Current.CancellationToken);

        loaded.IsSuccess.ShouldBeTrue(loaded.IsFailure ? loaded.Error : "");
        loaded.Value.ShouldBe(Defaults());
        JsonObject written = JsonNode.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken))!.AsObject();
        written["profilesRoot"]!.GetValue<string>().ShouldBe(Defaults().ProfilesRoot);
        written["listenPort"]!.GetValue<int>().ShouldBe(48211);
        written["store"]!["shared"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public void TheStoreIsNotSharedUntilItIsSaidToBe()
    {
        Defaults().SharedStore.ShouldBeFalse();
    }

    /// <summary>
    /// A side names a directory under the store's <c>.transit/</c> and one in
    /// the peer's own namespace, so a hand-edited value that is not a single
    /// path segment would reach <c>Path.Combine</c>, where a traversal escapes
    /// the store entirely. Dropped at the parser, which is the one place every
    /// consumer of a side goes through.
    /// </summary>
    [Theory]
    [InlineData("../evil")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("c:evil")]
    [InlineData(" wsl")]
    [InlineData("  ")]
    public async Task APeerWhoseSideIsNotASinglePathSegmentIsDropped(string side)
    {
        ArgumentNullException.ThrowIfNull(side);
        string path = Path.Combine(_root, "appdata", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(
            path,
            """{"peers": [{"side": "@SIDE@", "baseAddress": "http://127.0.0.1:48212", "storePathFromPeer": "/mnt/c/store"}]}"""
                .Replace("@SIDE@", side.Replace("\\", "\\\\", StringComparison.Ordinal), StringComparison.Ordinal),
            TestContext.Current.CancellationToken);

        Result<ClaudeCodeAccountRotationConfiguration, string> loaded = await ConfigurationFile.LoadOrCreateAsync(path, Defaults(), TestContext.Current.CancellationToken);

        loaded.IsSuccess.ShouldBeTrue(loaded.IsFailure ? loaded.Error : "");
        loaded.Value.Peers.ShouldBeEmpty();
    }

    [Fact]
    public async Task APeerWhoseSideIsASinglePathSegmentIsKept()
    {
        string path = Path.Combine(_root, "appdata", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(
            path,
            """{"peers": [{"side": "wsl", "baseAddress": "http://127.0.0.1:48212", "storePathFromPeer": "/mnt/c/store"}]}""",
            TestContext.Current.CancellationToken);

        Result<ClaudeCodeAccountRotationConfiguration, string> loaded = await ConfigurationFile.LoadOrCreateAsync(path, Defaults(), TestContext.Current.CancellationToken);

        loaded.Value.Peers.ShouldHaveSingleItem().Side.Value.ShouldBe("wsl");
    }

    [Fact]
    public async Task StoreSharedTurnsTheSharedStoreOnAndNothingElseDoes()
    {
        string path = Path.Combine(_root, "appdata", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, """{"store": {"shared": true}}""", TestContext.Current.CancellationToken);

        Result<ClaudeCodeAccountRotationConfiguration, string> loaded = await ConfigurationFile.LoadOrCreateAsync(path, Defaults(), TestContext.Current.CancellationToken);

        loaded.Value.SharedStore.ShouldBeTrue();
    }

    [Fact]
    public async Task AFileWithNoStoreSectionKeepsTheStoreUnshared()
    {
        string path = Path.Combine(_root, "appdata", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, """{"listenPort": 5000}""", TestContext.Current.CancellationToken);

        Result<ClaudeCodeAccountRotationConfiguration, string> loaded = await ConfigurationFile.LoadOrCreateAsync(path, Defaults(), TestContext.Current.CancellationToken);

        loaded.Value.SharedStore.ShouldBeFalse();
    }

    [Fact]
    public async Task AStoreSectionThatIsNotABooleanKeepsTheDefault()
    {
        string path = Path.Combine(_root, "appdata", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, """{"store": {"shared": "yes please"}}""", TestContext.Current.CancellationToken);

        Result<ClaudeCodeAccountRotationConfiguration, string> loaded = await ConfigurationFile.LoadOrCreateAsync(path, Defaults(), TestContext.Current.CancellationToken);

        loaded.Value.SharedStore.ShouldBeFalse();
    }

    [Fact]
    public async Task AnExistingFileOverridesDefaultsKeyByKey()
    {
        string path = Path.Combine(_root, "appdata", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, """{"listenPort": 5000, "refreshLockWaitSeconds": 3, "claudeExecutable": "tools/claude.exe"}""", TestContext.Current.CancellationToken);

        ClaudeCodeAccountRotationConfiguration loaded = (await ConfigurationFile.LoadOrCreateAsync(path, Defaults(), TestContext.Current.CancellationToken)).Value;

        loaded.ListenPort.ShouldBe(5000);
        loaded.RefreshLockWaitBound.ShouldBe(TimeSpan.FromSeconds(3));
        loaded.ClaudeExecutable.ShouldBe("tools/claude.exe");
        loaded.ProfilesRoot.ShouldBe(Defaults().ProfilesRoot);
    }

    [Fact]
    public async Task AMalformedFileIsRefusedNamingThePath()
    {
        string path = Path.Combine(_root, "appdata", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "{ not json", TestContext.Current.CancellationToken);

        Result<ClaudeCodeAccountRotationConfiguration, string> loaded = await ConfigurationFile.LoadOrCreateAsync(path, Defaults(), TestContext.Current.CancellationToken);

        loaded.IsFailure.ShouldBeTrue();
        loaded.Error.ShouldContain(path);
    }

    [Fact]
    public void AProfilesRootOnAnotherVolumeIsRefusedNamingBothPaths()
    {
        ClaudeCodeAccountRotationConfiguration configuration = Defaults();
        string VolumeOf(string path) => path.StartsWith(configuration.ProfilesRoot, StringComparison.Ordinal) ? "E:" : "C:";

        Result<Unit, string> verdict = ConfigurationValidator.Validate(configuration, _home, VolumeOf, static _ => null);

        verdict.IsFailure.ShouldBeTrue();
        verdict.Error.ShouldContain(configuration.ProfilesRoot);
        verdict.Error.ShouldContain(configuration.LiveConfigDirectory);
    }

    [Fact]
    public void TheVolumeResolverPutsADirectoryAndItsChildOnOneVolume()
    {
        // The real resolver, on this machine: a temp directory and a path beneath it share a
        // volume on every platform (a Unix DriveInfo built from the path would say otherwise).
        string parent = Path.GetTempPath();
        string child = Path.Combine(parent, "claude-code-account-rotation-volume-probe", "live");

        string? volume = ConfigurationValidator.VolumeOf(parent);

        volume.ShouldNotBeNull();
        ConfigurationValidator.VolumeOf(child).ShouldBe(volume);
    }

    [Fact]
    public void AProfilesRootInsideTheLiveDirectoryIsRefused()
    {
        ClaudeCodeAccountRotationConfiguration configuration = Defaults() with { ProfilesRoot = Path.Combine(_home, ".claude", "profiles") };

        ConfigurationValidator.Validate(configuration, _home, static _ => "C:", static _ => null).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void AProfilesRootThatIsTheHomeRootIsRefused()
    {
        ClaudeCodeAccountRotationConfiguration configuration = Defaults() with { ProfilesRoot = _home };

        ConfigurationValidator.Validate(configuration, _home, static _ => "C:", static _ => null).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void AProfilesRootUnderASyncFolderIsRefused()
    {
        string oneDrive = Path.Combine(_home, "OneDrive");
        ClaudeCodeAccountRotationConfiguration configuration = Defaults() with { ProfilesRoot = Path.Combine(oneDrive, "profiles") };
        string? Environment(string name) => name == "OneDrive" ? oneDrive : null;

        Result<Unit, string> verdict = ConfigurationValidator.Validate(configuration, _home, static _ => "C:", Environment);

        verdict.IsFailure.ShouldBeTrue();
        verdict.Error.ShouldContain("OneDrive");
    }

    [Fact]
    public void ADropboxFolderUnderTheProfileIsRefusedWithoutAnyVariable()
    {
        ClaudeCodeAccountRotationConfiguration configuration = Defaults() with { ProfilesRoot = Path.Combine(_home, "Dropbox", "claude-profiles") };

        ConfigurationValidator.Validate(configuration, _home, static _ => "C:", static _ => null).Error.ShouldContain("Dropbox");
    }

    [Fact]
    public void AnAppDataDirectoryOnAnotherVolumeIsRefused()
    {
        // Quarantined pairs move into app data by rename, which cannot cross a volume.
        ClaudeCodeAccountRotationConfiguration configuration = Defaults();
        string VolumeOf(string path) => path.StartsWith(configuration.AppDataDirectory, StringComparison.Ordinal) ? "E:" : "C:";

        Result<Unit, string> verdict = ConfigurationValidator.Validate(configuration, _home, VolumeOf, static _ => null);

        verdict.IsFailure.ShouldBeTrue();
        verdict.Error.ShouldContain(configuration.AppDataDirectory);
    }

    [Fact]
    public void AnAppDataDirectoryUnderASyncFolderIsRefused()
    {
        ClaudeCodeAccountRotationConfiguration configuration = Defaults() with { AppDataDirectory = Path.Combine(_home, "OneDrive", "claude-code-account-rotation") };

        Result<Unit, string> verdict = ConfigurationValidator.Validate(configuration, _home, static _ => "C:", static _ => null);

        verdict.IsFailure.ShouldBeTrue();
        verdict.Error.ShouldContain("OneDrive");
    }

    [Theory]
    [InlineData("Dropbox (Personal)")]
    [InlineData("OneDrive - Contoso")]
    [InlineData("My Drive")]
    [InlineData("iCloudDrive")]
    [InlineData("Box")]
    public void ASyncFolderIsRecognizedByItsPrefixWhateverTheSuffix(string folderName)
    {
        ClaudeCodeAccountRotationConfiguration configuration = Defaults() with { ProfilesRoot = Path.Combine(_home, folderName, "claude-profiles") };

        Result<Unit, string> verdict = ConfigurationValidator.Validate(configuration, _home, static _ => "C:", static _ => null);

        verdict.IsFailure.ShouldBeTrue();
        verdict.Error.ShouldContain(folderName);
    }

    [Fact]
    public void TheDefaultsThemselvesValidate()
    {
        ConfigurationValidator.Validate(Defaults(), _home, static _ => "C:", static _ => null).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void StartupArgumentsParseConfigPortVersionAndHelp()
    {
        StartupArguments parsed = StartupArguments.Parse(["--config", "x.json", "--port", "5001"]).Value;
        parsed.ConfigPath.ShouldBe("x.json");
        parsed.Port.ShouldBe(5001);

        StartupArguments.Parse(["--version"]).Value.ShowVersion.ShouldBeTrue();
        StartupArguments.Parse(["--help"]).Value.ShowHelp.ShouldBeTrue();
        StartupArguments.Parse(["--port", "abc"]).IsFailure.ShouldBeTrue();
        // Unknown arguments belong to the host (and a test runner passes its own), so they pass through.
        StartupArguments.Parse(["--results-directory", "x", "--port", "5002"]).Value.Port.ShouldBe(5002);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
