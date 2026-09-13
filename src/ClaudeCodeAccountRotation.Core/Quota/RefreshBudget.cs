using ClaudeCodeAccountRotation.Core.Identity;

namespace ClaudeCodeAccountRotation.Core.Quota;

/// <summary>
/// The self-imposed ceiling on usage reads, per account. Spike 02 measured the
/// endpoint's own bucket at about eight reads per rolling five minutes for one
/// client identity, then a hard 300-second lockout; this budget sits under that
/// with a sliding window, a minimum gap between reads, and a lockout honored
/// from the endpoint's own <c>Retry-After</c>. Every clock reading comes from
/// the injected <see cref="TimeProvider"/>.
/// </summary>
public sealed class RefreshBudget(
    TimeProvider timeProvider,
    int maxReadsPerWindow = 6,
    TimeSpan? window = null,
    TimeSpan? minimumGapSinceLastRead = null)
{
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly int _maxReadsPerWindow = maxReadsPerWindow;
    private readonly TimeSpan _window = window ?? TimeSpan.FromMinutes(5);
    private readonly TimeSpan _minimumGap = minimumGapSinceLastRead ?? TimeSpan.FromSeconds(60);

    // ponytail: one dictionary under a lock. Refresh all iterates ten accounts a
    // few times an hour; per-account locks would buy nothing measurable.
    private readonly Dictionary<AccountEmail, AccountBudget> _accounts = [];
    private readonly Lock _mutex = new();

    /// <summary>
    /// Takes one read out of <paramref name="account"/>'s budget, or refuses.
    /// The caller settles the reservation afterwards: nothing more for a read
    /// that happened, <see cref="RecordUnauthorized"/> for a 401, and
    /// <see cref="RecordLockout"/> for a 429.
    /// </summary>
    public bool TryReserve(AccountEmail account)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        lock (_mutex)
        {
            AccountBudget budget = Budget(account);
            budget.Reads.RemoveAll(read => now - read >= _window);
            budget.Refunded.RemoveAll(read => now - read >= _window);
            if (budget.LockedOutUntil > now
                || budget.Reads.Count + budget.Refunded.Count >= _maxReadsPerWindow
                || (budget.Reads.Count > 0 && now - budget.Reads[^1] < _minimumGap))
            {
                return false;
            }

            budget.Reads.Add(now);
            return true;
        }
    }

    /// <summary>
    /// Gives back the reservation a 401 consumed nothing of. The endpoint
    /// rejected the token rather than serving the read, so it does not start the
    /// gap clock and the single retry after a credential refresh rides the
    /// original reservation. It is not free, though: the refund is remembered
    /// for the rest of the window, so a token the endpoint keeps rejecting still
    /// runs the account out of reads instead of looping against the endpoint
    /// forever.
    /// </summary>
    public void RecordUnauthorized(AccountEmail account)
    {
        lock (_mutex)
        {
            if (_accounts.TryGetValue(account, out AccountBudget? budget) && budget.Reads.Count > 0)
            {
                budget.Refunded.Add(budget.Reads[^1]);
                budget.Reads.RemoveAt(budget.Reads.Count - 1);
            }
        }
    }

    /// <summary>Honors a 429: no read for this account until <paramref name="retryAfter"/> has passed.</summary>
    public void RecordLockout(AccountEmail account, TimeSpan retryAfter)
    {
        lock (_mutex)
        {
            Budget(account).LockedOutUntil = _timeProvider.GetUtcNow() + retryAfter;
        }
    }

    /// <summary>How long this account stays locked out, or null when it is not.</summary>
    public TimeSpan? LockedOutFor(AccountEmail account)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        lock (_mutex)
        {
            return _accounts.TryGetValue(account, out AccountBudget? budget) && budget.LockedOutUntil > now
                ? budget.LockedOutUntil.Value - now
                : null;
        }
    }

    /// <summary>
    /// How long until the minimum gap since this account's last successful read
    /// has passed, or null when no gap stands in the way: the account has never
    /// been read, or enough time has gone by.
    /// <para>
    /// This is what a budget refusal tells the operator ("read N s ago") instead
    /// of failing silently, and it reads rather than reserves, so asking costs
    /// the account nothing and no entry is created for an account that has never
    /// been read. It measures from the reads only, never the refunded ones: a 401
    /// did not start the gap clock, exactly as <see cref="RecordUnauthorized"/>
    /// arranges, so the retry after a credential refresh must not be told to wait.
    /// </para>
    /// </summary>
    public TimeSpan? GapRemaining(AccountEmail account)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        lock (_mutex)
        {
            if (!_accounts.TryGetValue(account, out AccountBudget? budget) || budget.Reads.Count == 0)
            {
                return null;
            }

            TimeSpan remaining = _minimumGap - (now - budget.Reads[^1]);
            return remaining > TimeSpan.Zero ? remaining : null;
        }
    }

    private AccountBudget Budget(AccountEmail account)
    {
        if (!_accounts.TryGetValue(account, out AccountBudget? budget))
        {
            budget = new AccountBudget();
            _accounts[account] = budget;
        }

        return budget;
    }

    private sealed class AccountBudget
    {
        public List<DateTimeOffset> Reads { get; } = [];

        /// <summary>Reads a 401 gave back: they count toward the window, not the gap.</summary>
        public List<DateTimeOffset> Refunded { get; } = [];

        public DateTimeOffset? LockedOutUntil { get; set; }
    }
}
