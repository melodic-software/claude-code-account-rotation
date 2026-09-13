using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.App.Switching;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Accounts;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Ports;
using ClaudeCodeAccountRotation.Core.Quota;
using Microsoft.Extensions.Logging;

namespace ClaudeCodeAccountRotation.App.Quota;

/// <summary>
/// One refresh pass: every account that has credentials and is not paused, in
/// the order that reaches the least recently read first, one usage read each. A
/// paused account is read by no pass; it takes a turn only on a full pass, only
/// to have its login renewed when that login is about to lapse.
/// <para>
/// A parked account's access token is expired on nearly every read, because
/// nothing has used that account since it was parked, so the token refresh and
/// its compare-and-swap write-back are the normal path through here rather than
/// an exception. The token endpoint kills the old refresh token the moment it
/// answers 200, which is what sets the shape of everything below: the POST and
/// the write-back are one unit under the mutation gate and under
/// <see cref="CancellationToken.None"/>, bounded by the adapter's own timeout
/// and the write retry budget and never by a request's token or the host's
/// shutdown; a pair that changed underneath the gate is refused before any POST;
/// and a write-back that cannot land parks the rotated pair in the recovery
/// directory rather than dropping it.
/// </para>
/// <para>
/// Nothing here waits on a timer. The one-second spacing between reads goes
/// through an injected delay so a test can assert it without sleeping, and the
/// budget refuses rather than waiting.
/// </para>
/// </summary>
internal sealed partial class QuotaRefresh
{
    /// <summary>
    /// How long a gated credential unit waits for the mutation gate. Not zero,
    /// which is what production hands every other caller: the dashboard poll's
    /// identity repair takes the gate with a zero wait every ten seconds, so a
    /// zero wait here would skip accounts at random depending on which poll it
    /// collided with. Two seconds outlasts a repair and still refuses a real
    /// switch quickly.
    /// </summary>
    private static readonly TimeSpan _defaultGateWait = TimeSpan.FromSeconds(2);

    /// <summary>
    /// What a 429 costs when the host declines to say. Spike 02 measured the
    /// real lockout at 300 seconds, and an absent or zero <c>Retry-After</c>
    /// must not be read as "come straight back".
    /// </summary>
    private static readonly TimeSpan _lockoutFloor = TimeSpan.FromSeconds(300);

    /// <summary>One second between reads, so a pass does not arrive at the endpoint as a burst.</summary>
    private static readonly TimeSpan _spacing = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How close a paused account's login has to be to expiring before a pass
    /// spends a token request on renewing it. A login lasts twenty-eight days, so
    /// a week is wide enough that any pass inside it catches the account and
    /// narrow enough that a paused account is otherwise left entirely alone.
    /// </summary>
    private static readonly TimeSpan _loginRenewalWindow = TimeSpan.FromDays(7);

    /// <summary>
    /// How long a renewed login is left alone before a pass will spend another
    /// token request on it. The token response is allowed to omit the new login
    /// expiry, and the pair then keeps the expiry it had, so the window above
    /// stays open and every later pass would post again for the same account —
    /// every ten seconds if the operator leaves Refresh all under their finger.
    /// A day is far inside the week the window covers, so a renewal that really
    /// did not take still gets six more chances before the login lapses.
    /// </summary>
    private static readonly TimeSpan _loginRenewalInterval = TimeSpan.FromDays(1);

    /// <summary>
    /// Three write-back attempts and the wait after each, jittered. The wait
    /// after the last attempt is counted too: 1.75 s of pacing is what the host's
    /// shutdown budget is computed from, so the drain covers the unit's worst
    /// case whether or not the final wait buys another try.
    /// </summary>
    private static readonly TimeSpan[] _writeBackBackoff =
        [TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1)];

    private readonly ICredentialPairStore _pairs;
    private readonly ProfileFolderStore _profiles;
    private readonly RosterFile _rosterFile;
    private readonly ClaudeStateFile _stateFile;
    private readonly LiveDirectorySwitch _executor;
    private readonly Func<IUsageEndpointClient> _usage;
    private readonly Func<ITokenRefreshClient> _tokens;
    private readonly RefreshBudget _budget;
    private readonly QuotaState _state;
    private readonly UsageSnapshotCache _cache;
    private readonly CredentialMutationGate _gate;
    private readonly ILoginSessionRunner _logins;
    private readonly RecoveryFiles _recovery;
    private readonly TimeProvider _timeProvider;
    private readonly Func<TimeSpan, CancellationToken, Task> _pace;
    private readonly ILogger<QuotaRefresh> _logger;
    private readonly TimeSpan _gateWait;

    public QuotaRefresh(
        ICredentialPairStore pairs,
        ProfileFolderStore profiles,
        RosterFile rosterFile,
        ClaudeStateFile stateFile,
        LiveDirectorySwitch executor,
        Func<IUsageEndpointClient> usage,
        Func<ITokenRefreshClient> tokens,
        RefreshBudget budget,
        QuotaState state,
        UsageSnapshotCache cache,
        CredentialMutationGate gate,
        ILoginSessionRunner logins,
        RecoveryFiles recovery,
        TimeProvider timeProvider,
        Func<TimeSpan, CancellationToken, Task> pace,
        ILogger<QuotaRefresh> logger,
        TimeSpan? gateWait = null)
    {
        _pairs = pairs;
        _profiles = profiles;
        _rosterFile = rosterFile;
        _stateFile = stateFile;
        _executor = executor;
        _usage = usage;
        _tokens = tokens;
        _budget = budget;
        _state = state;
        _cache = cache;
        _gate = gate;
        _logins = logins;
        _recovery = recovery;
        _timeProvider = timeProvider;
        _pace = pace;
        _logger = logger;
        // Defaulted rather than registered, so the composition root says nothing
        // about it and a test can hold the gate against a pass without paying the
        // production wait twice over.
        _gateWait = gateWait ?? _defaultGateWait;
    }

    /// <summary>
    /// Runs one pass to completion and records what each account came to. Never
    /// throws: the worker that drives it must survive any single account's bad
    /// day, so a failure inside one turn becomes that account's outcome.
    /// <paramref name="stopping"/> is consulted only between accounts and by the
    /// usage read, never by the gated refresh unit.
    /// </summary>
    public async Task RunAsync(RefreshRequest request, CancellationToken stopping)
    {
        ArgumentNullException.ThrowIfNull(request);
        Dictionary<RefreshOutcomeKind, int> summary = [];
        try
        {
            IReadOnlyList<Candidate> candidates = await CandidatesAsync(request, summary, stopping);
            bool ended = false;
            bool sentRead = false;
            foreach (Candidate candidate in candidates)
            {
                if (stopping.IsCancellationRequested)
                {
                    break;
                }

                Turn turn = ended ? new Turn(LockedOut()) : await TurnAsync(candidate, sentRead, stopping);
                Record(candidate.Email, turn.Outcome, summary);
                ended |= turn.EndsPass;
                sentRead |= turn.SentRead;
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // The host is shutting down mid-pace or mid-read. Ordinary, and not
            // something to log at Error: the accounts already settled keep their
            // outcomes and the rest wait for the next pass.
        }
#pragma warning disable CA1031 // Do not catch general exception types
        catch (Exception exception)
#pragma warning restore CA1031
        {
            // Building the candidate set reads four files written by other
            // processes. Anything unforeseen there ends the pass with what it has
            // rather than taking the hosted worker down.
            LogPassFailed(exception.GetType().Name);
            LogPassFailedDetail(exception);
        }
        finally
        {
            // Only a full pass leaves a summary. A single card's refresh counts
            // one account, and a header reading "last pass: 1 read" after it would
            // say the other nine were never tried.
            if (request.Account is null)
            {
                _state.RecordPassSummary(summary);
            }
        }
    }

    /// <summary>
    /// Who this pass reads, in order, and the outcome of everyone it decided not
    /// to read before it started. The live account is whatever the state file
    /// says after a stale-identity repair; a repair that could not run leaves the
    /// live identity unverified, and reading a pair whose owner is in doubt would
    /// put another account's numbers on the card.
    /// </summary>
    private async Task<IReadOnlyList<Candidate>> CandidatesAsync(
        RefreshRequest request,
        Dictionary<RefreshOutcomeKind, int> summary,
        CancellationToken stopping)
    {
        IdentityRepair repair = await _executor.RepairStaleIdentityAsync(stopping);
        AccountEmail? liveEmail = (await _stateFile.ReadAccountBlockAsync(stopping))?.Email;
        IReadOnlyList<ParkedProfile> parked = await _profiles.ListAsync(stopping);
        Roster roster = await _rosterFile.ReadAsync(stopping);
        bool Wanted(AccountEmail email) => request.Account is null || request.Account == email;

        List<Candidate> candidates = [];
        List<Candidate> renewals = [];
        if (liveEmail is AccountEmail live && Wanted(live))
        {
            if (repair is IdentityRepair.Busy or IdentityRepair.NoProfileBlock)
            {
                Record(live, Outcome(RefreshOutcomeKind.Skipped, RefreshMessages.LiveIdentityUnverified), summary);
            }
            else
            {
                candidates.Add(new Candidate(live, parked.FirstOrDefault(profile => profile.Email == live)?.FolderPath, IsLive: true));
            }
        }

        foreach (ParkedProfile profile in parked)
        {
            if (profile.Email == liveEmail || !Wanted(profile.Email))
            {
                continue;
            }

            if (roster.Find(profile.Email) is { Paused: true })
            {
                // A paused account rides along on a full pass so its login can be
                // renewed before it lapses; whether that costs a token request is
                // its login expiry's business, decided on its own turn. A
                // single-account refresh is the operator asking about one card,
                // and a paused card has nothing to ask about.
                if (request.Account is null && profile.HasCredentials)
                {
                    renewals.Add(new Candidate(profile.Email, profile.FolderPath, IsLive: false, PausedRenewal: true));
                }
                else
                {
                    Record(profile.Email, Outcome(RefreshOutcomeKind.Skipped, RefreshMessages.Paused), summary);
                }
            }
            else if (!profile.HasCredentials)
            {
                Record(profile.Email, Outcome(RefreshOutcomeKind.NeedsLogin, RefreshMessages.NeedsLogin), summary);
            }
            else
            {
                candidates.Add(new Candidate(profile.Email, profile.FolderPath, IsLive: false));
            }
        }

        // A roster entry that has never been logged in owns no folder, so the
        // listing above cannot see it; its card says so rather than staying blank.
        foreach (RosterEntry entry in roster.Entries)
        {
            if (Wanted(entry.Email)
                && entry.Email != liveEmail
                && !parked.Any(profile => profile.Email == entry.Email))
            {
                Record(entry.Email, Outcome(RefreshOutcomeKind.NeedsLogin, RefreshMessages.NeedsLogin), summary);
            }
        }

        IReadOnlyList<RefreshCandidate> order = RefreshOrder.Order(
            [.. candidates.Select(candidate => new RefreshCandidate(candidate.Email, LastReadAt(candidate.Email)))]);
        // The renewals stay out of the ordering, which is about whose turn it is
        // to be read, and go first: a pass under a shared rate bucket ends on a
        // 429 partway down the reading order, and a renewal queued behind that
        // would be the one thing a pass never gets to.
        return [.. renewals, .. order.Select(ordered => candidates.First(candidate => candidate.Email == ordered.Email))];
    }

    /// <summary>
    /// One account's turn. The order of the guards is the whole of the safety
    /// argument: nothing is sent while a lockout stands, a stranded folder is
    /// restored before its pair is read at all, the live pair is never refreshed,
    /// a parked pair known to be expired skips the doomed read, and the budget's
    /// reservation is taken only when a request is about to leave. A paused
    /// account takes the same guards up to the pair being in hand and then leaves
    /// for <see cref="RenewPausedLoginAsync"/>, which never reads.
    /// </summary>
    private async Task<Turn> TurnAsync(Candidate candidate, bool sentRead, CancellationToken stopping)
    {
        try
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            if (_state.LockedUntil(now) is not null)
            {
                return new Turn(LockedOut());
            }

            if (!candidate.IsLive
                && candidate.FolderPath is string stranded
                && _recovery.HasRecoveryFor(stranded)
                && await RestoreUnderGateAsync(stranded) is RefreshOutcome blocked)
            {
                return new Turn(blocked);
            }

            CredentialPair? pair = candidate.IsLive
                ? await _pairs.ReadLiveAsync(stopping)
                : await _pairs.ReadParkedAsync(candidate.FolderPath!, stopping);
            if (pair is null)
            {
                return new Turn(Outcome(RefreshOutcomeKind.NeedsLogin, RefreshMessages.NeedsLogin));
            }

            if (candidate.PausedRenewal)
            {
                return await RenewPausedLoginAsync(candidate, pair, now);
            }

            if (pair.AccessTokenExpiresAt <= now)
            {
                if (candidate.IsLive)
                {
                    return new Turn(Outcome(RefreshOutcomeKind.SessionWillRefresh, RefreshMessages.SessionWillRefresh));
                }

                GatedRefresh refreshed = await RefreshUnderGateAsync(candidate.FolderPath!, pair);
                if (refreshed.Outcome is RefreshOutcome refused)
                {
                    return new Turn(refused, refreshed.EndsPass);
                }

                pair = refreshed.Pair!;
            }

            return await ReadAsync(candidate, pair, sentRead, retried: false, stopping);
        }
#pragma warning disable CA1031 // Do not catch general exception types
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            // One account's unforeseen failure costs that account its turn and
            // nothing more; the next nine still get read.
            LogTurnFailed(candidate.Email.Value, exception.GetType().Name);
            LogTurnFailedDetail(candidate.Email.Value, exception);
            return new Turn(Outcome(RefreshOutcomeKind.ReadFailed, RefreshMessages.ReadFailedTransport));
        }
    }

    /// <summary>
    /// The usage read, and the one retry a 401 on a parked pair earns. The
    /// retry goes back through <see cref="RefreshBudget.TryReserve"/> rather than
    /// riding the first reservation, because the budget refunded that one itself
    /// and its rule for a token the endpoint keeps rejecting is the one that
    /// should decide, not this method.
    /// </summary>
    private async Task<Turn> ReadAsync(Candidate candidate, CredentialPair pair, bool sentRead, bool retried, CancellationToken stopping)
    {
        if (!_budget.TryReserve(candidate.Email))
        {
            return new Turn(BudgetRefused(candidate.Email));
        }

        if (sentRead)
        {
            await _pace(_spacing, stopping);
        }

        Result<JsonDocument, UsageReadFailure> read = await _usage().ReadUsageAsync(pair.AccessToken, stopping);
        if (read.IsFailure)
        {
            return await FailedReadAsync(candidate, read.Error, retried, stopping);
        }

        using JsonDocument body = read.Value;
        Result<IReadOnlyList<UsageLimit>, string> limits = UsageResponseParser.ParseLimits(body.RootElement);
        if (limits.IsFailure)
        {
            LogReadUnparsable(candidate.Email.Value, limits.Error);
            return new Turn(Outcome(RefreshOutcomeKind.ReadFailed, RefreshMessages.ReadFailedMalformed), SentRead: true);
        }

        UsageSnapshot snapshot = new(
            candidate.Email,
            _timeProvider.GetUtcNow(),
            QuotaSource.OnDemandRefresh,
            limits.Value,
            UsageResponseParser.ParseExtraUsage(body.RootElement));
        _state.RecordSnapshot(snapshot);
        await _cache.SaveAsync(candidate.Email, snapshot);
        return new Turn(Outcome(RefreshOutcomeKind.Read, RefreshMessages.Read), SentRead: true);
    }

    private async Task<Turn> FailedReadAsync(Candidate candidate, UsageReadFailure failure, bool retried, CancellationToken stopping)
    {
        switch (failure.Kind)
        {
            case UsageReadFailureKind.Unauthorized when candidate.IsLive:
                // The running session owns the live lineage and renews it itself;
                // this tool refreshing it would rotate the token underneath the CLI.
                // The reservation is still refunded, exactly as it is on the parked
                // path: the endpoint rejected the token rather than serving the
                // read, so nothing should make the next pass wait out the gap for a
                // read that never happened.
                _budget.RecordUnauthorized(candidate.Email);
                return new Turn(Outcome(RefreshOutcomeKind.SessionWillRefresh, RefreshMessages.SessionWillRefresh), SentRead: true);

            case UsageReadFailureKind.Unauthorized when !retried:
                {
                    _budget.RecordUnauthorized(candidate.Email);
                    CredentialPair? current = await _pairs.ReadParkedAsync(candidate.FolderPath!, stopping);
                    if (current is null)
                    {
                        return new Turn(Outcome(RefreshOutcomeKind.NeedsLogin, RefreshMessages.NeedsLogin), SentRead: true);
                    }

                    GatedRefresh refreshed = await RefreshUnderGateAsync(candidate.FolderPath!, current);
                    return refreshed.Outcome is RefreshOutcome refused
                        ? new Turn(refused, refreshed.EndsPass, SentRead: true)
                        : await ReadAsync(candidate, refreshed.Pair!, sentRead: false, retried: true, stopping);
                }

            case UsageReadFailureKind.Unauthorized:
                // A freshly rotated access token the endpoint still rejects is not
                // something another refresh can fix.
                return new Turn(Outcome(RefreshOutcomeKind.ReadFailed, RefreshMessages.ReadFailedUnauthorized), SentRead: true);

            case UsageReadFailureKind.RateLimited:
                {
                    TimeSpan retryAfter = Lockout(failure.RetryAfter);
                    DateTimeOffset until = _timeProvider.GetUtcNow() + retryAfter;
                    _budget.RecordLockout(candidate.Email, retryAfter);
                    _state.UsageLockedUntil = until;
                    LogRateLimited(candidate.Email.Value, (int)retryAfter.TotalSeconds);
                    return new Turn(
                        Outcome(RefreshOutcomeKind.RateLimited, RefreshMessages.RateLimited(retryAfter), until),
                        EndsPass: true,
                        SentRead: true);
                }

            default:
                LogReadFailed(candidate.Email.Value, failure.Kind.ToString(), failure.Detail);
                return new Turn(
                    Outcome(
                        RefreshOutcomeKind.ReadFailed,
                        failure.Kind == UsageReadFailureKind.MalformedBody ? RefreshMessages.ReadFailedMalformed : RefreshMessages.ReadFailedTransport),
                    SentRead: true);
        }
    }

    /// <summary>
    /// A paused account's login, renewed before it lapses. Nothing reads a paused
    /// account, which is exactly why its twenty-eight-day login can run out
    /// unnoticed: the operator finds out when they try to switch to it and the
    /// pair is already dead. Inside the last week of that login the pass spends
    /// the gated refresh unit on it and nothing else — no usage read, and no
    /// reservation out of a read budget that belongs to the accounts still in the
    /// rotation. Further out, with no recorded login expiry, or within a day of
    /// the last renewal, the account stays what it was: skipped. A renewal that
    /// lands is a skip too, with the sentence that says why the card still
    /// carries no numbers.
    /// </summary>
    private async Task<Turn> RenewPausedLoginAsync(Candidate candidate, CredentialPair pair, DateTimeOffset now)
    {
        if (pair.LoginExpiresAt is not DateTimeOffset expiry || expiry - now > _loginRenewalWindow)
        {
            return new Turn(Outcome(RefreshOutcomeKind.Skipped, RefreshMessages.Paused));
        }

        if (_state.LoginRenewedAt(candidate.FolderPath!) is DateTimeOffset renewed && now - renewed < _loginRenewalInterval)
        {
            return new Turn(Outcome(RefreshOutcomeKind.Skipped, RefreshMessages.PausedLoginRenewedRecently));
        }

        GatedRefresh refreshed = await RefreshUnderGateAsync(candidate.FolderPath!, pair);
        // Every refusal the unit makes before it posts is a skip, and nothing it
        // decides after the endpoint has answered is one, so the kind is what says
        // whether this turn owes the next candidate the spacing.
        bool posted = refreshed.Outcome is null or { Kind: not RefreshOutcomeKind.Skipped };
        // A renewal is recorded only where the endpoint answered with credentials:
        // the login itself is renewed the moment it does, whether or not the write
        // back landed, and a stranded pair is put back by the next restore. A
        // transport failure or a 429 renewed nothing, and recording one would have
        // the card claim a renewal for a day that never happened. Nor is a lost
        // lineage recorded: the next pass would skip it as renewed and bury the one
        // outcome the operator has to act on.
        if (refreshed.Outcome is null or { Kind: RefreshOutcomeKind.Stranded })
        {
            _state.RecordLoginRenewal(candidate.FolderPath!, now);
        }

        return new Turn(
            refreshed.Outcome ?? Outcome(RefreshOutcomeKind.Skipped, RefreshMessages.PausedLoginRenewed),
            refreshed.EndsPass,
            SentRead: posted);
    }

    /// <summary>
    /// The restore of a stranded folder, under the same gate and the same login
    /// check as the write-back that stranded it. A restore rewrites a credential
    /// file by compare-and-swap, so it is a credential mutation like any other:
    /// outside the gate it could land between a switch's journal write and its
    /// unpark, and against a login in flight it would rewrite the folder the
    /// child is about to be judged on by digest. The gate is taken here rather
    /// than inside <see cref="RecoveryFiles"/> because the removal route already
    /// holds it when it restores, and this gate is not re-entrant.
    /// </summary>
    /// <returns>
    /// Null when the folder is no longer stranded and the turn may go on, or the
    /// outcome that stopped it. The file is kept in every refusal.
    /// </returns>
    private async Task<RefreshOutcome?> RestoreUnderGateAsync(string folder)
    {
        IDisposable? permit = null;
        try
        {
            try
            {
                permit = await _gate.AcquireAsync(_gateWait, CancellationToken.None);
            }
            catch (TimeoutException)
            {
                return Outcome(RefreshOutcomeKind.Skipped, RefreshMessages.MutationInProgress);
            }

            if (_logins.IsRunningAgainst(folder))
            {
                return Outcome(RefreshOutcomeKind.Skipped, RefreshMessages.LoginInProgress);
            }

            // Under CancellationToken.None, the gated write-back's rule: a restore
            // abandoned halfway is the same lost lineage whichever half of it the
            // host's shutdown lands in.
            return await _recovery.RestoreAsync(folder, CancellationToken.None)
                ? null
                : Outcome(RefreshOutcomeKind.Stranded, RefreshMessages.Stranded);
        }
        finally
        {
            permit?.Dispose();
        }
    }

    /// <summary>
    /// The token POST and the write-back, as one unit under the mutation gate and
    /// under <see cref="CancellationToken.None"/>. Neither half may be abandoned
    /// once the endpoint has answered: the old refresh token is dead from that
    /// moment, so a cancellation between the POST and the write would strand the
    /// only working lineage. Everything the unit decides on is read under the
    /// gate, never before it.
    /// </summary>
    private async Task<GatedRefresh> RefreshUnderGateAsync(string folder, CredentialPair before)
    {
        IDisposable? permit = null;
        try
        {
            try
            {
                permit = await _gate.AcquireAsync(_gateWait, CancellationToken.None);
            }
            catch (TimeoutException)
            {
                return Refused(RefreshOutcomeKind.Skipped, RefreshMessages.MutationInProgress);
            }

            // A login owns its folder for its whole ten-minute window and decides
            // whose pair the folder holds by the credential file's digest; a
            // rewrite underneath it would be judged as a login that never happened.
            if (_logins.IsRunningAgainst(folder))
            {
                return Refused(RefreshOutcomeKind.Skipped, RefreshMessages.LoginInProgress);
            }

            CredentialPair? current = await _pairs.ReadParkedAsync(folder, CancellationToken.None);
            if (current is null || current.Fingerprint != before.Fingerprint)
            {
                // A switch, a login, or a removal moved the pair between the read that
                // chose this account and the gate. Refusing here is what keeps the
                // POST from killing a refresh token that now belongs somewhere else.
                return Refused(RefreshOutcomeKind.Skipped, RefreshMessages.PairChanged);
            }

            Result<RefreshedTokens, UsageReadFailure> refreshed = await _tokens().RefreshAsync(current.RefreshToken, CancellationToken.None);
            if (refreshed.IsSuccess)
            {
                CredentialPair rotated = Rotate(current, refreshed.Value);
                try
                {
                    return await WriteBackAsync(folder, current, rotated);
                }
#pragma warning disable CA1031 // Do not catch general exception types
                catch (Exception exception)
#pragma warning restore CA1031
                {
                    // Past this point the old refresh token is already dead, so
                    // every way out but the recovery directory loses the account.
                    // The retry loop names the failures it expects; this catches
                    // the ones nobody thought of, because the alternative is the
                    // turn's own catch-all dropping the only working lineage.
                    string name = FolderName(folder);
                    LogWriteBackFailed(name, exception.GetType().Name);
                    LogWriteBackFailedDetail(name, exception);
                    return await StrandAsync(folder, current.Fingerprint, rotated);
                }
            }

            if (refreshed.Error.Kind == UsageReadFailureKind.RateLimited)
            {
                TimeSpan retryAfter = Lockout(refreshed.Error.RetryAfter);
                DateTimeOffset until = _timeProvider.GetUtcNow() + retryAfter;
                _state.TokenLockedUntil = until;
                LogTokenHostRateLimited((int)retryAfter.TotalSeconds);
                return new GatedRefresh(
                    Outcome(RefreshOutcomeKind.RateLimited, RefreshMessages.RateLimited(retryAfter), until),
                    Pair: null,
                    EndsPass: true);
            }

            LogTokenRefreshFailed(FolderName(folder), refreshed.Error.Kind.ToString(), refreshed.Error.Detail);
            return Refused(RefreshOutcomeKind.TokenRefreshFailed, RefreshMessages.TokenRefreshFailed);
        }
        finally
        {
            permit?.Dispose();
        }
    }

    /// <summary>
    /// Lands the rotated pair, or parks it where the next start can. A
    /// <see cref="Result"/> failure from the store is deterministic — the only
    /// two it has are "no parked pair" and "not the pair being replaced", and
    /// both will say the same thing in a second — so it strands at once rather
    /// than spending the retry budget on a settled answer. An exception is the
    /// transient case: a file locked by a virus scanner, a folder momentarily
    /// unwritable, a credential file read back mid-write by whoever is writing it.
    /// </summary>
    private async Task<GatedRefresh> WriteBackAsync(string folder, CredentialPair current, CredentialPair rotated)
    {
        for (int attempt = 1; attempt <= _writeBackBackoff.Length; attempt++)
        {
            Exception transient;
            try
            {
                Result<Unit, string> written = await _pairs.WriteParkedAsync(folder, rotated, current.Fingerprint, CancellationToken.None);
                if (written.IsSuccess)
                {
                    // Guarded because the two fingerprints are sliced to build the
                    // line: the rotation's audit trail is worth the slice only when
                    // something is there to read it.
                    if (_logger.IsEnabled(LogLevel.Information))
                    {
                        // CA1873 does not see through the guard to a source-generated
                        // log method, and the two slices and the path segment below are
                        // nothing next to the credential file this line records.
#pragma warning disable CA1873 // Evaluation of this argument may be expensive
                        LogRotated(FolderName(folder), current.Fingerprint.Sha256Hex[..12], rotated.Fingerprint.Sha256Hex[..12]);
#pragma warning restore CA1873
                    }

                    return new GatedRefresh(Outcome: null, rotated);
                }

                LogWriteBackRefused(FolderName(folder), written.Error);
                return await StrandAsync(folder, current.Fingerprint, rotated);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            {
                // JsonException among them: the store re-reads the folder's file
                // to compare fingerprints, and a file caught mid-write parses as
                // nothing at all. That is the same momentary state as a locked
                // file and deserves the same retry, not a strand.
                transient = exception;
            }

            string name = FolderName(folder);
            LogWriteBackRetried(name, attempt, transient.GetType().Name);
            LogWriteBackRetriedDetail(name, attempt, transient);
            await _pace(Jittered(_writeBackBackoff[attempt - 1]), CancellationToken.None);
        }

        return await StrandAsync(folder, current.Fingerprint, rotated);
    }

    private async Task<GatedRefresh> StrandAsync(string folder, RefreshTokenFingerprint expected, CredentialPair rotated)
    {
        if (await _recovery.WriteAsync(folder, expected, rotated, CancellationToken.None))
        {
            return Refused(RefreshOutcomeKind.Stranded, RefreshMessages.Stranded);
        }

        LogLost(FolderName(folder), expected.Sha256Hex[..12], rotated.Fingerprint.Sha256Hex[..12]);
        return Refused(RefreshOutcomeKind.Lost, RefreshMessages.Lost);
    }

    /// <summary>
    /// The rotated pair: the file's own bytes with four values replaced, so every
    /// sibling key the CLI wrote (the subscription type, anything a later version
    /// adds) survives the rewrite untouched. <c>refreshTokenExpiresAt</c> is kept
    /// as it was when the response omitted <c>refresh_token_expires_in</c>;
    /// writing nothing there would tell the page the login expired in 1970, and
    /// dropping the key would lose the only record of when it really does.
    /// </summary>
    private static CredentialPair Rotate(CredentialPair current, RefreshedTokens tokens)
    {
        JsonObject raw = current.Raw.DeepClone().AsObject();
        JsonObject oauth = raw["claudeAiOauth"]!.AsObject();
        oauth["accessToken"] = tokens.AccessToken;
        oauth["refreshToken"] = tokens.RefreshToken;
        oauth["expiresAt"] = tokens.AccessTokenExpiresAt.ToUnixTimeMilliseconds();
        if (tokens.LoginExpiresAt is DateTimeOffset loginExpiry)
        {
            oauth["refreshTokenExpiresAt"] = loginExpiry.ToUnixTimeMilliseconds();
        }

        // Cannot fail: the object came from a pair that parsed, and every value
        // replaced above is of the shape the parse requires.
        return CredentialPair.FromJson(raw).Value;
    }

    /// <summary>
    /// The backoff with up to a fifth either way, from the cryptographic source
    /// rather than <see cref="Random"/>: the value is not security-sensitive, but
    /// it is the only randomness in the app and one source is one thing to reason
    /// about.
    /// </summary>
    private static TimeSpan Jittered(TimeSpan backoff) =>
        backoff * (1.0 + (RandomNumberGenerator.GetInt32(-200, 201) / 1000.0));

    /// <summary>A 429's wait, floored: an absent or zero <c>Retry-After</c> is not an invitation to come straight back.</summary>
    private static TimeSpan Lockout(TimeSpan? retryAfter) =>
        retryAfter is TimeSpan wait && wait > TimeSpan.Zero ? wait : _lockoutFloor;

    private static string FolderName(string folderPath) =>
        Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath)));

    private RefreshOutcome Outcome(RefreshOutcomeKind kind, string message, DateTimeOffset? retryAt = null) =>
        new(kind, message, retryAt, _timeProvider.GetUtcNow());

    private GatedRefresh Refused(RefreshOutcomeKind kind, string message) =>
        new(Outcome(kind, message), Pair: null);

    /// <summary>What every candidate after a 429 reports: rate limited, with the same countdown, and nothing sent.</summary>
    private RefreshOutcome LockedOut()
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        DateTimeOffset until = _state.LockedUntil(now) ?? now;
        return Outcome(RefreshOutcomeKind.RateLimited, RefreshMessages.RateLimited(until - now), until);
    }

    private RefreshOutcome BudgetRefused(AccountEmail account)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        TimeSpan? wait = _budget.GapRemaining(account) ?? _budget.LockedOutFor(account);
        // The same two sources LastReadAt counts, for the same reason: a figure
        // loaded from the cache file was read from the endpoint too, just by the
        // run before this one, and "read N s ago" is exactly as true of it.
        string message = _state.LatestFor(account) is { Source: QuotaSource.OnDemandRefresh or QuotaSource.Cached } latest
            ? RefreshMessages.ReadRecently(now - latest.CapturedAt)
            : RefreshMessages.BudgetSpent;
        return Outcome(RefreshOutcomeKind.BudgetRefused, message, wait is TimeSpan remaining ? now + remaining : null);
    }

    /// <summary>
    /// When this account's numbers were last actually taken from the endpoint,
    /// which is what decides whose turn it is. A tee observation does not count:
    /// it cost the endpoint nothing and so says nothing about fairness.
    /// </summary>
    private DateTimeOffset? LastReadAt(AccountEmail account) =>
        _state.LatestFor(account) is { Source: QuotaSource.OnDemandRefresh or QuotaSource.Cached } snapshot
            ? snapshot.CapturedAt
            : null;

    private void Record(AccountEmail account, RefreshOutcome outcome, Dictionary<RefreshOutcomeKind, int> summary)
    {
        _state.RecordOutcome(account, outcome);
        summary[outcome.Kind] = summary.GetValueOrDefault(outcome.Kind) + 1;
    }

    /// <summary>
    /// One account the pass will read, and which pair it reads. A
    /// <paramref name="PausedRenewal"/> candidate is the exception: it is here
    /// only so its login can be renewed, and it is never read.
    /// </summary>
    private sealed record Candidate(AccountEmail Email, string? FolderPath, bool IsLive, bool PausedRenewal = false);

    /// <summary>How one account's turn ended, and what it cost the pass.</summary>
    private sealed record Turn(RefreshOutcome Outcome, bool EndsPass = false, bool SentRead = false);

    /// <summary>Either a rotated pair to read with, or the outcome that stopped the unit.</summary>
    private sealed record GatedRefresh(RefreshOutcome? Outcome, CredentialPair? Pair, bool EndsPass = false);

    // Every failure line comes in a pair: the type and the curated reason at a
    // level the operator sees, and the exception itself at Debug. A stack trace
    // or a file-system message quotes paths, and these lines are read off a
    // console the operator leaves open beside ten real accounts.
    [LoggerMessage(Level = LogLevel.Warning, Message = "the refresh pass could not be built ({Failure})")]
    private partial void LogPassFailed(string failure);

    [LoggerMessage(Level = LogLevel.Debug, Message = "the refresh pass could not be built")]
    private partial void LogPassFailedDetail(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "the refresh of {Account} failed unexpectedly ({Failure})")]
    private partial void LogTurnFailed(string account, string failure);

    [LoggerMessage(Level = LogLevel.Debug, Message = "the refresh of {Account} failed unexpectedly")]
    private partial void LogTurnFailedDetail(string account, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "rotated the credential pair for {Folder} ({Previous} to {Rotated})")]
    private partial void LogRotated(string folder, string previous, string rotated);

    [LoggerMessage(Level = LogLevel.Warning, Message = "the write-back for {Folder} was refused by the store: {Reason}")]
    private partial void LogWriteBackRefused(string folder, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "write-back attempt {Attempt} for {Folder} failed ({Failure})")]
    private partial void LogWriteBackRetried(string folder, int attempt, string failure);

    [LoggerMessage(Level = LogLevel.Debug, Message = "write-back attempt {Attempt} for {Folder} failed")]
    private partial void LogWriteBackRetriedDetail(string folder, int attempt, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "the write-back for {Folder} failed unexpectedly and the rotated pair is being stranded ({Failure})")]
    private partial void LogWriteBackFailed(string folder, string failure);

    [LoggerMessage(Level = LogLevel.Debug, Message = "the write-back for {Folder} failed unexpectedly")]
    private partial void LogWriteBackFailedDetail(string folder, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "the rotated credential pair for {Folder} is lost (replacing {Previous}, rotated to {Rotated}); log that account in again")]
    private partial void LogLost(string folder, string previous, string rotated);

    [LoggerMessage(Level = LogLevel.Warning, Message = "the token refresh for {Folder} failed ({Kind}): {Detail}")]
    private partial void LogTokenRefreshFailed(string folder, string kind, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "the token host is rate limiting; no refresh for {Seconds} s")]
    private partial void LogTokenHostRateLimited(int seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "the usage host is rate limiting {Account}; no read for {Seconds} s")]
    private partial void LogRateLimited(string account, int seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "the usage read for {Account} failed ({Kind}): {Detail}")]
    private partial void LogReadFailed(string account, string kind, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "the usage response for {Account} did not parse: {Reason}")]
    private partial void LogReadUnparsable(string account, string reason);
}
