using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace ClaudeCodeAccountRotation.App.Security;

/// <summary>
/// Every mutating route: a cross-site form post carries no custom header and a
/// cross-origin fetch carries a foreign Origin, so requiring the
/// <c>X-Claude-Code-Account-Rotation</c> header and an Origin that is this
/// instance's own (or absent) refuses both before the handler runs. No CORS is
/// configured anywhere.
/// <para>
/// The Origin is compared against the port this instance bound, never against
/// the request's own Host header. Deriving the expected origin from the request
/// would compare two values the same request supplies, so a rebound name
/// arriving in both would match itself and this half of the filter would
/// defend nothing. A launch with port 0 binds a port the operating system
/// chooses; that port replaces the configured one here after the socket is
/// taken, and the configured value stays in the file.
/// </para>
/// </summary>
internal sealed class SameOriginMutationFilter : IEndpointFilter
{
    public const string HeaderName = "X-Claude-Code-Account-Rotation";

    private readonly LoopbackOrigins _origins;

    public SameOriginMutationFilter(LoopbackOrigins origins)
    {
        ArgumentNullException.ThrowIfNull(origins);
        _origins = origins;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        HttpRequest request = context.HttpContext.Request;

        if (!request.Headers.ContainsKey(HeaderName))
        {
            return Results.Json(new { error = "mutations require the " + HeaderName + " header" }, statusCode: StatusCodes.Status403Forbidden);
        }

        if (request.Headers.TryGetValue("Origin", out StringValues origin)
            && origin.Count > 0
            && !_origins.Allows(origin.ToString()))
        {
            return Results.Json(new { error = "cross-origin mutations are refused" }, statusCode: StatusCodes.Status403Forbidden);
        }

        return await next(context);
    }
}

/// <summary>
/// The three spellings of this instance's loopback origin. The reference is
/// replaced, not mutated, when the bound port is known.
/// </summary>
internal sealed class LoopbackOrigins
{
    private string[] _ownOrigins;

    public LoopbackOrigins(int port)
    {
        _ownOrigins = ForPort(port);
    }

    /// <summary>The port Kestrel actually took. Port 0 learns it only after bind.</summary>
    public void UseBoundPort(int port) => Volatile.Write(ref _ownOrigins, ForPort(port));

    public bool Allows(string origin) =>
        Volatile.Read(ref _ownOrigins).Contains(origin, StringComparer.OrdinalIgnoreCase);

    private static string[] ForPort(int port)
    {
        // Kestrel binds loopback only, and a browser writes the name the operator
        // typed, so all three spellings of that one address are this instance's own.
        string suffix = ":" + port.ToString(CultureInfo.InvariantCulture);
        return ["http://localhost" + suffix, "http://127.0.0.1" + suffix, "http://[::1]" + suffix];
    }
}
