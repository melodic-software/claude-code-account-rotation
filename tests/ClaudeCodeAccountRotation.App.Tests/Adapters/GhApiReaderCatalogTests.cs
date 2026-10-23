using System.Text;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.Process;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Accounts;
using ClaudeCodeAccountRotation.Core.Configuration;

namespace ClaudeCodeAccountRotation.App.Tests.Adapters;

/// <summary>
/// The lane catalog read through the contents API. Answers follow GitHub's
/// documented "Get repository content" shape for a file: <c>type</c>
/// <c>file</c>, <c>encoding</c> <c>base64</c>, and the base64 <c>content</c>
/// wrapped at 60 characters
/// (https://docs.github.com/en/rest/repos/contents#get-repository-content).
/// </summary>
public sealed class GhApiReaderCatalogTests
{
    private const string Catalog = """
        { "version": 1, "org": "octo", "roles": [ { "id": "intake", "secretPrefix": "INTAKE",
          "tokenSecret": "INTAKE_CLAUDE_CODE_OAUTH_TOKEN", "keySecret": "INTAKE_CLAUDE_API_KEY", "keyed": true,
          "category": "intake", "lanes": [] } ] }
        """;

    private static JsonObject File(string text)
    {
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        string wrapped = string.Join("\n", Enumerable.Range(0, (encoded.Length + 59) / 60).Select(index => encoded.Substring(index * 60, Math.Min(60, encoded.Length - (index * 60)))));
        return new JsonObject { ["type"] = "file", ["encoding"] = "base64", ["path"] = "lane-funding.json", ["content"] = wrapped + "\n" };
    }

    [Fact]
    public async Task TheFileIsReadAtItsRefAndParsed()
    {
        Answers reader = new(Result<JsonNode, GhApiFailure>.Success(File(Catalog)));

        Result<LaneCatalog, GhApiFailure> read = await reader.ReadLaneCatalogAsync(
            new LaneCatalogSource("melodic-software/claude-code-plugins", ".github/lane-funding.json", "feat/lane-funding-catalog"),
            TestContext.Current.CancellationToken);

        reader.Paths.ShouldBe(["repos/melodic-software/claude-code-plugins/contents/.github/lane-funding.json?ref=feat%2Flane-funding-catalog"]);
        read.Value.Organization.ShouldBe("octo");
        read.Value.Roles.Single().Prefix.ShouldBe("INTAKE");
    }

    [Fact]
    public async Task WithNoRefTheDefaultBranchIsRead()
    {
        Answers reader = new(Result<JsonNode, GhApiFailure>.Success(File(Catalog)));

        await reader.ReadLaneCatalogAsync(new LaneCatalogSource("octo/lanes", "lane-funding.json", null), TestContext.Current.CancellationToken);

        reader.Paths.ShouldBe(["repos/octo/lanes/contents/lane-funding.json"]);
    }

    [Fact]
    public async Task AMissingFileSaysWhereItLooked()
    {
        Answers reader = new(Result<JsonNode, GhApiFailure>.Failure(new GhApiFailure("GitHub answered HTTP 404", RateLimited: false, Status: 404)));

        Result<LaneCatalog, GhApiFailure> read = await reader.ReadLaneCatalogAsync(new LaneCatalogSource("octo/lanes", "lane-funding.json", "main"), TestContext.Current.CancellationToken);

        read.Error.Message.ShouldBe("no lane catalog at lane-funding.json in octo/lanes at main (GitHub answered HTTP 404)");
    }

    [Fact]
    public async Task ARateLimitStaysARateLimit()
    {
        Answers reader = new(Result<JsonNode, GhApiFailure>.Failure(new GhApiFailure("rate limited", RateLimited: true)));

        Result<LaneCatalog, GhApiFailure> read = await reader.ReadLaneCatalogAsync(new LaneCatalogSource("octo/lanes", "lane-funding.json", null), TestContext.Current.CancellationToken);

        read.Error.RateLimited.ShouldBeTrue();
    }

    [Fact]
    public async Task ADirectoryIsNoCatalog()
    {
        Answers reader = new(Result<JsonNode, GhApiFailure>.Success(new JsonArray(new JsonObject { ["type"] = "file", ["name"] = "a.json" })));

        Result<LaneCatalog, GhApiFailure> read = await reader.ReadLaneCatalogAsync(new LaneCatalogSource("octo/lanes", ".github", null), TestContext.Current.CancellationToken);

        read.Error.Message.ShouldContain("not a file");
    }

    [Theory]
    [InlineData("""{ "version": 2, "org": "octo", "roles": [] }""", "version 2")]
    [InlineData("not json", "is not JSON")]
    public async Task AFileThatIsNoVersionOneCatalogIsRefused(string text, string why)
    {
        Answers reader = new(Result<JsonNode, GhApiFailure>.Success(File(text)));

        Result<LaneCatalog, GhApiFailure> read = await reader.ReadLaneCatalogAsync(new LaneCatalogSource("octo/lanes", "lane-funding.json", null), TestContext.Current.CancellationToken);

        read.Error.Message.ShouldContain(why);
        read.Error.RateLimited.ShouldBeFalse();
    }

    [Fact]
    public async Task ContentThatIsNotBase64IsRefused()
    {
        Answers reader = new(Result<JsonNode, GhApiFailure>.Success(new JsonObject { ["type"] = "file", ["encoding"] = "base64", ["content"] = "%%%" }));

        Result<LaneCatalog, GhApiFailure> read = await reader.ReadLaneCatalogAsync(new LaneCatalogSource("octo/lanes", "lane-funding.json", null), TestContext.Current.CancellationToken);

        read.Error.Message.ShouldContain("not base64");
    }

    private sealed class Answers(Result<JsonNode, GhApiFailure> answer) : IGhApiReader
    {
        public List<string> Paths { get; } = [];

        public Task<Result<JsonNode, GhApiFailure>> GetAsync(string path, CancellationToken cancellationToken)
        {
            Paths.Add(path);
            return Task.FromResult(answer);
        }
    }
}
