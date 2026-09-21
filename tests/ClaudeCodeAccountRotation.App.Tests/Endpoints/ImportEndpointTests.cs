using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Switching;
using ClaudeCodeAccountRotation.App.Tests.Switching;
using ClaudeCodeAccountRotation.Core.Identity;
using Shouldly;

namespace ClaudeCodeAccountRotation.App.Tests.Endpoints;

/// <summary>
/// The follower's HTTP surface: the four import routes, the dashboard the
/// leader reads, and the routes a follower must not have at all.
/// </summary>
public sealed class ImportEndpointTests : IAsyncDisposable
{
    private const string OutgoingEmail = "a@example.com";
    private const string IncomingEmail = "b@example.com";

    private static readonly Uri _import = new("/api/import", UriKind.Relative);
    private static readonly Uri _commit = new("/api/import/commit", UriKind.Relative);
    private static readonly Uri _abort = new("/api/import/abort", UriKind.Relative);
    private static readonly Uri _status = new("/api/import-status", UriKind.Relative);
    private static readonly Uri _dashboard = new("/api/dashboard", UriKind.Relative);

    private readonly FollowerAppFactory _factory = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private object ImportBody(RefreshTokenFingerprint fingerprint) => new
    {
        email = IncomingEmail,
        claimedPath = _factory.Roots.ClaimedPath(IncomingEmail),
        fingerprint = fingerprint.Sha256Hex,
        account = FollowerRoots.AccountJson(IncomingEmail),
        exportPath = _factory.Roots.ExportPath(OutgoingEmail),
    };

    [Fact]
    public async Task AFollowerHasNoRosterRouteNoLoginRouteAndNoSwitchRoute()
    {
        using HttpClient client = _factory.CreateMutatingClient();

        using HttpResponseMessage accounts = await client.PostAsJsonAsync(new Uri("/api/accounts", UriKind.Relative), new { email = IncomingEmail }, Token);
        using HttpResponseMessage login = await client.PostAsJsonAsync(new Uri("/api/accounts/" + IncomingEmail + "/login", UriKind.Relative), new { }, Token);
        using HttpResponseMessage switching = await client.PostAsJsonAsync(new Uri("/api/accounts/" + IncomingEmail + "/switch", UriKind.Relative), new { }, Token);
        using HttpResponseMessage refresh = await client.PostAsJsonAsync(new Uri("/api/refresh", UriKind.Relative), new { }, Token);

        accounts.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        login.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        switching.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        refresh.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AFollowerAnswersItsOwnDashboardWithTheLiveAccountAndFingerprint()
    {
        RefreshTokenFingerprint fa = await _factory.Roots.WriteLiveAsync(OutgoingEmail, "refresh-a", Token);
        using HttpClient client = _factory.CreateClient();

        JsonObject dashboard = (await client.GetFromJsonAsync<JsonObject>(_dashboard, Token))!;

        dashboard["role"]!.GetValue<string>().ShouldBe("follower");
        dashboard["side"]!.GetValue<string>().ShouldBe("wsl");
        dashboard["liveAccount"]!.GetValue<string>().ShouldBe(OutgoingEmail);
        dashboard["liveFingerprint"]!.GetValue<string>().ShouldBe(fa.Sha256Hex);
    }

    [Fact]
    public async Task TheDashboardReconcilesBeforeItReadsEitherFile()
    {
        // The roots as a crash between F5 and F7 leaves them, written before this
        // host has served anything: the live file holds the incoming pair, the
        // state file still names the outgoing account, the journal still reads
        // Exported, and no staging file remains. This route is the leader's L1
        // view, so it must not answer with one account's name beside the other's
        // fingerprint.
        var fa = RefreshTokenFingerprint.FromRefreshToken("refresh-a");
        var fb = RefreshTokenFingerprint.FromRefreshToken("refresh-b");
        await CredentialFiles.WriteAsync(_factory.Roots.LiveDirectory, "refresh-b", Token);
        await _factory.Roots.WriteStateFileAsync(OutgoingEmail, Token);
        await _factory.Roots.WriteClaimedAsync(IncomingEmail, "refresh-b", Token);
        await File.WriteAllTextAsync(
            _factory.Roots.ExportPath(OutgoingEmail),
            CredentialFiles.Shape("refresh-a").ToJsonString(),
            Token);
        await _factory.Roots.Journal().WriteAsync(
            new ImportJournalEntry(
                new AccountEmail(IncomingEmail),
                fb,
                _factory.Roots.ClaimedPath(IncomingEmail),
                _factory.Roots.ExportPath(OutgoingEmail),
                new AccountEmail(OutgoingEmail),
                fa,
                FollowerRoots.AccountJson(IncomingEmail),
                FollowerRoots.AccountJson(OutgoingEmail),
                ImportStep.Exported,
                _factory.Roots.Clock.GetUtcNow()),
            Token);
        using HttpClient client = _factory.CreateClient();

        JsonObject dashboard = (await client.GetFromJsonAsync<JsonObject>(_dashboard, Token))!;

        dashboard["liveAccount"]!.GetValue<string>().ShouldBe(IncomingEmail);
        dashboard["liveFingerprint"]!.GetValue<string>().ShouldBe(fb.Sha256Hex);
    }

    [Fact]
    public async Task ImportStopsAtTheExportAndTheCommitFinishesIt()
    {
        RefreshTokenFingerprint fa = await _factory.Roots.WriteLiveAsync(OutgoingEmail, "refresh-a", Token);
        RefreshTokenFingerprint fb = await _factory.Roots.WriteClaimedAsync(IncomingEmail, "refresh-b", Token);
        using HttpClient client = _factory.CreateMutatingClient();

        using HttpResponseMessage exported = await client.PostAsJsonAsync(_import, ImportBody(fb), Token);

        exported.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonNode answer = (await exported.Content.ReadFromJsonAsync<JsonNode>(Token))!;
        answer["exportedFingerprint"]!.GetValue<string>().ShouldBe(fa.Sha256Hex);
        (await FollowerRoots.FingerprintOfAsync(_factory.Roots.LivePath, Token)).ShouldBe(fa);

        using HttpResponseMessage committed = await client.PostAsJsonAsync(_commit, new { email = IncomingEmail }, Token);

        committed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await FollowerRoots.FingerprintOfAsync(_factory.Roots.LivePath, Token)).ShouldBe(fb);
    }

    [Fact]
    public async Task ACommitWithNoExportedImportIsRefusedWithAConflict()
    {
        await _factory.Roots.WriteLiveAsync(OutgoingEmail, "refresh-a", Token);
        using HttpClient client = _factory.CreateMutatingClient();

        using HttpResponseMessage committed = await client.PostAsJsonAsync(_commit, new { email = IncomingEmail }, Token);

        committed.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task AnAbortLeavesTheLivePairAndTheClaimedFile()
    {
        RefreshTokenFingerprint fa = await _factory.Roots.WriteLiveAsync(OutgoingEmail, "refresh-a", Token);
        RefreshTokenFingerprint fb = await _factory.Roots.WriteClaimedAsync(IncomingEmail, "refresh-b", Token);
        using HttpClient client = _factory.CreateMutatingClient();
        using HttpResponseMessage exported = await client.PostAsJsonAsync(_import, ImportBody(fb), Token);
        exported.StatusCode.ShouldBe(HttpStatusCode.OK);

        using HttpResponseMessage aborted = await client.PostAsJsonAsync(_abort, new { email = IncomingEmail }, Token);

        aborted.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await FollowerRoots.FingerprintOfAsync(_factory.Roots.LivePath, Token)).ShouldBe(fa);
        (await FollowerRoots.FingerprintOfAsync(_factory.Roots.ClaimedPath(IncomingEmail), Token)).ShouldBe(fb);
    }

    [Fact]
    public async Task NoTokenLiteralReachesAResponseOrALogLine()
    {
        await _factory.Roots.WriteLiveAsync(OutgoingEmail, "refresh-a", Token);
        RefreshTokenFingerprint fb = await _factory.Roots.WriteClaimedAsync(IncomingEmail, "refresh-b", Token);
        using HttpClient client = _factory.CreateMutatingClient();

        using HttpResponseMessage exported = await client.PostAsJsonAsync(_import, ImportBody(fb), Token);
        string exportedBody = await exported.Content.ReadAsStringAsync(Token);
        using HttpResponseMessage committed = await client.PostAsJsonAsync(_commit, new { email = IncomingEmail }, Token);
        string committedBody = await committed.Content.ReadAsStringAsync(Token);

        foreach (string secret in new[] { "refresh-a", "refresh-b", "access-refresh-a", "access-refresh-b" })
        {
            exportedBody.ShouldNotContain(secret);
            committedBody.ShouldNotContain(secret);
            foreach (string line in _factory.Logs.Lines)
            {
                line.ShouldNotContain(secret);
            }
        }
    }

    [Fact]
    public async Task ImportStatusReportsTheJournalStepAndTheLiveFingerprint()
    {
        RefreshTokenFingerprint fa = await _factory.Roots.WriteLiveAsync(OutgoingEmail, "refresh-a", Token);
        RefreshTokenFingerprint fb = await _factory.Roots.WriteClaimedAsync(IncomingEmail, "refresh-b", Token);
        using HttpClient client = _factory.CreateMutatingClient();
        using HttpResponseMessage exported = await client.PostAsJsonAsync(_import, ImportBody(fb), Token);
        exported.StatusCode.ShouldBe(HttpStatusCode.OK);

        JsonObject reported = (await client.GetFromJsonAsync<JsonObject>(_status, Token))!;

        reported["journalStep"]!.GetValue<string>().ShouldBe("Exported");
        reported["liveFingerprint"]!.GetValue<string>().ShouldBe(fa.Sha256Hex);
        reported["imported"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public async Task AMutatingImportRequestWithoutTheHeaderIsRefused()
    {
        RefreshTokenFingerprint fb = await _factory.Roots.WriteClaimedAsync(IncomingEmail, "refresh-b", Token);
        using HttpClient bare = _factory.CreateClient();

        using HttpResponseMessage response = await bare.PostAsJsonAsync(_import, ImportBody(fb), Token);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
