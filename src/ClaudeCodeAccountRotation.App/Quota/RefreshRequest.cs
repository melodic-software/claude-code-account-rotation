using ClaudeCodeAccountRotation.Core.Identity;

namespace ClaudeCodeAccountRotation.App.Quota;

/// <summary>
/// What a pass was asked to do: every account that has credentials (a paused
/// one only to renew a login about to lapse), or one named account. Flat rather
/// than a hierarchy, the shape every other result and input in this codebase
/// takes, because the difference is one filter over the same candidate set and
/// everything downstream is identical.
/// </summary>
internal sealed record RefreshRequest(AccountEmail? Account)
{
    /// <summary>Every eligible account: the paused logins due for renewal first, then the rest in <c>RefreshOrder</c>'s order.</summary>
    public static RefreshRequest All { get; } = new(Account: null);

    /// <summary>One account, still through the whole per-account sequence: a stranded folder is restored, the budget still refuses, the lockouts still hold.</summary>
    public static RefreshRequest One(AccountEmail account) => new(account);
}
