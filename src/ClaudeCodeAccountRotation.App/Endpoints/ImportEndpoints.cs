using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.App.Security;
using ClaudeCodeAccountRotation.App.Switching;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Peers;
using ClaudeCodeAccountRotation.Core.Quota;
using ClaudeCodeAccountRotation.Core.Switching;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ClaudeCodeAccountRotation.App.Endpoints;

/// <summary>
/// The follower's only mutating surface, and the two-call shape of the export
/// gate: <c>POST /api/import</c> runs F1 to F4 and stops, and
/// <c>POST /api/import/commit</c> is the leader saying it has read the export
/// natively on the store's own volume. <c>POST /api/import/abort</c> unwinds,
/// and <c>GET /api/import-status</c> answers the leader's reconciliation.
/// <para>
/// No token appears in any request, response, or log line: what crosses is an
/// account email, a path on the store's volume, and a SHA-256 fingerprint.
/// </para>
/// </summary>
internal static class ImportEndpoints
{
    public static void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        RouteGroupBuilder mutations = routes.MapGroup("/api").AddEndpointFilter<SameOriginMutationFilter>();

        mutations.MapPost("/import", static async (
            ImportRequestBody body,
            FollowerImport import,
            CancellationToken cancellationToken) =>
        {
            Result<ImportRequest, string> request = Parse(body);
            if (request.IsFailure)
            {
                return Results.BadRequest(new { error = request.Error });
            }

            Result<ImportAnswer, string> answer = await import.ImportAsync(request.Value, cancellationToken);
            return answer.Match(
                static done => Results.Ok(new ImportAnswerView(
                    done.ExportedFingerprint?.Sha256Hex,
                    done.Outgoing?.Value,
                    done.AlreadyImported,
                    View(done.Result))),
                static reason => Results.Json(new { error = reason }, statusCode: StatusCodes.Status409Conflict));
        });

        mutations.MapPost("/import/commit", static async (
            ImportCommitBody body,
            FollowerImport import,
            CancellationToken cancellationToken) =>
        {
            Result<AccountEmail, string> email = AccountEmail.Parse(body?.Email ?? string.Empty);
            if (email.IsFailure)
            {
                return Results.BadRequest(new { error = email.Error });
            }

            Result<ImportResult, string> result = await import.CommitAsync(email.Value, cancellationToken);
            return result.Match(
                static done => Results.Ok(View(done)),
                static reason => Results.Json(new { error = reason }, statusCode: StatusCodes.Status409Conflict));
        });

        mutations.MapPost("/import/abort", static async (
            ImportCommitBody body,
            FollowerImport import,
            CancellationToken cancellationToken) =>
        {
            Result<AccountEmail, string> email = AccountEmail.Parse(body?.Email ?? string.Empty);
            if (email.IsFailure)
            {
                return Results.BadRequest(new { error = email.Error });
            }

            Result<Unit, string> aborted = await import.AbortAsync(email.Value, cancellationToken);
            return aborted.Match(
                static _ => Results.Ok(new { aborted = true }),
                static reason => Results.Json(new { error = reason }, statusCode: StatusCodes.Status409Conflict));
        });

        routes.MapGet("/api/import-status", static async (string? email, FollowerImport import, CancellationToken cancellationToken) =>
        {
            AccountEmail? about = string.IsNullOrWhiteSpace(email) ? null : AccountEmail.Parse(email).Match(static parsed => (AccountEmail?)parsed, static _ => null);
            ImportStatus status = await import.StatusAsync(cancellationToken, about);
            return Results.Ok(new ImportStatusView(
                status.Imported,
                status.JournalStep?.ToString(),
                status.LiveFingerprint?.Sha256Hex,
                status.LiveAccount?.Raw,
                status.Detail,
                status.LoginExpiresAt));
        });

        // The follower's dashboard is what the leader's L1 reads to learn that
        // this side is online and which account it holds. It is not the Windows
        // page's dashboard and carries none of its roster, quota or profile
        // facts, because a follower has no roster to report.
        routes.MapGet("/api/dashboard", static async (
            FollowerImport import,
            RateLimitGuardTeeFileReader tee,
            CancellationToken cancellationToken) =>
        {
            // One snapshot from the status read, which runs reconciliation first
            // and then reads the live pair and the state file under the commit's
            // own lock. Reading either file here, outside it, could answer the
            // leader's L1 with the account a crash between F5 and F7 left behind,
            // or a commit in flight had not yet patched, beside the fingerprint of
            // the pair that replaced it.
            ImportStatus status = await import.StatusAsync(cancellationToken);
            // The tee beside it: the leader reads no usage for a pair this side
            // holds, so these are the only figures its card can carry. A session
            // that has written nothing, or nothing it could attribute, is an
            // absent block rather than an empty one.
            StatuslineSnapshot? observed = await tee.ReadAsync(cancellationToken);
            return Results.Ok(new FollowerDashboardView(
                "follower",
                SideName.Wsl.Value,
                status.LiveAccount?.Email?.Value,
                status.LiveFingerprint?.Sha256Hex,
                status.JournalStep?.ToString(),
                Hosting.AppComposition.Version,
                status.LiveAccount?.Raw,
                observed is null ? null : new TeeView(
                    observed.CapturedAt,
                    observed.Account?.Value,
                    observed.FiveHourPercent,
                    observed.FiveHourResetsAt,
                    observed.SevenDayPercent,
                    observed.SevenDayResetsAt),
                // Read in the status snapshot above, under the commit's own
                // lock: one family per account, so the card for an account this
                // side holds has no local file to read its expiry from.
                status.LoginExpiresAt));
        });
    }

    private static ImportResultView? View(ImportResult? result) => result is null
        ? null
        : new ImportResultView(
            result.Outgoing?.Value,
            result.OutgoingFingerprint?.Sha256Hex,
            result.OutgoingAccount,
            result.AlreadyImported);

    private static Result<ImportRequest, string> Parse(ImportRequestBody? body)
    {
        if (body is null)
        {
            return Result<ImportRequest, string>.Failure("an import request is required");
        }

        Result<AccountEmail, string> email = AccountEmail.Parse(body.Email ?? string.Empty);
        if (email.IsFailure)
        {
            return Result<ImportRequest, string>.Failure(email.Error);
        }

        if (string.IsNullOrWhiteSpace(body.ExportPath))
        {
            return Result<ImportRequest, string>.Failure("a hand-off request needs an exportPath");
        }

        if (string.IsNullOrWhiteSpace(body.Fingerprint))
        {
            return Result<ImportRequest, string>.Failure(
                "a hand-off request needs a fingerprint: the incoming pair's for an import, and for a release the pair this side is expected to be live on");
        }

        // An absent claimedPath is a release, and it is the only thing that
        // says so. An account block with it would be the caller asking for two
        // different things at once, since a release installs no identity.
        bool release = string.IsNullOrWhiteSpace(body.ClaimedPath);
        if (release && body.Account is not null)
        {
            return Result<ImportRequest, string>.Failure("a release names no claimedPath and carries no account block; nothing arrives on this side");
        }

        return Result<ImportRequest, string>.Success(new ImportRequest(
            email.Value,
            release ? null : body.ClaimedPath,
            new RefreshTokenFingerprint(body.Fingerprint),
            release ? null : body.Account ?? [],
            body.ExportPath));
    }

    internal sealed record ImportRequestBody(string? Email, string? ClaimedPath, string? Fingerprint, JsonObject? Account, string? ExportPath);

    internal sealed record ImportCommitBody(string? Email);

    internal sealed record ImportAnswerView(string? ExportedFingerprint, string? Outgoing, bool AlreadyImported, ImportResultView? Result);

    internal sealed record ImportResultView(string? Outgoing, string? OutgoingFingerprint, JsonObject? OutgoingAccount, bool AlreadyImported);

    internal sealed record ImportStatusView(
        bool Imported,
        string? JournalStep,
        string? LiveFingerprint,
        JsonObject? LiveAccountBlock,
        string Detail,
        DateTimeOffset? LoginExpiresAt = null);

    internal sealed record FollowerDashboardView(
        string Role,
        string Side,
        string? LiveAccount,
        string? LiveFingerprint,
        string? ImportJournalStep,
        string? Version,
        JsonObject? LiveAccountBlock,
        TeeView? Tee = null,
        DateTimeOffset? LoginExpiresAt = null);

    /// <summary>
    /// This side's rate-limit-guard observation in plain wire types: the account
    /// is the e-mail the tee named, which the other side parses again rather
    /// than trusting, and the two windows are the ones the tee carries.
    /// </summary>
    internal sealed record TeeView(
        DateTimeOffset CapturedAt,
        string? Account,
        double? FiveHourPercent,
        DateTimeOffset? FiveHourResetsAt,
        double? SevenDayPercent,
        DateTimeOffset? SevenDayResetsAt);
}
