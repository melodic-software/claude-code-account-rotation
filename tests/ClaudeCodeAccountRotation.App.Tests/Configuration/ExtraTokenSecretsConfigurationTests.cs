using ClaudeCodeAccountRotation.App.Configuration;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Configuration;

namespace ClaudeCodeAccountRotation.App.Tests.Configuration;

/// <summary>
/// <c>extraTokenSecrets</c>: absent in every file an earlier release wrote,
/// which keeps the two defaults; a list replaces them; a wrong shape or a name
/// GitHub would refuse stops the load.
/// </summary>
public sealed class ExtraTokenSecretsConfigurationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "claude-code-account-rotation-tests", Guid.NewGuid().ToString("N"));
    private readonly string _home;

    public ExtraTokenSecretsConfigurationTests()
    {
        _home = Path.Combine(_root, "home");
        Directory.CreateDirectory(_home);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private ClaudeCodeAccountRotationConfiguration Defaults() =>
        ConfigurationDefaults.ForUser(_home, Path.Combine(_home, "AppData", "Local"), null);

    private async Task<Result<ClaudeCodeAccountRotationConfiguration, string>> LoadAsync(string json)
    {
        string path = Path.Combine(_root, "appdata", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, json, TestContext.Current.CancellationToken);
        return await ConfigurationFile.LoadOrCreateAsync(path, Defaults(), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AFileWithoutTheKeyGetsTheSharedReviewTokenAndTheFallback()
    {
        Result<ClaudeCodeAccountRotationConfiguration, string> loaded = await LoadAsync("{}");

        loaded.Value.ExtraTokenSecrets.ShouldBe(["CLAUDE_CODE_OAUTH_TOKEN", "FALLBACK_CLAUDE_CODE_OAUTH_TOKEN"]);
    }

    [Fact]
    public async Task AListReplacesTheDefaultsAndAnEmptyOneTurnsThemOff()
    {
        (await LoadAsync("""{"extraTokenSecrets": [" SPARE_CLAUDE_CODE_OAUTH_TOKEN "]}""")).Value.ExtraTokenSecrets.ShouldBe(["SPARE_CLAUDE_CODE_OAUTH_TOKEN"]);
        (await LoadAsync("""{"extraTokenSecrets": []}""")).Value.ExtraTokenSecrets.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("\"FALLBACK_CLAUDE_CODE_OAUTH_TOKEN\"")]
    [InlineData("[7]")]
    public async Task AValueThatIsNotAListOfNamesIsRefused(string value) =>
        (await LoadAsync("{\"extraTokenSecrets\": " + value + "}")).Error.ShouldContain("extraTokenSecrets that is not an array of secret names");

    [Theory]
    [InlineData("GITHUB_TOKEN")]
    [InlineData("has space")]
    [InlineData("--org=evil")]
    public void ANameGitHubWouldRefuseFailsValidation(string name) =>
        ConfigurationValidator.Validate(Defaults() with { ExtraTokenSecrets = [name] }, _home, static _ => "/", static _ => null)
            .Error.ShouldContain("extraTokenSecrets");
}
