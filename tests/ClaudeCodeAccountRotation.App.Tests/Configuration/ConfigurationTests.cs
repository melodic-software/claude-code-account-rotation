using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Configuration;
using ClaudeCodeAccountRotation.App.Security;
using ClaudeCodeAccountRotation.App.Tests.Switching;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Configuration;

namespace ClaudeCodeAccountRotation.App.Tests.Configuration;

public sealed class ConfigurationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "claude-code-account-rotation-tests", Guid.NewGuid().ToString("N"));
    private readonly string _home;

    public static bool OnUnix => !OperatingSystem.IsWindows();

    public static bool OnWindows => OperatingSystem.IsWindows();

    /// <summary>Two mounts the real volume resolver can tell apart, so a link onto the second is not a same-volume path.</summary>
    public static bool HasTwoVolumes =>
        !OperatingSystem.IsWindows()
        && Directory.Exists("/dev/shm")
        && Directory.Exists("/tmp")
        && ConfigurationValidator.VolumeOf("/dev/shm") != ConfigurationValidator.VolumeOf("/tmp");

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
    public async Task ALiveDirectoryInTheFileMovesTheStateFileUnderIt()
    {
        string live = Path.Combine(_root, "elsewhere");
        string path = Path.Combine(_root, "appdata", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(
            path,
            new JsonObject { ["liveConfigDirectory"] = live }.ToJsonString(),
            TestContext.Current.CancellationToken);

        Result<ClaudeCodeAccountRotationConfiguration, string> loaded = await ConfigurationFile.LoadOrCreateAsync(path, Defaults(), TestContext.Current.CancellationToken);

        loaded.IsSuccess.ShouldBeTrue(loaded.IsFailure ? loaded.Error : "");
        loaded.Value.StateFilePath.ShouldBe(Path.Combine(live, ".claude.json"));
        loaded.Value.StateFilePath.ShouldNotBe(Defaults().StateFilePath);
    }

    [Fact]
    public async Task AnExplicitStateFilePathIsKeptWhenTheLiveDirectoryIsAlsoSet()
    {
        string live = Path.Combine(_root, "elsewhere");
        string state = Path.Combine(_root, "custom", ".claude.json");
        string path = Path.Combine(_root, "appdata", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(
            path,
            new JsonObject { ["liveConfigDirectory"] = live, ["stateFilePath"] = state }.ToJsonString(),
            TestContext.Current.CancellationToken);

        Result<ClaudeCodeAccountRotationConfiguration, string> loaded = await ConfigurationFile.LoadOrCreateAsync(path, Defaults(), TestContext.Current.CancellationToken);

        loaded.IsSuccess.ShouldBeTrue(loaded.IsFailure ? loaded.Error : "");
        loaded.Value.LiveConfigDirectory.ShouldBe(live);
        loaded.Value.StateFilePath.ShouldBe(state);
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
    public async Task MissingConfigurationIsCreatedFromTheTemplateWithRuntimeDefaults()
    {
        string template = EmbeddedConfigTemplate.Json;
        // One key, and no path for it: the home directory is filled in when the file is written.
        template.Split("\"profilesRoot\"").Length.ShouldBe(2);
        template.ShouldNotContain(@":\");
        template.ShouldNotContain("/home/");
        template.ShouldNotContain("/Users/");
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        home.ShouldNotBeNullOrWhiteSpace();
        template.ShouldNotContain(home);
        JsonObject templateJson = JsonNode.Parse(template)!.AsObject();
        templateJson["profilesRoot"].ShouldBeNull();
        templateJson["liveConfigDirectory"].ShouldBeNull();
        templateJson["stateFilePath"].ShouldBeNull();
        templateJson["appDataDirectory"].ShouldBeNull();

        string path = Path.Combine(_root, "from-template", "config.json");
        Result<ClaudeCodeAccountRotationConfiguration, string> loaded = await ConfigurationFile.LoadOrCreateAsync(path, Defaults(), TestContext.Current.CancellationToken);

        loaded.IsSuccess.ShouldBeTrue(loaded.IsFailure ? loaded.Error : "");
        File.Exists(path).ShouldBeTrue();
        JsonObject written = JsonNode.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken))!.AsObject();
        written["profilesRoot"]!.GetValue<string>().ShouldBe(Defaults().ProfilesRoot);
        written["liveConfigDirectory"]!.GetValue<string>().ShouldBe(Defaults().LiveConfigDirectory);
        written["stateFilePath"]!.GetValue<string>().ShouldBe(Defaults().StateFilePath);
        written["appDataDirectory"]!.GetValue<string>().ShouldBe(Defaults().AppDataDirectory);
        template.ShouldNotContain(written["profilesRoot"]!.GetValue<string>());
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

    /// <summary>
    /// The target is on the same volume and is not a sync folder. The refusal
    /// is that the profiles root is itself a link, and the reason names no path.
    /// </summary>
    [Fact(SkipUnless = nameof(OnUnix), Skip = "Symbolic links are the Unix form of this refusal")]
    public void ASymlinkedProfilesRootIsRefusedWithoutNamingThePath()
    {
        string real = Path.Combine(_root, "real-profiles");
        string link = Path.Combine(_root, "linked-profiles");
        Directory.CreateDirectory(real);
        Directory.CreateSymbolicLink(link, real);

        Result<Unit, string> verdict = ConfigurationValidator.Validate(
            Defaults() with { ProfilesRoot = link },
            _home,
            static _ => "C:",
            static _ => null);

        verdict.IsFailure.ShouldBeTrue();
        verdict.Error.ShouldContain("symbolic link");
        verdict.Error.ShouldNotContain(link);
        verdict.Error.ShouldNotContain(real);
    }

    [Fact(SkipUnless = nameof(OnWindows), Skip = "Junctions are a Windows reparse point")]
    public void AJunctionProfilesRootIsRefusedWithoutNamingThePath()
    {
        string real = Path.Combine(_root, "real-profiles");
        string junction = Path.Combine(_root, "junction-profiles");
        Directory.CreateDirectory(real);
        if (!WindowsJunction.TryCreate(junction, real))
        {
            Assert.Skip("This process cannot create a directory junction.");
        }

        Result<Unit, string> verdict = ConfigurationValidator.Validate(
            Defaults() with { ProfilesRoot = junction },
            _home,
            static _ => "C:",
            static _ => null);

        verdict.IsFailure.ShouldBeTrue();
        verdict.Error.ShouldContain("junction");
        verdict.Error.ShouldNotContain(junction);
        verdict.Error.ShouldNotContain(real);
    }

    [Fact(SkipUnless = nameof(OnUnix), Skip = "Symbolic links are the Unix form of this refusal")]
    public void ASymlinkedAppDataDirectoryIsRefusedWithoutNamingThePath()
    {
        string real = Path.Combine(_root, "real-app");
        string link = Path.Combine(_root, "linked-app");
        Directory.CreateDirectory(real);
        Directory.CreateSymbolicLink(link, real);

        Result<Unit, string> verdict = ConfigurationValidator.Validate(
            Defaults() with { AppDataDirectory = link },
            _home,
            static _ => "C:",
            static _ => null);

        verdict.IsFailure.ShouldBeTrue();
        verdict.Error.ShouldContain("app data directory");
        verdict.Error.ShouldContain("symbolic link");
        verdict.Error.ShouldNotContain(link);
        verdict.Error.ShouldNotContain(real);
    }

    [Fact(SkipUnless = nameof(OnWindows), Skip = "Junctions are a Windows reparse point")]
    public void AJunctionAppDataDirectoryIsRefusedWithoutNamingThePath()
    {
        string real = Path.Combine(_root, "real-app");
        string junction = Path.Combine(_root, "junction-app");
        Directory.CreateDirectory(real);
        if (!WindowsJunction.TryCreate(junction, real))
        {
            Assert.Skip("This process cannot create a directory junction.");
        }

        Result<Unit, string> verdict = ConfigurationValidator.Validate(
            Defaults() with { AppDataDirectory = junction },
            _home,
            static _ => "C:",
            static _ => null);

        verdict.IsFailure.ShouldBeTrue();
        verdict.Error.ShouldContain("app data directory");
        verdict.Error.ShouldContain("junction");
        verdict.Error.ShouldNotContain(junction);
        verdict.Error.ShouldNotContain(real);
    }

    /// <summary>The profiles directory is not itself a link; an ancestor is, and the target is a sync folder.</summary>
    [Fact(SkipUnless = nameof(OnUnix), Skip = "Symbolic links are the Unix form of this refusal")]
    public void AProfilesRootReachedThroughALinkIntoASyncFolderIsRefused()
    {
        string dropbox = Path.Combine(_home, "Dropbox", "claude-profiles");
        Directory.CreateDirectory(dropbox);
        string via = Path.Combine(_root, "via-home");
        Directory.CreateSymbolicLink(via, _home);

        Result<Unit, string> verdict = ConfigurationValidator.Validate(
            Defaults() with { ProfilesRoot = Path.Combine(via, "Dropbox", "claude-profiles") },
            _home,
            static _ => "C:",
            static _ => null);

        verdict.IsFailure.ShouldBeTrue();
        verdict.Error.ShouldContain("Dropbox");
    }

    [Fact(SkipUnless = nameof(HasTwoVolumes), Skip = "Needs two mount points, which only the Unix legs have")]
    public void AProfilesRootReachedThroughALinkOntoAnotherVolumeIsRefused()
    {
        string remote = Path.Combine("/dev/shm", "ccar-link-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(remote);
        try
        {
            Directory.CreateDirectory(Path.Combine(remote, "profiles"));
            string link = Path.Combine(_root, "other-volume");
            Directory.CreateSymbolicLink(link, remote);

            Result<Unit, string> verdict = ConfigurationValidator.Validate(
                Defaults() with { ProfilesRoot = Path.Combine(link, "profiles") },
                _home,
                ConfigurationValidator.VolumeOf,
                static _ => null);

            verdict.IsFailure.ShouldBeTrue();
            verdict.Error.ShouldContain("same volume");
        }
        finally
        {
            Directory.Delete(remote, recursive: true);
        }
    }

    [Fact(SkipUnless = nameof(OnUnix), Skip = "Symbolic links are the Unix form of this refusal")]
    public void ADanglingProfilesRootIsRefusedWithoutNamingThePath()
    {
        string link = Path.Combine(_root, "dangling-profiles");
        Directory.CreateSymbolicLink(link, Path.Combine(_root, "missing-profiles"));

        Result<Unit, string> verdict = ConfigurationValidator.Validate(
            Defaults() with { ProfilesRoot = link },
            _home,
            static _ => "C:",
            static _ => null);

        verdict.IsFailure.ShouldBeTrue();
        verdict.Error.ShouldContain("missing");
        verdict.Error.ShouldNotContain(link);
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
        StartupArguments.Parse(["-h"]).Value.ShowHelp.ShouldBeTrue();
        StartupArguments.Parse(["/?"]).Value.ShowHelp.ShouldBeTrue();
        StartupArguments.Parse(["--port", "0"]).Value.Port.ShouldBe(0);
        StartupArguments.Parse(["--port", "abc"]).IsFailure.ShouldBeTrue();
        StartupArguments.Parse(["--port", "-1"]).IsFailure.ShouldBeTrue();
        // Unknown arguments belong to the host (and a test runner passes its own), so they pass through.
        StartupArguments.Parse(["--results-directory", "x", "--port", "5002"]).Value.Port.ShouldBe(5002);
    }

    [Fact]
    public async Task HelpListsEveryFlagAndDoesNotStartTheServer()
    {
        using Process process = new();
        process.StartInfo = new ProcessStartInfo(FollowerProcess.ExecutablePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        process.StartInfo.ArgumentList.Add("--help");
        process.Start().ShouldBeTrue();

        Task<string> stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, TestContext.Current.CancellationToken);
        bool exited = true;
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (!process.HasExited)
        {
            exited = false;
            process.Kill(entireProcessTree: true);
        }

        exited.ShouldBeTrue("`--help` kept running, so the server started");
        process.ExitCode.ShouldBe(0);
        string output = await stdout;
        output.ShouldContain("--config");
        output.ShouldContain("--port");
        output.ShouldContain("--version");
        output.ShouldContain("--help");
        output.ShouldContain("-h");
        output.ShouldContain("/?");
        output.ShouldContain("0 lets the operating system choose a free port");
        output.ShouldNotContain("Dashboard:");
        (await stderr).ShouldBeEmpty();
    }

    [Fact]
    public async Task AnEphemeralPortPrintsTheDashboardUrlAndServesIt()
    {
        string live = Path.Combine(_root, "live");
        string profiles = Path.Combine(_root, "profiles");
        string appData = Path.Combine(_root, "appdata-serve");
        Directory.CreateDirectory(live);
        Directory.CreateDirectory(profiles);
        Directory.CreateDirectory(appData);
        string configPath = Path.Combine(appData, "config.json");
        await File.WriteAllTextAsync(
            configPath,
            new JsonObject
            {
                ["liveConfigDirectory"] = live,
                ["stateFilePath"] = Path.Combine(_root, ".claude.json"),
                ["profilesRoot"] = profiles,
                ["appDataDirectory"] = appData,
                ["listenPort"] = 48211,
                ["store"] = new JsonObject { ["shared"] = false },
            }.ToJsonString(),
            TestContext.Current.CancellationToken);

        using Process process = new();
        process.StartInfo = new ProcessStartInfo(FollowerProcess.ExecutablePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        process.StartInfo.ArgumentList.Add("--config");
        process.StartInfo.ArgumentList.Add(configPath);
        process.StartInfo.ArgumentList.Add("--port");
        process.StartInfo.ArgumentList.Add("0");
        // The file names every path. This keeps a missing key from falling through to this machine.
        process.StartInfo.Environment["CLAUDE_CONFIG_DIR"] = live;
        process.Start().ShouldBeTrue();

        StringBuilder output = new();
        TaskCompletionSource<string> dashboard = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Task stdout = ReadLinesAsync(process.StandardOutput, output, dashboard, cancellationToken);
        Task stderr = ReadLinesAsync(process.StandardError, output, dashboard, cancellationToken);
        try
        {
            string? url = await WaitForDashboardAsync(process, dashboard, cancellationToken);
            url.ShouldNotBeNull(output.ToString());
            url.ShouldStartWith("http://127.0.0.1:");

            using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(20) };
            using HttpResponseMessage response = await client.GetAsync(new Uri(url + "/api/dashboard"), cancellationToken);
            ((int)response.StatusCode).ShouldBe(200, output.ToString());

            // The printed port is the origin mutations accept. The file's listenPort
            // is a different number on this launch, and a page opened there is not
            // this instance.
            using HttpRequestMessage boundOrigin = new(HttpMethod.Post, new Uri(url + "/api/accounts/not-an-email/switch"));
            boundOrigin.Headers.TryAddWithoutValidation(SameOriginMutationFilter.HeaderName, "1");
            boundOrigin.Headers.TryAddWithoutValidation("Origin", url);
            using HttpResponseMessage posted = await client.SendAsync(boundOrigin, cancellationToken);
            ((int)posted.StatusCode).ShouldBe(400, output.ToString());

            int boundPort = new Uri(url).Port;
            if (boundPort != 48211)
            {
                using HttpRequestMessage configuredOrigin = new(HttpMethod.Post, new Uri(url + "/api/accounts/not-an-email/switch"));
                configuredOrigin.Headers.TryAddWithoutValidation(SameOriginMutationFilter.HeaderName, "1");
                configuredOrigin.Headers.TryAddWithoutValidation("Origin", "http://127.0.0.1:48211");
                using HttpResponseMessage rejected = await client.SendAsync(configuredOrigin, cancellationToken);
                ((int)rejected.StatusCode).ShouldBe(403, output.ToString());
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }

            await stdout;
            await stderr;
        }
    }

    private static async Task ReadLinesAsync(StreamReader reader, StringBuilder output, TaskCompletionSource<string> dashboard, CancellationToken cancellationToken)
    {
        try
        {
            while (await reader.ReadLineAsync(cancellationToken) is string line)
            {
                Note(output, dashboard, line);
            }
        }
        catch (OperationCanceledException)
        {
            // The test is already leaving; the process is stopped in the caller.
        }
    }

    private static void Note(StringBuilder output, TaskCompletionSource<string> dashboard, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (output)
        {
            output.AppendLine(line);
        }

        const string prefix = "Dashboard: ";
        if (line.StartsWith(prefix, StringComparison.Ordinal))
        {
            dashboard.TrySetResult(line[prefix.Length..]);
        }
    }

    private static async Task<string?> WaitForDashboardAsync(Process process, TaskCompletionSource<string> dashboard, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        try
        {
            Task exit = process.WaitForExitAsync(linked.Token);
            Task winner = await Task.WhenAny(dashboard.Task, exit);
            if (winner == dashboard.Task)
            {
                return await dashboard.Task;
            }

            await exit;
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        return dashboard.Task.IsCompleted ? await dashboard.Task : null;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
