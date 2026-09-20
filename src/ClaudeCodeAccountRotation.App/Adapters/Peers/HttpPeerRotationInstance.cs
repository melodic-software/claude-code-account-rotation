using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Endpoints;
using ClaudeCodeAccountRotation.App.Security;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Peers;
using ClaudeCodeAccountRotation.Core.Switching;

namespace ClaudeCodeAccountRotation.App.Adapters.Peers;

/// <summary>
/// The other side over loopback HTTP: the five calls of
/// <see cref="IPeerRotationInstance"/> against the follower's routes.
/// <para>
/// Every mutating call carries <see cref="SameOriginMutationFilter.HeaderName"/>
/// and <b>no</b> <c>Origin</c> header, which is what the follower's filter
/// wants from a non-browser caller: the custom header is the thing a
/// cross-site form post cannot add, and an absent Origin is what a
/// process-to-process request honestly has.
/// </para>
/// <para>
/// Nothing here throws for an unreachable side. The distro being off is an
/// ordinary state, and every failure comes back as a reason the coordinator
/// turns into <see cref="SwitchRefusal.SideOffline"/> or a banner.
/// </para>
/// </summary>
internal sealed class HttpPeerRotationInstance : IPeerRotationInstance
{
    private readonly HttpClient _client;

    public HttpPeerRotationInstance(SideName side, HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        Side = side;
        _client = client;
        if (!_client.DefaultRequestHeaders.Contains(SameOriginMutationFilter.HeaderName))
        {
            _client.DefaultRequestHeaders.Add(SameOriginMutationFilter.HeaderName, "1");
        }
    }

    public SideName Side { get; }

    public Task<Result<PeerDashboard, string>> ReadDashboardAsync(CancellationToken cancellationToken) =>
        GetAsync<ImportEndpoints.FollowerDashboardView, PeerDashboard>(
            "/api/dashboard",
            view => new PeerDashboard(
                new SideName(view.Side),
                Email(view.LiveAccount),
                Fingerprint(view.LiveFingerprint),
                Step(view.ImportJournalStep),
                view.Version,
                view.LiveAccountBlock),
            cancellationToken);

    public Task<Result<ImportAnswer, string>> ImportAsync(ImportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return PostAsync<ImportEndpoints.ImportRequestBody, ImportEndpoints.ImportAnswerView, ImportAnswer>(
            "/api/import",
            new ImportEndpoints.ImportRequestBody(
                request.Email.Value,
                request.ClaimedPath,
                request.Fingerprint.Sha256Hex,
                request.Account,
                request.ExportPath),
            view => new ImportAnswer(
                Fingerprint(view.ExportedFingerprint),
                Email(view.Outgoing),
                view.AlreadyImported,
                Result(view.Result)),
            cancellationToken);
    }

    public Task<Result<ImportResult, string>> CommitImportAsync(AccountEmail email, CancellationToken cancellationToken) =>
        PostAsync<ImportEndpoints.ImportCommitBody, ImportEndpoints.ImportResultView, ImportResult>(
            "/api/import/commit",
            new ImportEndpoints.ImportCommitBody(email.Value),
            view => Result(view)!,
            cancellationToken);

    public Task<Result<Unit, string>> AbortImportAsync(AccountEmail email, CancellationToken cancellationToken) =>
        PostAsync<ImportEndpoints.ImportCommitBody, JsonObject, Unit>(
            "/api/import/abort",
            new ImportEndpoints.ImportCommitBody(email.Value),
            static _ => Unit.Value,
            cancellationToken);

    public Task<Result<ImportStatus, string>> ImportStatusAsync(AccountEmail email, CancellationToken cancellationToken) =>
        GetAsync<ImportEndpoints.ImportStatusView, ImportStatus>(
            "/api/import-status?email=" + Uri.EscapeDataString(email.Value),
            static view => new ImportStatus(view.Imported, Step(view.JournalStep), Fingerprint(view.LiveFingerprint), LiveAccount: null, view.Detail),
            cancellationToken);

    private async Task<Result<TOut, string>> GetAsync<TView, TOut>(string route, Func<TView, TOut> project, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await _client.GetAsync(new Uri(route, UriKind.Relative), cancellationToken);
            return await ProjectAsync(response, route, project, cancellationToken);
        }
        catch (Exception exception) when (Unreachable(exception))
        {
            return Result<TOut, string>.Failure(route + ": " + exception.Message);
        }
    }

    private async Task<Result<TOut, string>> PostAsync<TBody, TView, TOut>(
        string route,
        TBody body,
        Func<TView, TOut> project,
        CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await _client.PostAsJsonAsync(new Uri(route, UriKind.Relative), body, cancellationToken);
            return await ProjectAsync(response, route, project, cancellationToken);
        }
        catch (Exception exception) when (Unreachable(exception))
        {
            return Result<TOut, string>.Failure(route + ": " + exception.Message);
        }
    }

    /// <summary>
    /// A 409 carries the follower's own sentence, and that sentence is the
    /// evidence the coordinator's crash table reads ("not imported: …"). It is
    /// passed through unchanged rather than replaced with a status code.
    /// </summary>
    private static async Task<Result<TOut, string>> ProjectAsync<TView, TOut>(
        HttpResponseMessage response,
        string route,
        Func<TView, TOut> project,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            string? reason = null;
            try
            {
                reason = JsonNode.Parse(body) is JsonObject error ? error["error"]?.GetValue<string>() : null;
            }
            catch (JsonException)
            {
                // A body that is not the follower's own error shape; the status
                // line is then all there is to report.
            }

            return Result<TOut, string>.Failure(reason ?? (route + " answered " + (int)response.StatusCode));
        }

        TView? view = await response.Content.ReadFromJsonAsync<TView>(cancellationToken);
        return view is null
            ? Result<TOut, string>.Failure(route + " answered an empty body")
            : Result<TOut, string>.Success(project(view));
    }

    private static bool Unreachable(Exception exception) =>
        exception is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException or InvalidOperationException;

    private static ImportResult? Result(ImportEndpoints.ImportResultView? view) => view is null
        ? null
        : new ImportResult(Email(view.Outgoing), Fingerprint(view.OutgoingFingerprint), view.OutgoingAccount, view.AlreadyImported);

    private static AccountEmail? Email(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : AccountEmail.Parse(value).Match(static parsed => (AccountEmail?)parsed, static _ => null);

    private static RefreshTokenFingerprint? Fingerprint(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : new RefreshTokenFingerprint(value);

    private static ImportStep? Step(string? value) =>
        Enum.TryParse(value, ignoreCase: true, out ImportStep step) ? step : null;
}
