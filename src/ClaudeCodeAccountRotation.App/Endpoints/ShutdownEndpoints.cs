using ClaudeCodeAccountRotation.App.Security;
using ClaudeCodeAccountRotation.App.Switching;
using ClaudeCodeAccountRotation.Core.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ClaudeCodeAccountRotation.App.Endpoints;

/// <summary>
/// <c>POST /api/shutdown</c> stops this process after the response is written.
/// Both roles map it. The follower is started by the leader over <c>wsl.exe</c>
/// and has no other stop path.
/// <para>
/// A switch or import that is in flight, or any other credential change holding
/// the mutation gate, is refused with 409 and the process keeps running. The
/// journals are only read. Startup reconciliation finishes or unwinds an open
/// one; shutdown never rewrites it. A login does not hold the gate for the
/// session, and this route does not ask whether one is running.
/// </para>
/// </summary>
internal static class ShutdownEndpoints
{
    private const string SwitchOrImportInFlight = "a switch or import is in flight";
    private const string CredentialChangeInProgress = "another credential change is in progress";

    public static void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        RouteGroupBuilder mutations = routes.MapGroup("/api").AddEndpointFilter<SameOriginMutationFilter>();
        mutations.MapPost("/shutdown", ShutdownAsync);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The permit is held across the response: OnCompleted stops the host and then releases it, so a new mutation cannot acquire the gate first. A refusal disposes it in this handler.")]
    private static async Task<IResult> ShutdownAsync(
        HttpContext http,
        CredentialMutationGate gate,
        ClaudeCodeAccountRotationConfiguration configuration,
        IHostApplicationLifetime lifetime,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(lifetime);

        IDisposable permit;
        try
        {
            permit = await gate.AcquireAsync(TimeSpan.Zero, cancellationToken);
        }
        catch (TimeoutException)
        {
            return Results.Json(new { error = CredentialChangeInProgress }, statusCode: StatusCodes.Status409Conflict);
        }

        bool releaseInHandler = true;
        try
        {
            if (await JournalOpenAsync(configuration, http.RequestServices, cancellationToken))
            {
                return Results.Json(new { error = SwitchOrImportInFlight }, statusCode: StatusCodes.Status409Conflict);
            }

            // StopApplication inside the handler cancels the request before the
            // 200 is written. OnCompleted runs after that write. The permit stays
            // held until then, so a new mutation cannot take the gate first.
            http.Response.OnCompleted(() =>
            {
                lifetime.StopApplication();
                permit.Dispose();
                return Task.CompletedTask;
            });
            releaseInHandler = false;
            return Results.Ok(new { stopped = true });
        }
        finally
        {
            if (releaseInHandler)
            {
                permit.Dispose();
            }
        }
    }

    /// <summary>
    /// The journals this role registered. The follower has no switch journal,
    /// and the leader has no import journal; resolving the other would throw.
    /// </summary>
    private static async Task<bool> JournalOpenAsync(
        ClaudeCodeAccountRotationConfiguration configuration,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        if (configuration.Role == RotationRole.Follower)
        {
            return await services.GetRequiredService<ImportJournal>().ReadOpenAsync(cancellationToken) is not null;
        }

        if (await services.GetRequiredService<SwitchJournal>().ReadOpenAsync(cancellationToken) is not null)
        {
            return true;
        }

        return await services.GetRequiredService<WslSwitchJournal>().ReadOpenAsync(cancellationToken) is not null;
    }
}
