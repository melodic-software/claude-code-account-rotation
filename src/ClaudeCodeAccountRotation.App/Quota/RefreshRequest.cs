using ClaudeCodeAccountRotation.Core.Identity;

namespace ClaudeCodeAccountRotation.App.Quota;

/// <summary>
/// What a pass was asked to do: every account that has credentials and is not
/// paused, or one named account. Flat rather than a hierarchy, the shape every
/// other result and input in this codebase takes, because the difference is one
/// filter over the same candidate set and everything downstream is identical.
/// <para>
/// A pass is the only thing that reaches the usage or token endpoint, and it
/// starts only through <see cref="QuotaRefreshWorker.TryStart"/>, called by the
/// refresh routes the page posts to on load and from its Refresh buttons, and by
/// the rate-limit stop hook's route, with <see cref="One"/> for the live account.
/// Nothing starts a pass on a timer.
/// </para>
/// </summary>
internal sealed record RefreshRequest(AccountEmail? Account)
{
    /// <summary>Every eligible account, in <c>RefreshOrder</c>'s order.</summary>
    public static RefreshRequest All { get; } = new(Account: null);

    /// <summary>One account, still through the whole per-account sequence: a stranded folder is restored, the budget still refuses, the lockouts still hold.</summary>
    public static RefreshRequest One(AccountEmail account) => new(account);
}
