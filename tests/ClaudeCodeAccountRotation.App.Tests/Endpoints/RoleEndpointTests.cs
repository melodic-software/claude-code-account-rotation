using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.App.Adapters.Process;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Accounts;
using ClaudeCodeAccountRotation.Core.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ClaudeCodeAccountRotation.App.Tests.Endpoints;

/// <summary>
/// Check roles: GitHub is read on that click only, never with no organization
/// to read, and not past the first rate-limit answer.
/// </summary>
public sealed class RoleEndpointTests
{
    private const string LiveEmail = "live@example.com";
    private const string HolderEmail = "holder@example.com";

    private static readonly Uri _check = new("/api/roles/check", UriKind.Relative);

    private static async Task<(AppFactory Factory, SpyReader Reader)> StartAsync(CancellationToken cancellationToken)
    {
        AppFactory factory = new();
        SpyReader reader = new();
        factory.Overrides = services => services.Replace(ServiceDescriptor.Singleton<IGhApiReader>(reader));
        await CredentialFiles.WriteAsync(factory.LiveDirectory, "refresh-live", cancellationToken);
        await factory.WriteStateFileAsync(LiveEmail, cancellationToken);
        factory.Cli.Email = LiveEmail;
        return (factory, reader);
    }

    private static Task<Roster> HoldAsync(AppFactory factory, params CiRoleAssignment[] roles) =>
        factory.Services.GetRequiredService<RosterFile>().UpdateAsync(
            roster => roster.With(new RosterEntry(AccountEmail.Parse(HolderEmail).Value).WithCiRoles(roles)),
            TestContext.Current.CancellationToken);

    /// <summary>A page in the documented shape of "List organization secrets": https://docs.github.com/en/rest/actions/secrets#list-organization-secrets.</summary>
    private static JsonObject Page(int total, params (string Name, string UpdatedAt)[] secrets) => new JsonObject
    {
        ["total_count"] = total,
        ["secrets"] = new JsonArray([.. secrets.Select(static secret => (JsonNode)new JsonObject
        {
            ["name"] = secret.Name,
            ["created_at"] = "2026-01-01T00:00:00Z",
            ["updated_at"] = secret.UpdatedAt,
            ["visibility"] = "all",
        })]),
    };

    [Fact]
    public async Task TheDashboardNeverStartsGh()
    {
        (AppFactory factory, SpyReader reader) = await StartAsync(TestContext.Current.CancellationToken);
        await using AppFactory _ = factory;
        using HttpClient client = factory.CreateClient();
        await HoldAsync(factory, new CiRoleAssignment(new CiRole("AUTOMATIONS", CiSecretScope.Organization, "octo"), new DateOnly(2026, 10, 1)));

        using HttpResponseMessage response = await client.GetAsync(new Uri("/api/dashboard", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        reader.Paths.ShouldBeEmpty();
    }

    [Fact]
    public async Task WithNoOrganizationNamedNothingIsRead()
    {
        (AppFactory factory, SpyReader reader) = await StartAsync(TestContext.Current.CancellationToken);
        await using AppFactory _ = factory;
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(_check, new { organization = "  " }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        reader.Paths.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnOrganizationThatIsNotALoginIsRefusedBeforeAnyRead()
    {
        (AppFactory factory, SpyReader reader) = await StartAsync(TestContext.Current.CancellationToken);
        await using AppFactory _ = factory;
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(_check, new { organization = "octo/../users" }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        reader.Paths.ShouldBeEmpty();
    }

    [Fact]
    public async Task ARateLimitEndsTheCheckAfterThatCall()
    {
        (AppFactory factory, SpyReader reader) = await StartAsync(TestContext.Current.CancellationToken);
        await using AppFactory _ = factory;
        using HttpClient client = factory.CreateMutatingClient();
        await HoldAsync(factory, new CiRoleAssignment(new CiRole("AUTOMATIONS", CiSecretScope.Organization, "alpha"), new DateOnly(2026, 10, 1)));
        reader.Answers.Enqueue(Result<JsonNode, GhApiFailure>.Failure(new GhApiFailure(GhApiReader.RateLimitMessage, RateLimited: true)));

        using HttpResponseMessage response = await client.PostAsJsonAsync(_check, new { organization = "beta" }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        JsonObject body = (await response.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken))!;
        body["message"]!.GetValue<string>().ShouldBe(GhApiReader.RateLimitMessage);
        reader.Paths.ShouldBe(["orgs/alpha/actions/secrets?per_page=100&page=1"]);
    }

    [Fact]
    public async Task TheCheckListsRolesWithHoldersAndVerdicts()
    {
        (AppFactory factory, SpyReader reader) = await StartAsync(TestContext.Current.CancellationToken);
        await using AppFactory _ = factory;
        using HttpClient client = factory.CreateMutatingClient();
        await HoldAsync(factory, new CiRoleAssignment(
            new CiRole("AUTOMATIONS", CiSecretScope.Organization, "octo"),
            new DateOnly(2026, 10, 1),
            new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero)));
        reader.Answers.Enqueue(Result<JsonNode, GhApiFailure>.Success(Page(
            3,
            ("AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN", "2026-10-01T09:10:00Z"),
            ("ISSUE_LANE_CLAUDE_CODE_OAUTH_TOKEN", "2026-09-01T00:00:00Z"),
            ("ISSUE_LANE_CLAUDE_API_KEY", "2026-09-01T00:00:00Z"))));

        using HttpResponseMessage response = await client.PostAsJsonAsync(_check, new { }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonObject body = (await response.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken))!;
        JsonArray roles = body["roles"]!.AsArray();
        roles.Select(static role => (
            role!["name"]!.GetValue<string>(),
            role["holder"]?.GetValue<string>(),
            role["token"]?.GetValue<string>(),
            role["key"]?.GetValue<string>())).ShouldBe([
                ("AUTOMATIONS", HolderEmail, "changed", null),
                ("ISSUE_LANE", null, "unclaimed", "unclaimed"),
            ]);
        reader.Paths.ShouldBe(["orgs/octo/actions/secrets?per_page=100&page=1"]);
    }

    [Fact]
    public async Task AnUnknownHalfCarriesItsReason()
    {
        (AppFactory factory, SpyReader reader) = await StartAsync(TestContext.Current.CancellationToken);
        await using AppFactory _ = factory;
        using HttpClient client = factory.CreateMutatingClient();
        DateTimeOffset setAt = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
        await HoldAsync(
            factory,
            new CiRoleAssignment(new CiRole("AUTOMATIONS", CiSecretScope.Organization, "octo"), new DateOnly(2026, 10, 1), null, new DateOnly(2026, 10, 1), setAt),
            new CiRoleAssignment(new CiRole("ISSUE_LANE", CiSecretScope.Organization, "octo"), new DateOnly(2026, 10, 1), setAt));
        // The clock's zone is the machine's, so the date-only half is listed at noon UTC on its day: the same local day anywhere from UTC-11 to UTC+11.
        reader.Answers.Enqueue(Result<JsonNode, GhApiFailure>.Success(Page(
            3,
            ("AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN", "2026-10-01T12:00:00Z"),
            ("AUTOMATIONS_CLAUDE_API_KEY", "2026-10-01T08:00:00Z"),
            ("ISSUE_LANE_CLAUDE_CODE_OAUTH_TOKEN", "not a time"))));

        using HttpResponseMessage response = await client.PostAsJsonAsync(_check, new { }, TestContext.Current.CancellationToken);

        JsonObject body = (await response.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken))!;
        body["roles"]!.AsArray().Select(static role => (
            role!["name"]!.GetValue<string>(),
            role["token"]?.GetValue<string>(),
            role["tokenReason"]?.GetValue<string>(),
            role["key"]?.GetValue<string>(),
            role["keyReason"]?.GetValue<string>())).ShouldBe([
                ("AUTOMATIONS", "unknown", "noInstant", "unknown", "olderThanWrite"),
                ("ISSUE_LANE", "unknown", "noListedTime", null, null),
            ]);
    }

    /// <summary>A full page of 100 secrets named with <paramref name="stem"/>, under a stated total.</summary>
    private static JsonObject FullPage(int total, string stem) =>
        Page(total, [.. Enumerable.Range(0, 100).Select(index => (stem + index + "_CLAUDE_CODE_OAUTH_TOKEN", "2026-10-01T00:00:00Z"))]);

    [Fact]
    public async Task ATooLowTotalStopsTheListingOnlyAtAShortPage()
    {
        SpyReader reader = new();
        reader.Answers.Enqueue(Result<JsonNode, GhApiFailure>.Success(FullPage(1, "A")));
        reader.Answers.Enqueue(Result<JsonNode, GhApiFailure>.Success(Page(1, ("B_CLAUDE_CODE_OAUTH_TOKEN", "2026-10-01T00:00:00Z"))));

        Result<OrganizationSecrets, GhApiFailure> listed = await reader.ListOrganizationSecretsAsync("octo", TestContext.Current.CancellationToken);

        listed.Value.Secrets.Count.ShouldBe(101);
        listed.Value.Secrets[^1].Name.ShouldBe("B_CLAUDE_CODE_OAUTH_TOKEN");
        reader.Paths.ShouldBe([
            "orgs/octo/actions/secrets?per_page=100&page=1",
            "orgs/octo/actions/secrets?per_page=100&page=2",
        ]);
    }

    [Fact]
    public async Task AListingPastFiftyPagesIsReportedAndNotUsed()
    {
        SpyReader reader = new();
        for (int page = 0; page < 51; page++)
        {
            reader.Answers.Enqueue(Result<JsonNode, GhApiFailure>.Success(FullPage(6000, "P" + page + "_")));
        }

        Result<OrganizationSecrets, GhApiFailure> listed = await reader.ListOrganizationSecretsAsync("octo", TestContext.Current.CancellationToken);

        listed.Error.Message.ShouldContain("50 pages");
        listed.Error.RateLimited.ShouldBeFalse();
        reader.Paths.Count.ShouldBe(50);
    }

    [Fact]
    public async Task ASecretWithAnUnreadableTimeIsKeptWithNone()
    {
        SpyReader reader = new();
        reader.Answers.Enqueue(Result<JsonNode, GhApiFailure>.Success(Page(1, ("A_CLAUDE_CODE_OAUTH_TOKEN", "not a time"))));

        Result<OrganizationSecrets, GhApiFailure> listed = await reader.ListOrganizationSecretsAsync("octo", TestContext.Current.CancellationToken);

        listed.Value.Secrets.ShouldBe([new ListedSecret("A_CLAUDE_CODE_OAUTH_TOKEN", null)]);
    }

    /// <summary>Records every path it is asked for and answers from a queue; an empty queue fails the read.</summary>
    private sealed class SpyReader : IGhApiReader
    {
        public List<string> Paths { get; } = [];

        public Queue<Result<JsonNode, GhApiFailure>> Answers { get; } = new();

        public Task<Result<JsonNode, GhApiFailure>> GetAsync(string path, CancellationToken cancellationToken)
        {
            Paths.Add(path);
            return Task.FromResult(Answers.TryDequeue(out Result<JsonNode, GhApiFailure> answer)
                ? answer
                : Result<JsonNode, GhApiFailure>.Failure(new GhApiFailure("no answer scripted", RateLimited: false)));
        }
    }
}
