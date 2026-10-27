using ClaudeCodeAccountRotation.Core.Identity;

namespace ClaudeCodeAccountRotation.App.Quota;

/// <summary>
/// What a pass was asked to do: every account that has credentials and is not
/// paused, every such account that backs a CI role, a named set of them, or one named account. Flat rather than a hierarchy, the shape every
/// other result and input in this codebase takes, because the difference is one
/// filter over the same candidate set and everything downstream is identical.
/// <para>
/// A pass is the only thing that reaches the usage or token endpoint, and it
/// starts only through <see cref="QuotaRefreshWorker.TryStart"/>, called by the
/// refresh routes the page posts to on load and from its Refresh buttons, and by
/// the rate-limit stop hook's route, with <see cref="One"/> for the live account,
/// and with <see cref="CiRoleAccounts"/> by the fifteen-minute
/// <see cref="CiUsageSchedule"/> and the route a CI warning can call, and with
/// <see cref="Named"/> by <see cref="UsageResetSchedule"/> for the accounts whose window has reset.
/// </para>
/// </summary>
internal sealed record RefreshRequest(AccountEmail? Account, bool CiRoles = false, IReadOnlySet<AccountEmail>? Accounts = null)
{
    /// <summary>Every eligible account, in <c>RefreshOrder</c>'s order.</summary>
    public static RefreshRequest All { get; } = new(Account: null);

    /// <summary>Every eligible account that backs a CI role: the accounts CI spends while nobody watches.</summary>
    public static RefreshRequest CiRoleAccounts { get; } = new(Account: null, CiRoles: true);

    /// <summary>Whether this is the full pass rather than one account's, a named set's, or the CI-role accounts'.</summary>
    public bool IsAll => Account is null && !CiRoles && Accounts is null;

    /// <summary>One account, still through the whole per-account sequence: a stranded folder is restored, the budget still refuses, the lockouts still hold.</summary>
    public static RefreshRequest One(AccountEmail account) => new(account);

    /// <summary>Each eligible account of <paramref name="accounts"/>, in <c>RefreshOrder</c>'s order, as one pass.</summary>
    public static RefreshRequest Named(IEnumerable<AccountEmail> accounts) => new(Account: null, Accounts: accounts.ToHashSet());
}
