using Microsoft.AspNetCore.Http;

namespace ClaudeCodeAccountRotation.App.Security;

/// <summary>
/// Sends the page's security headers on every response. The dashboard is a
/// same-origin document: <c>index.html</c> loads <c>/app.css</c> and
/// <c>/app.js</c>, and the script polls this host. There is no inline script
/// and no inline style element. The usage bar sets its width through the
/// CSSOM, which <c>style-src 'self'</c> does not block, so the policy does not
/// need <c>unsafe-inline</c> and it has no <c>unsafe-eval</c>.
/// <para>
/// The ten-second poll reads local files only. It is not the usage-endpoint
/// polling the refresh contract forbids.
/// </para>
/// </summary>
internal sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    internal const string ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'";

    internal const string ContentTypeOptions = "nosniff";

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Response.Headers.ContentSecurityPolicy = ContentSecurityPolicy;
        context.Response.Headers.XContentTypeOptions = ContentTypeOptions;
        return next(context);
    }
}
