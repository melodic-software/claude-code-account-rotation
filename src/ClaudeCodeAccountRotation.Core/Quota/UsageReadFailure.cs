namespace ClaudeCodeAccountRotation.Core.Quota;

/// <summary>Why an outbound call did not produce an answer.</summary>
public enum UsageReadFailureKind
{
    /// <summary>
    /// The network, DNS, TLS, the 20-second timeout, or any status other than
    /// 401 and 429 (and, from the token refresh, a 400 that is not
    /// <c>invalid_grant</c>): from the caller's side an
    /// unreachable endpoint and a 503 are the same thing, something to report on
    /// the card and try again later.
    /// </summary>
    Transport,

    /// <summary>401: the access token expired. A parked pair may be refreshed once and retried.</summary>
    Unauthorized,

    /// <summary>429: honored by waiting out <see cref="UsageReadFailure.RetryAfter"/>, never by retrying.</summary>
    RateLimited,

    /// <summary>A 2xx whose body did not parse, or parsed without what it must carry.</summary>
    MalformedBody,

    /// <summary>
    /// The token endpoint refused the refresh token itself: a 400 whose OAuth
    /// error is <c>invalid_grant</c>, or a 401, to the refresh grant. The login behind the pair
    /// is dead, and no retry with the same refresh token can revive it; only a
    /// new login can. Only the token refresh reports it.
    /// </summary>
    LoginRejected,
}

/// <summary>
/// The typed failure of either outbound call, the usage read and the token
/// refresh. Expected failures are results, not exceptions.
/// </summary>
public sealed record UsageReadFailure(UsageReadFailureKind Kind, string Detail, TimeSpan? RetryAfter = null);
