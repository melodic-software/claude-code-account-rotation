using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Quota;

namespace ClaudeCodeAccountRotation.App.Quota;

/// <summary>
/// Everything a refresh pass leaves behind, in memory: the newest snapshot per
/// account, how each account's last turn went, the two host lockouts, whether a
/// pass is running, what the last one came to, and the warnings a restore could
/// not clear. The dashboard reads it on every poll and the routes consult it
/// before starting anything, so the two together never see half a pass's work.
/// <para>
/// A restart forgets all of it by design (the parent plan's T4: the budget
/// window and the lockouts are tracked in memory). The operator restarts the
/// tool to install a build, not to dodge a lockout, and the endpoint enforces
/// its own limit regardless.
/// </para>
/// <para>
/// No member holds a token. The snapshots are percentages and reset times, the
/// outcomes are curated sentences, and the warnings name accounts.
/// </para>
/// </summary>
internal sealed class QuotaState
{
    // ponytail: one lock over five small dictionaries. Ten accounts read a few
    // times an hour; finer-grained locking would buy nothing measurable and the
    // page's read must see one consistent picture anyway.
    private readonly Lock _mutex = new();
    private readonly Dictionary<AccountEmail, UsageSnapshot> _latest = [];
    private readonly Dictionary<AccountEmail, RefreshOutcome> _outcomes = [];
    private readonly Dictionary<string, string> _recoveryWarnings =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private readonly Dictionary<string, DateTimeOffset> _loginRenewals =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private DateTimeOffset? _usageLockedUntil;
    private DateTimeOffset? _tokenLockedUntil;
    private bool _inProgress;
    private IReadOnlyDictionary<RefreshOutcomeKind, int>? _lastPassSummary;
    private TaskCompletionSource? _run;

    /// <summary>Whether a pass or a single-account read is in flight.</summary>
    public bool InProgress
    {
        get
        {
            lock (_mutex)
            {
                return _inProgress;
            }
        }
    }

    /// <summary>No usage read until this instant, from the usage host's own 429.</summary>
    public DateTimeOffset? UsageLockedUntil
    {
        get
        {
            lock (_mutex)
            {
                return _usageLockedUntil;
            }
        }

        set
        {
            lock (_mutex)
            {
                _usageLockedUntil = value;
            }
        }
    }

    /// <summary>No token refresh until this instant, from the token host's own 429. A separate host, so a separate lockout.</summary>
    public DateTimeOffset? TokenLockedUntil
    {
        get
        {
            lock (_mutex)
            {
                return _tokenLockedUntil;
            }
        }

        set
        {
            lock (_mutex)
            {
                _tokenLockedUntil = value;
            }
        }
    }

    /// <summary>How many accounts each outcome befell in the last completed pass, or null before the first one.</summary>
    public IReadOnlyDictionary<RefreshOutcomeKind, int>? LastPassSummary
    {
        get
        {
            lock (_mutex)
            {
                return _lastPassSummary;
            }
        }
    }

    /// <summary>
    /// The pass in flight, for a test or a route that must wait for it. Created
    /// by <see cref="TryBeginRun"/> under this lock rather than by the worker
    /// when it picks the request up, which is what closes the race between the
    /// route answering 202 and the worker starting: a caller that awaits this
    /// immediately after a successful start cannot observe the previous pass's
    /// completed task.
    /// </summary>
    public Task CurrentRun
    {
        get
        {
            lock (_mutex)
            {
                return _run?.Task ?? Task.CompletedTask;
            }
        }
    }

    /// <summary>Every recovery warning still standing, one sentence per folder, for the page's warning list.</summary>
    public IReadOnlyList<string> RecoveryWarnings
    {
        get
        {
            lock (_mutex)
            {
                return [.. _recoveryWarnings.Values];
            }
        }
    }

    /// <summary>
    /// The instant both hosts are clear again, or null when neither lockout is
    /// still standing at <paramref name="now"/>. One method rather than the same
    /// "later of the two" expression wherever it is asked: the page, the routes
    /// and the pass itself all have to agree on whether this tool is holding off,
    /// and three hand-rolled copies are three chances to drift apart.
    /// </summary>
    public DateTimeOffset? LockedUntil(DateTimeOffset now)
    {
        lock (_mutex)
        {
            DateTimeOffset? later = _usageLockedUntil > _tokenLockedUntil ? _usageLockedUntil : _tokenLockedUntil ?? _usageLockedUntil;
            return later > now ? later : null;
        }
    }

    /// <summary>Claims the right to run one pass, or refuses because one is already running.</summary>
    public bool TryBeginRun()
    {
        lock (_mutex)
        {
            if (_inProgress)
            {
                return false;
            }

            _inProgress = true;
            // Asynchronous continuations, or the waiting route's own work would run
            // on the worker's thread inside EndRun and delay the next pass.
            _run = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return true;
        }
    }

    /// <summary>Ends the pass and releases everyone waiting on <see cref="CurrentRun"/>. Idempotent, because the worker calls it in <c>finally</c>.</summary>
    public void EndRun()
    {
        TaskCompletionSource? finished;
        lock (_mutex)
        {
            _inProgress = false;
            finished = _run;
        }

        finished?.TrySetResult();
    }

    public UsageSnapshot? LatestFor(AccountEmail account)
    {
        lock (_mutex)
        {
            return _latest.GetValueOrDefault(account);
        }
    }

    public void RecordSnapshot(UsageSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_mutex)
        {
            _latest[snapshot.Account] = snapshot;
        }
    }

    public RefreshOutcome? OutcomeFor(AccountEmail account)
    {
        lock (_mutex)
        {
            return _outcomes.GetValueOrDefault(account);
        }
    }

    public void RecordOutcome(AccountEmail account, RefreshOutcome outcome)
    {
        lock (_mutex)
        {
            _outcomes[account] = outcome;
        }
    }

    public void RecordPassSummary(IReadOnlyDictionary<RefreshOutcomeKind, int> counts)
    {
        lock (_mutex)
        {
            _lastPassSummary = counts;
        }
    }

    /// <summary>
    /// Records that a folder's recovery file could not be applied. Keyed by
    /// folder rather than queued, so a restore that keeps failing at every start
    /// says the same thing once instead of growing the page's warning list
    /// without bound.
    /// </summary>
    public void WarnRecovery(string folder, string warning)
    {
        lock (_mutex)
        {
            _recoveryWarnings[folder] = warning;
        }
    }

    /// <summary>Drops a folder's warning once its recovery file is applied or moved aside.</summary>
    public void ClearRecoveryWarning(string folder)
    {
        lock (_mutex)
        {
            _recoveryWarnings.Remove(folder);
        }
    }

    /// <summary>
    /// When this folder's login was last renewed by a pass, or null when no pass
    /// has renewed it. The token response is allowed to omit the new login
    /// expiry, and the pair then keeps the expiry it already had, so the window
    /// that chose the account stays open and every later pass would choose it
    /// again. This is the only record that the request was already made.
    /// </summary>
    public DateTimeOffset? LoginRenewedAt(string folder)
    {
        lock (_mutex)
        {
            return _loginRenewals.TryGetValue(folder, out DateTimeOffset renewed) ? renewed : null;
        }
    }

    public void RecordLoginRenewal(string folder, DateTimeOffset renewedAt)
    {
        lock (_mutex)
        {
            _loginRenewals[folder] = renewedAt;
        }
    }
}
