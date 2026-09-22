using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using ClaudeCodeAccountRotation.App.Hosting;
using ClaudeCodeAccountRotation.App.Switching;
using ClaudeCodeAccountRotation.Core.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace ClaudeCodeAccountRotation.App.Security;

/// <summary>
/// Requires this process's loopback token on every route except the liveness
/// probe and the two static assets the page loads before it has a token.
/// The document itself loads either way: a missing or wrong credential returns
/// the page with an empty token element, and a matching one embeds the token.
/// A query string is never a credential.
/// </summary>
internal sealed class LoopbackTokenMiddleware(RequestDelegate next)
{
    internal const string CookieName = "ccar-instance";

    private const string Placeholder = "<meta name=\"ccar-instance-token\" content=\"\">";

    private static readonly byte[] _unauthorizedBody = "{\"error\":\"unauthorized\"}"u8.ToArray();

    public async Task InvokeAsync(HttpContext context, InstanceLock instance, ClaudeCodeAccountRotationConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(configuration);

        if (IsOpen(context.Request))
        {
            await next(context);
            return;
        }

        Presented bearer = ReadBearer(context.Request);
        Presented cookie = ReadCookie(context.Request);
        bool bearerMatches = !bearer.IsPresent || instance.MatchesPresented(bearer.Value);
        bool cookieMatches = !cookie.IsPresent || instance.MatchesPresented(cookie.Value);
        bool authorized = (bearer.IsPresent || cookie.IsPresent) && bearerMatches && cookieMatches;

        if (configuration.Role != RotationRole.Follower && IsDocument(context.Request))
        {
            await WriteDocumentAsync(context, instance, authorized, bearer.IsPresent, cookie.IsPresent);
            return;
        }

        if (!authorized)
        {
            if (cookie.IsPresent)
            {
                ClearCookie(context.Response);
            }

            await WriteUnauthorizedAsync(context);
            return;
        }

        if (bearer.IsPresent)
        {
            SetCookie(context.Response, instance.Token);
        }

        await next(context);
    }

    private static bool IsOpen(HttpRequest request) =>
        HttpMethods.IsGet(request.Method)
        && request.Path.Value is "/healthz" or "/app.js" or "/app.css";

    private static bool IsDocument(HttpRequest request) =>
        HttpMethods.IsGet(request.Method) && request.Path.Value == "/";

    private static Presented ReadBearer(HttpRequest request)
    {
        if (!request.Headers.TryGetValue(HeaderNames.Authorization, out StringValues values) || values.Count == 0)
        {
            return Presented.Absent;
        }

        if (values.Count == 1
            && AuthenticationHeaderValue.TryParse(values.ToString(), out AuthenticationHeaderValue? header)
            && header.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase)
            && header.Parameter is { Length: > 0 } parameter)
        {
            return new Presented(true, parameter);
        }

        // Present, but not a single bearer value. Compared as a failed decode.
        return new Presented(true, string.Empty);
    }

    private static Presented ReadCookie(HttpRequest request) =>
        request.Cookies.TryGetValue(CookieName, out string? value)
            ? new Presented(true, value ?? string.Empty)
            : Presented.Absent;

    private static async Task WriteDocumentAsync(HttpContext context, InstanceLock instance, bool authorized, bool bearerPresented, bool cookiePresented)
    {
        bool embed = authorized;
        string? html = DocumentHtml(embed ? instance.Token : null);
        if (html is null)
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsync("the dashboard page is missing its instance token element");
            return;
        }

        if (embed && bearerPresented)
        {
            SetCookie(context.Response, instance.Token);
        }
        else if (!embed && cookiePresented)
        {
            ClearCookie(context.Response);
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(html));
    }

    /// <summary>
    /// The embedded page with the token element filled or left empty. Null when
    /// the placeholder is missing or repeated, which is a broken executable
    /// rather than a page with nowhere to put the token.
    /// </summary>
    private static string? DocumentHtml(string? token)
    {
        string html = EmbeddedPage.IndexHtml;
        int at = html.IndexOf(Placeholder, StringComparison.Ordinal);
        if (at < 0 || html.IndexOf(Placeholder, at + Placeholder.Length, StringComparison.Ordinal) >= 0)
        {
            return null;
        }

        if (token is null)
        {
            return html;
        }

        string element = "<meta name=\"ccar-instance-token\" content=\"" + HtmlEncoder.Default.Encode(token) + "\">";
        return html[..at] + element + html[(at + Placeholder.Length)..];
    }

    private static void SetCookie(HttpResponse response, string token) =>
        response.Cookies.Append(CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Strict,
            Path = "/",
            Secure = false,
        });

    private static void ClearCookie(HttpResponse response) =>
        response.Cookies.Delete(CookieName, new CookieOptions
        {
            HttpOnly = true,
            SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Strict,
            Path = "/",
            Secure = false,
        });

    private static Task WriteUnauthorizedAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Append(HeaderNames.WWWAuthenticate, "Bearer");
        context.Response.ContentType = "application/json";
        return context.Response.Body.WriteAsync(_unauthorizedBody).AsTask();
    }

    private readonly record struct Presented(bool IsPresent, string Value)
    {
        public static Presented Absent => new(false, string.Empty);
    }
}
