using System.Diagnostics.CodeAnalysis;
using System.Net.Http;

namespace ClaudeCodeAccountRotation.App.Adapters.Http;

/// <summary>
/// The socket under both outbound clients. Each call returns a new handler, so
/// the two named clients never share a pool.
/// </summary>
/// <remarks>
/// Redirects stay off. A 307 or 308 would resend the request, and the token
/// client's body is the refresh token. The factory's own primary handler copies
/// <c>HttpClientFactoryOptions.HandlerLifetime</c> (two minutes by default) onto
/// <c>PooledConnectionLifetime</c> so a held client still respects DNS changes.
/// Replacing that handler drops the lifetime, so this one sets the same value.
/// See Microsoft Learn, IHttpClientFactory, HTTP handler lifetime:
/// https://learn.microsoft.com/dotnet/core/extensions/httpclient-factory#http-handler-lifetime
/// </remarks>
internal static class OutboundPrimaryHandler
{
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The HTTP client factory owns the handler and disposes it when the handler lifetime ends.")]
    public static SocketsHttpHandler Create() => new()
    {
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    };
}
