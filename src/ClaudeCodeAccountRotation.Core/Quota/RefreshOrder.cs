using ClaudeCodeAccountRotation.Core.Identity;

namespace ClaudeCodeAccountRotation.Core.Quota;

/// <summary>
/// One account a pass may read, with the moment its numbers were last read from
/// the endpoint. <paramref name="LastReadAt"/> is null for an account never
/// read, and it advances only on a read that actually happened: a lockout, a
/// skip, or a budget refusal must leave it where it was, or the account that was
/// refused would drift to the back of the queue without ever having been read.
/// The tee does not advance it either, because a tee observation costs the
/// endpoint nothing and so says nothing about whose turn it is.
/// </summary>
public sealed record RefreshCandidate(AccountEmail Email, DateTimeOffset? LastReadAt);

/// <summary>
/// The order a refresh pass reads accounts in. Pure, so the rule is testable
/// without a pass around it.
/// <para>
/// Spike 02b never ran, so whether the endpoint's rate bucket is kept per token
/// or shared across the client is unknown. Under the shared hypothesis a pass
/// populates roughly the first eight cards and the rest wait for the next
/// window, which makes the order the whole of the fairness guarantee: an account
/// never read goes first, then the one read longest ago, so three passes reach
/// every account even when each pass ends early on a 429.
/// </para>
/// </summary>
public static class RefreshOrder
{
    /// <summary>
    /// Never read first, then oldest read first, ties broken by ordinal e-mail.
    /// The tie-break is not cosmetic: ten accounts read inside one pass share an
    /// instant often enough that an unspecified order would make two passes over
    /// the same state disagree, and a reproducible pass is what lets the operator
    /// read the coverage log.
    /// </summary>
    public static IReadOnlyList<RefreshCandidate> Order(IReadOnlyList<RefreshCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        // Comparer<DateTimeOffset?>.Default already sorts null ahead of every
        // value, which is exactly "never read first".
        return [.. candidates
            .OrderBy(candidate => candidate.LastReadAt)
            .ThenBy(candidate => candidate.Email.Value, StringComparer.Ordinal)];
    }
}
