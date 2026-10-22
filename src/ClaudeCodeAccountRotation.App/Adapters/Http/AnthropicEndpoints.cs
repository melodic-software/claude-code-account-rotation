using System.Globalization;
using System.Net;
using ClaudeCodeAccountRotation.Core.Quota;

namespace ClaudeCodeAccountRotation.App.Adapters.Http;

/// <summary>
/// What the outbound adapters share: the honest identity the tool presents,
/// the timeout, and, for the usage and token clients, the mapping from an HTTP
/// outcome to a <see cref="UsageReadFailure"/>. The tool never imitates the CLI's
/// User-Agent; it says what it is on every request it makes.
/// </summary>
internal static class AnthropicEndpoints
{
    public const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    public const string TokenUrl = "https://platform.claude.com/v1/oauth/token";
    public const string OAuthBeta = "oauth-2025-04-20";
    public const string AdminApiKeysUrl = "https://api.anthropic.com/v1/organizations/api_keys";

    /// <summary>The <c>anthropic-version</c> the Admin API listing is sent with, as its documented example sends it.</summary>
    public const string ApiVersion = "2023-06-01";

    /// <summary>
    /// Claude Code's public OAuth client id, as spike 03 sent it. The refresh
    /// grant is issued to that client, so the token endpoint accepts no other;
    /// this is the one request where the tool presents the CLI's client
    /// identity, and the User-Agent still says who is actually calling.
    /// </summary>
    public const string ClaudeCodeClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";

    public const string HomeUrl = "https://github.com/melodic-software/claude-code-account-rotation";

    /// <summary>The Claude Console page where an organization's API keys are created.</summary>
    public const string ConsoleKeysUrl = "https://platform.claude.com/settings/keys";

    /// <summary>The Claude Console page where an organization's Admin keys are created.</summary>
    public const string ConsoleAdminKeysUrl = "https://platform.claude.com/settings/admin-keys";

    /// <summary>
    /// The only Console pages the page may ask the tool to open, by the name
    /// the request carries. A name not in here opens nothing.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, Uri> ConsolePages = new Dictionary<string, Uri>(StringComparer.Ordinal)
    {
        ["keys"] = new(ConsoleKeysUrl),
        ["admin-keys"] = new(ConsoleAdminKeysUrl),
    };

    /// <summary>Timeout-first: no outbound call waits on the default 100 seconds.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// <c>&lt;product token&gt;/&lt;version&gt; (+&lt;home url&gt;)</c>. The build
    /// stamps an informational version with a commit suffix; the suffix is cut
    /// so the product version stays a version.
    /// </summary>
    public static string UserAgent(string productToken, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        int suffix = version.IndexOf('+', StringComparison.Ordinal);
        string product = suffix < 0 ? version : version[..suffix];
        return productToken + "/" + product + " (+" + HomeUrl + ")";
    }

    /// <summary>
    /// The status the endpoint answered with, as a typed failure. A 401 is the
    /// one the caller may act on (refresh a parked pair and retry once); a 429
    /// carries the endpoint's own <c>Retry-After</c> and is honored by waiting,
    /// never by retrying.
    /// </summary>
    public static UsageReadFailure Failure(HttpResponseMessage response, TimeProvider timeProvider) =>
        response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new UsageReadFailure(UsageReadFailureKind.Unauthorized, "the access token was rejected (401)"),
            HttpStatusCode.TooManyRequests => new UsageReadFailure(
                UsageReadFailureKind.RateLimited,
                "the endpoint is rate limiting this token (429)",
                RetryAfter(response, timeProvider)),
            _ => new UsageReadFailure(
                UsageReadFailureKind.Transport,
                "the endpoint answered " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)),
        };

    /// <summary>
    /// The longest wait a <c>Retry-After</c> can impose. Spike 02 measured the
    /// real lockout at 300 seconds; the header is a value the tool does not
    /// control, and one absurd number should not park an account for a year.
    /// ponytail: one hour covers every lockout observed so far. If the endpoint
    /// ever answers a 429 keyed to the five-hour or weekly window, raise this
    /// rather than let the tool retry into a wall it was told about.
    /// </summary>
    public static readonly TimeSpan MaximumRetryAfter = TimeSpan.FromHours(1);

    /// <summary>
    /// <c>Retry-After</c> as either a delay or an absolute date, both of which
    /// the header allows, clamped to <see cref="MaximumRetryAfter"/>; an absent
    /// or unreadable one leaves the wait unknown.
    /// </summary>
    private static TimeSpan? RetryAfter(HttpResponseMessage response, TimeProvider timeProvider)
    {
        System.Net.Http.Headers.RetryConditionHeaderValue? header = response.Headers.RetryAfter;
        if (header?.Delta is TimeSpan delta)
        {
            return Clamp(delta);
        }

        if (header?.Date is DateTimeOffset date)
        {
            return Clamp(date - timeProvider.GetUtcNow());
        }

        return null;
    }

    private static TimeSpan Clamp(TimeSpan wait) =>
        wait < TimeSpan.Zero ? TimeSpan.Zero : wait > MaximumRetryAfter ? MaximumRetryAfter : wait;
}
