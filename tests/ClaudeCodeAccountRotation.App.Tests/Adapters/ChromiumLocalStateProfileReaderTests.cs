using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.Core.Accounts;

namespace ClaudeCodeAccountRotation.App.Tests.Adapters;

public sealed class ChromiumLocalStateProfileReaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "claude-code-account-rotation-tests", Guid.NewGuid().ToString("N"));

    public ChromiumLocalStateProfileReaderTests() => Directory.CreateDirectory(_directory);

    /// <summary>
    /// The shape a Chromium-family browser actually writes, minus the dozens of
    /// keys this reader never looks at. Edge is the reason both fields are
    /// shown: its directory key and its display name disagree, so neither one
    /// alone tells the operator which profile they are picking.
    /// </summary>
    private static string LocalState(params (string Directory, string? Name, string? UserName)[] profiles)
    {
        JsonObject cache = [];
        foreach ((string directory, string? name, string? userName) in profiles)
        {
            JsonObject entry = [];
            if (name is not null)
            {
                entry["name"] = name;
            }

            if (userName is not null)
            {
                entry["user_name"] = userName;
            }

            cache[directory] = entry;
        }

        return new JsonObject { ["profile"] = new JsonObject { ["info_cache"] = cache } }.ToJsonString();
    }

    private string PathFor(BrowserFamily browser) => Path.Combine(_directory, browser.ToString(), "Local State");

    private async Task WriteAsync(BrowserFamily browser, string content)
    {
        string path = PathFor(browser);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);
    }

    private ChromiumLocalStateProfileReader Reader() => new(PathFor);

    [Fact]
    public async Task EveryProfileCarriesItsDirectoryItsDisplayNameAndItsSignedInAddress()
    {
        await WriteAsync(BrowserFamily.Chrome, LocalState(
            ("Profile 1", "Your Chrome", "one@example.com"),
            ("Profile 2", "Kyle", "two@example.com")));
        // The Edge shape: the directory is "Default" and the name it shows is
        // "Profile 1", which is also another browser's directory name.
        await WriteAsync(BrowserFamily.Edge, LocalState(("Default", "Profile 1", "three@example.com")));
        // Brave, signed into nothing: a profile with no address at all.
        await WriteAsync(BrowserFamily.Brave, LocalState(("Default", "Personal", null)));

        IReadOnlyList<BrowserProfile> profiles = await Reader().ReadAsync(TestContext.Current.CancellationToken);

        profiles.Select(profile => (profile.Browser, profile.Directory, profile.Name, profile.SignedInAs?.Value)).ShouldBe(
        [
            (BrowserFamily.Chrome, "Profile 1", "Your Chrome", "one@example.com"),
            (BrowserFamily.Chrome, "Profile 2", "Kyle", "two@example.com"),
            (BrowserFamily.Edge, "Default", "Profile 1", "three@example.com"),
            (BrowserFamily.Brave, "Default", "Personal", null),
        ]);
    }

    [Fact]
    public async Task AProfileWhoseUserNameIsNotAnAddressIsSurfacedAsNoAddress()
    {
        // AccountEmail.Parse is an allowlist, and this file is written by another
        // program. An unparsable value leaves the profile unattributed rather
        // than failing the enumeration the whole page depends on.
        await WriteAsync(BrowserFamily.Chrome, LocalState(("Profile 1", "Work", "Someone (Work) <one@example.com>")));

        IReadOnlyList<BrowserProfile> profiles = await Reader().ReadAsync(TestContext.Current.CancellationToken);

        profiles.Count.ShouldBe(1);
        profiles[0].Directory.ShouldBe("Profile 1");
        profiles[0].SignedInAs.ShouldBeNull();
    }

    [Theory]
    // A blank name is no name: it would render an option nobody can read, and
    // the directory is the one thing every profile certainly has.
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AProfileWithoutAUsableNameFallsBackToItsDirectory(string? name)
    {
        await WriteAsync(BrowserFamily.Chrome, LocalState(("Profile 7", name, null)));

        (await Reader().ReadAsync(TestContext.Current.CancellationToken))[0].Name.ShouldBe("Profile 7");
    }

    [Fact]
    // The browser numbers directories from a counter it never decrements and
    // writes info_cache in whatever order it likes, so the file's order is a
    // deletion history, not a list. The operator reads Default first, then the
    // numbers as numbers, then anything the browser named some other way.
    public async Task ProfilesComeBackDefaultFirstThenByNumberThenTheRestByName()
    {
        await WriteAsync(BrowserFamily.Edge, LocalState(
            ("Profile 10", "Ten", null),
            ("Profile 2", "Two", null),
            ("Work", "Work", null),
            ("Default", "Profile 1", null),
            ("Profile 1", "One", null)));

        IReadOnlyList<BrowserProfile> profiles = await Reader().ReadAsync(TestContext.Current.CancellationToken);

        profiles.Select(profile => profile.Directory).ShouldBe(["Default", "Profile 1", "Profile 2", "Profile 10", "Work"]);
    }

    [Theory]
    // Every way the file can fail to answer is the same answer: this browser
    // has no profiles. A browser the operator never installed must not take
    // down the page for the ones they did.
    [InlineData(null)]
    [InlineData("""{"profile":{"info_cache":{"Profile 1":""")]
    [InlineData("""{"profile":{}}""")]
    [InlineData("""{"profile":{"info_cache":[]}}""")]
    [InlineData("{}")]
    public async Task ABrowserThatCannotBeReadContributesNoProfilesAndLeavesTheOthersAlone(string? edgeContent)
    {
        await WriteAsync(BrowserFamily.Chrome, LocalState(("Profile 1", "Work", "one@example.com")));
        if (edgeContent is not null)
        {
            await WriteAsync(BrowserFamily.Edge, edgeContent);
        }

        IReadOnlyList<BrowserProfile> profiles = await Reader().ReadAsync(TestContext.Current.CancellationToken);

        profiles.Select(profile => profile.Browser).ShouldBe([BrowserFamily.Chrome]);
    }

    [Fact]
    public async Task APathThatCannotBeOpenedContributesNoProfiles()
    {
        await WriteAsync(BrowserFamily.Chrome, LocalState(("Profile 1", "Work", "one@example.com")));
        await WriteAsync(BrowserFamily.Edge, LocalState(("Default", "Other", "two@example.com")));
        string denied = PathFor(BrowserFamily.Edge);
        RateLimitsTeeFileReaderTests.DenyReading(denied);
        try
        {
            IReadOnlyList<BrowserProfile> profiles = await Reader().ReadAsync(TestContext.Current.CancellationToken);

            profiles.Select(profile => profile.Browser).ShouldBe([BrowserFamily.Chrome]);
        }
        finally
        {
            // Before Dispose deletes the directory, or the cleanup fails too.
            RateLimitsTeeFileReaderTests.AllowReading(denied);
        }
    }

    [Fact]
    public async Task ABrowserWithNoKnownUserDataLocationContributesNoProfiles()
    {
        await WriteAsync(BrowserFamily.Chrome, LocalState(("Profile 1", "Work", "one@example.com")));
        ChromiumLocalStateProfileReader reader = new(browser => browser == BrowserFamily.Chrome ? PathFor(browser) : null);

        (await reader.ReadAsync(TestContext.Current.CancellationToken)).Count.ShouldBe(1);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
