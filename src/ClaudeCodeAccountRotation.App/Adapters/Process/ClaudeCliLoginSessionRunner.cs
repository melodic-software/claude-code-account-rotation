using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ClaudeCodeAccountRotation.App.Accounts;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.App.Switching;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Accounts;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Ports;
using Microsoft.Extensions.Logging;

namespace ClaudeCodeAccountRotation.App.Adapters.Process;

/// <summary>
/// Drives <c>claude auth login --email &lt;address&gt;</c> under one account's
/// config directory with stdin and stdout piped, per the spike that settled the
/// mechanism on this machine.
/// <para>
/// What the spike settled and this file relies on: the sign-in URL is printed
/// wrapped in OSC 8 hyperlink escapes, so the URL is matched as a substring on
/// its authorize path rather than read as a line; the code prompt reads a piped
/// line; a rejected code leaves the prompt open for another attempt rather than
/// ending the process; and a non-zero exit says nothing on its own, so the
/// completion signal is the credential file appearing in the folder.
/// </para>
/// <para>
/// A finished session stays readable for ten minutes, the same window a login
/// is given to complete, and is then dropped. The child is killed on expiry, on
/// cancel, and at shutdown. Nothing here writes the code anywhere but the
/// child's standard input, and no message returned to the page is built from
/// the child's own output. An audit line names the account and the outcome of
/// a start, a completion, a failure, or a revocation, and never the code, the
/// pair, or anything the child printed.
/// </para>
/// </summary>
internal sealed partial class ClaudeCliLoginSessionRunner : ILoginSessionRunner, IDisposable
{
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long a session stays readable after it leaves
    /// <see cref="LoginSessionState.Pending"/>. The same ten minutes as
    /// <see cref="SessionLifetime"/>: a reload during the login's own window
    /// still finds the session, and a finished one is never dropped sooner than
    /// that. A session that is still pending, or whose reader has not finished,
    /// is kept.
    /// </summary>
    internal static readonly TimeSpan CompletedSessionRetention = SessionLifetime;

    /// <summary>
    /// How long shutdown waits for a login's reader to leave the child before
    /// the child is disposed. The reader is cancelled first; this bound is only
    /// the finish that follows. Every session in flight shares that one wait.
    /// </summary>
    internal static readonly TimeSpan DisposeWait = TimeSpan.FromSeconds(2);

    /// <summary>How long a start waits for the URL, and a code for the CLI's answer.</summary>
    private static readonly TimeSpan _replyBudget = TimeSpan.FromSeconds(45);

    private static readonly TimeSpan _gateWait = TimeSpan.FromSeconds(30);
    private const int MaximumCodeLength = 256;

    private const string RejectedMessage = "That code was rejected. Copy the whole code from the browser and paste it again.";
    private const string ExpiredMessage = "This login expired after ten minutes. Start it again.";
    private const string EndedMessage = "The login ended without writing a credential file. Start it again.";
    private const string ResidueKeptMessage = "The login wrote credentials but named no account, so the folder was left exactly as it is.";
    private const string EndedKeptMessage =
        "The login ended without writing new credentials; the earlier login in that folder was left as it was. Start it again.";
    private const string ExpiredKeptMessage =
        "This login expired after ten minutes; the earlier login in that folder was left as it was. Start it again.";
    private const string UnreadablePairAtStartReason =
        "the credentials already in that folder could not be read, so a new login there could not be told apart from them; try again in a moment";
    private const string UnreadablePairMessage =
        "The credentials in that folder could not be read after the login ended, so what it wrote could not be told from what was"
        + " there before, and nothing was deleted. Remove the account from the roster, which revokes that login, before switching to it.";
    private const string UnjudgedKeptMessage =
        "What kind of account signed in could not be checked, and the folder held a login before this one, so its credentials were"
        + " left in place rather than deleted. Remove the account from the roster, which revokes that login, before switching to it.";

    private const string RefusedPrefix = "That login was refused: ";
    private const string UnreadableTierReason =
        "the CLI did not report what kind of account signed in, and a tier that cannot be read is refused rather than admitted";
    private const string RevokedSuffix =
        ". The credentials it wrote were revoked with `claude auth logout` and deleted, so nothing joined the rotation."
        + " Log in again and pick the Max account at the sign-in step.";
    private const string NotRevokedSuffix =
        ". The credentials it wrote were deleted but could not be revoked, so that refresh token stays valid until its login expires."
        + " Log in again and pick the Max account at the sign-in step.";
    private const string NotDeletedSuffix =
        ". The credentials it wrote could not be deleted; remove the account from the roster before switching to anything.";
    private const string UnjudgedMessage =
        "Another credential change held the lock, so what kind of account signed in could not be checked."
        + " Remove the account from the roster, which revokes that login, before switching to it.";
    private const string FaultedMessage =
        "Judging what kind of account signed in failed, so the credentials in that folder were left in place."
        + " Remove the account from the roster, which revokes that login, before switching to it.";
    private const string PumpFaultedMessage = "The login stopped unexpectedly. Start it again.";

    private readonly LoginChildFactory _start;
    private readonly ProfileFolderStore _profiles;
    private readonly ClaudeStateFile _stateFile;
    private readonly CredentialMutationGate _gate;
    private readonly IClaudeCliAuthStatus _authStatus;
    private readonly IClaudeCliLogout _logout;
    private readonly TimeProvider _clock;
    private readonly ILogger<ClaudeCliLoginSessionRunner> _logger;
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private int _disposed;

    public ClaudeCliLoginSessionRunner(
        LoginChildFactory start,
        ProfileFolderStore profiles,
        ClaudeStateFile stateFile,
        CredentialMutationGate gate,
        IClaudeCliAuthStatus authStatus,
        IClaudeCliLogout logout,
        TimeProvider clock,
        ILogger<ClaudeCliLoginSessionRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(stateFile);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(authStatus);
        ArgumentNullException.ThrowIfNull(logout);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _start = start;
        _profiles = profiles;
        _stateFile = stateFile;
        _gate = gate;
        _authStatus = authStatus;
        _logout = logout;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// The argument list, never a command line. <c>--email</c> is what puts
    /// <c>login_hint</c> on the authorize URL, which is what pre-fills the
    /// address in the mapped browser profile.
    /// </summary>
    public static string[] Arguments(AccountEmail email) => ["auth", "login", "--email", email.Value];

    /// <summary>
    /// Admits a login and starts its child, all of it under the one mutation
    /// gate, which is what makes starting a login safe beside a switch.
    /// <para>
    /// Three things happen inside the gate and none of them is safe outside it.
    /// The scan for a login already running against the folder, and the
    /// registration that makes this one visible to that scan, are one step, so
    /// a double-submit cannot put two children on one <c>CLAUDE_CONFIG_DIR</c>.
    /// The live account is read again here, not just at the endpoint, because a
    /// switch can complete between the endpoint's read and this call, and a
    /// login into the live account's own parked folder is the second holder the
    /// tool exists to prevent. And because the switch reads the registration
    /// under the same gate, a switch either sees this login and refuses or
    /// completes before this one is admitted; the two can never interleave.
    /// </para>
    /// </summary>
    public async Task<Result<LoginSession, string>> StartAsync(AccountEmail email, string folderPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        string folder = Path.GetFullPath(folderPath);
        Session session;
        IDisposable? permit = null;
        try
        {
            try
            {
                permit = await _gate.AcquireAsync(_gateWait, cancellationToken);
            }
            catch (TimeoutException)
            {
                return Result<LoginSession, string>.Failure("another credential change is in progress; try the login again in a moment");
            }

            if (RunningAgainst(folder) is Session running)
            {
                return Result<LoginSession, string>.Failure(
                    "a login for " + running.Email.Value + " is already running against that folder; finish it or let it expire first");
            }

            if ((await _stateFile.ReadAccountBlockAsync(cancellationToken))?.Email == email)
            {
                return Result<LoginSession, string>.Failure(
                    "that account is live on this machine; switch away from it before logging it in again");
            }

            // What the folder holds before the child can touch it, read under the
            // same gate, so nothing else is moving it: the finish tells this login's
            // own pair from an earlier one only by comparison with this reading.
            string? digestAtStart;
            try
            {
                digestAtStart = await DigestCredentialFileAsync(folder);
            }
            catch (IOException)
            {
                return Result<LoginSession, string>.Failure(UnreadablePairAtStartReason);
            }
            catch (UnauthorizedAccessException)
            {
                return Result<LoginSession, string>.Failure(UnreadablePairAtStartReason);
            }

            Result<ILoginChild, string> child = _start(Arguments(email), folder);
            if (child.IsFailure)
            {
                return Result<LoginSession, string>.Failure(child.Error);
            }

            session = new Session(LoginSessionId.New(), email, folder, child.Value, digestAtStart, _clock.GetUtcNow() + SessionLifetime, SessionLifetime, _clock);
            // Started under the gate, so a session anything can find already has its
            // pump. The pump's own finish takes the gate in turn and waits on it
            // until the finally below releases it, which is at once.
            session.Pump = Task.Run(() => PumpAsync(session), CancellationToken.None);
            _sessions[session.Id.Value] = session;
            LogLoginStarted(session.Email.Value);
        }
        finally
        {
            permit?.Dispose();
        }

        // Outside the gate: only the wait for the URL, which can outlast the child.
        await WaitAsync(session.Started.Task, cancellationToken);
        if (session.SignInUrl is null)
        {
            Cancel(session);
            return Result<LoginSession, string>.Failure(
                "the CLI printed no sign-in URL; run `claude auth login` by hand once to see what it says");
        }

        return Result<LoginSession, string>.Success(Snapshot(session));
    }

    public async Task<Result<LoginSession, string>> SubmitCodeAsync(LoginSessionId id, string code, CancellationToken cancellationToken)
    {
        EvictFinishedSessions();
        if (!_sessions.TryGetValue(id.Value, out Session? session))
        {
            return Result<LoginSession, string>.Failure("no login session with that id is running");
        }

        EnforceExpiry(session);
        if (session.State != LoginSessionState.Pending)
        {
            return Result<LoginSession, string>.Failure("that login session is no longer running; start a new one");
        }

        // Everything this refuses is refused by shape alone; no branch here ever
        // puts the code itself into the reason.
        string trimmed = (code ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return Result<LoginSession, string>.Failure("paste the code from the browser first");
        }

        if (trimmed.Length > MaximumCodeLength)
        {
            return Result<LoginSession, string>.Failure("that is longer than any login code; paste just the code");
        }

        if (trimmed.Any(char.IsControl))
        {
            // A newline inside the code would be two lines at the prompt, and the
            // second would answer whatever the CLI asks next.
            return Result<LoginSession, string>.Failure("a login code holds no line breaks or control characters");
        }

        TaskCompletionSource echo = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (session.Sync)
        {
            // One code in flight per session. The pump answers through the field, so
            // a second submit that replaced it would leave the first caller's own
            // task uncompleted until its reply budget ran out, and the CLI's answer
            // to the first code would be read as the answer to the second.
            if (session.Echo is not null)
            {
                return Result<LoginSession, string>.Failure("that login is still checking the last code; wait for its answer before pasting again");
            }

            session.Message = null;
            session.Output.Clear();
            session.Echo = echo;
        }

        try
        {
            await session.Child.WriteCodeAsync(trimmed, cancellationToken);
            await WaitAsync(echo.Task, cancellationToken);
        }
        finally
        {
            lock (session.Sync)
            {
                if (ReferenceEquals(session.Echo, echo))
                {
                    session.Echo = null;
                }
            }
        }

        return Result<LoginSession, string>.Success(Snapshot(session));
    }

    public bool IsRunningAgainst(string folderPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        return RunningAgainst(Path.GetFullPath(folderPath)) is not null;
    }

    public LoginSession? Status(LoginSessionId id)
    {
        EvictFinishedSessions();
        if (!_sessions.TryGetValue(id.Value, out Session? session))
        {
            return null;
        }

        EnforceExpiry(session);
        return Snapshot(session);
    }

    /// <summary>
    /// Completes once the session's pump has finished the folder. Expiry settles a
    /// session inside the request that notices it, and the folder is finished on
    /// the pump afterwards; a test asserting on what that finish left behind has
    /// nothing else to await, and an id no session answers to is that test waiting
    /// for nothing, so it throws rather than completing.
    /// </summary>
    internal Task FinishedAsync(LoginSessionId id) =>
        _sessions.TryGetValue(id.Value, out Session? session)
            ? session.Pump
            : throw new ArgumentException("no login session " + id.Value, nameof(id));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Session[] sessions = [.. _sessions.Values];
        foreach (Session session in sessions)
        {
            Cancel(session);
        }

        // The reader is cancelled above. Wait for it to leave the child, but
        // not forever: a pump stuck past this bound is disposed anyway, which
        // is the shutdown race the wait exists to make rare.
        WaitForPumps(sessions);

        foreach (Session session in sessions)
        {
            session.Dispose();
        }

        _sessions.Clear();
    }

    /// <summary>
    /// The first URL in the text whose path is the OAuth authorize path. The
    /// pattern stops at every control character, so the OSC 8 escape bytes the
    /// URL is wrapped in are never part of the match, and it anchors on the
    /// path rather than on the prompt's wording, which is likelier to change.
    /// </summary>
    internal static Uri? ExtractAuthorizeUrl(string text)
    {
        foreach (Match match in UrlPattern().Matches(text))
        {
            if (Uri.TryCreate(match.Value, UriKind.Absolute, out Uri? url)
                && url.AbsolutePath.EndsWith("/oauth/authorize", StringComparison.Ordinal))
            {
                return url;
            }
        }

        return null;
    }

    [GeneratedRegex(@"https://[^\s\p{Cc}""'<>]+", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex UrlPattern();

    /// <summary>
    /// Reads the child, then finishes the folder. Anything other than the child
    /// ending is logged and, if the session is still pending, recorded as a
    /// failure before the ten-minute expiry. The pump task itself does not
    /// fault: an exception that escaped this method would be unobserved, because
    /// nothing in production awaits <see cref="Session.Pump"/>, and the session
    /// would stay pending until expiry with no line in the log.
    /// </summary>
    private async Task PumpAsync(Session session)
    {
        try
        {
            // Every step runs. A fault from the read must not skip killing the
            // child or finishing the folder; the first fault is the one logged.
            Exception? read = await RunStepAsync(() => ReadLoopAsync(session));
            Exception? kill = RunStep(session.Child.Kill);
            Exception? finish = await RunStepAsync(() => FinishAsync(session));
            Exception? fault = read ?? kill ?? finish;
            if (fault is not null)
            {
                RecordPumpFault(session, fault);
            }
        }
#pragma warning disable CA1031 // Do not catch general exception types
        catch (Exception exception)
#pragma warning restore CA1031
        {
            // The steps above capture their own failures. This is a fault in the
            // bookkeeping around them, and it still has to end the session.
            RecordPumpFault(session, exception);
        }
        finally
        {
            session.Started.TrySetResult();
        }
    }

    private async Task ReadLoopAsync(Session session)
    {
        while (true)
        {
            string? chunk = await session.Child.ReadAsync(session.Lifetime.Token);
            if (chunk is null)
            {
                return;
            }

            string seen;
            lock (session.Sync)
            {
                session.Output.Append(chunk);
                seen = session.Output.ToString();
            }

            if (session.SignInUrl is null && ExtractAuthorizeUrl(seen) is Uri url)
            {
                session.SignInUrl = url;
                session.Started.TrySetResult();
            }

            // A rejection is only ever a nicety: the completion signal is the
            // credential file, so a CLI that reworded this line leaves the
            // session pending and the operator free to paste again.
            if (seen.Contains("invalid code", StringComparison.OrdinalIgnoreCase))
            {
                Settle(session, LoginSessionState.Pending, RejectedMessage);
            }

            if (_clock.GetUtcNow() >= session.ExpiresAt)
            {
                EnforceExpiry(session);
            }
        }
    }

    /// <summary>
    /// Runs one step of the pump. Cancellation and a closed pipe are how a
    /// login ends, and neither is a fault. Anything else is returned so the
    /// caller can log it and still run the steps after it: a throw from
    /// killing the child must not skip the folder finish or the signal that
    /// the session has ended.
    /// </summary>
    private static async Task<Exception?> RunStepAsync(Func<Task> step)
    {
        try
        {
            await step();
            return null;
        }
#pragma warning disable CA1031 // Do not catch general exception types
        catch (Exception exception)
#pragma warning restore CA1031
        {
            return IsOrdinaryEnd(exception) ? null : exception;
        }
    }

    private static Exception? RunStep(Action step)
    {
        try
        {
            step();
            return null;
        }
#pragma warning disable CA1031 // Do not catch general exception types
        catch (Exception exception)
#pragma warning restore CA1031
        {
            return IsOrdinaryEnd(exception) ? null : exception;
        }
    }

    private static bool IsOrdinaryEnd(Exception exception) =>
        exception is OperationCanceledException or IOException;

    private void RecordPumpFault(Session session, Exception exception)
    {
        LogPumpFaulted(session.Email.Value, exception.GetType().Name, exception);
        // onlyWhilePending: FinishAsync may already have said how the folder
        // was left, and that message is the one the page should keep. The log
        // above is what names the fault either way.
        Settle(session, LoginSessionState.Failed, PumpFaultedMessage, onlyWhilePending: true);
    }

    /// <summary>
    /// The single point where a finished login is taken at its word, and only
    /// once the child has ended. Reading the folder mid-run could see a
    /// credential file written before the state file names its account, and
    /// pruning on that reading would delete the fresh identity and leave the
    /// stale one.
    /// <para>
    /// A pair in the folder is not proof that this login wrote it. The folder
    /// may have held a working login before the child started ("Log in again"),
    /// and a login that ended without completing leaves that pair exactly as it
    /// found it. So the file is compared with the reading taken at the start:
    /// the same bytes are the earlier login, untouched, and the session ends the
    /// way a login that wrote nothing does; different bytes, or a file where
    /// there was none, are this login's and are judged.
    /// </para>
    /// </summary>
    private async Task FinishAsync(Session session)
    {
        // Every way the rest can fail is caught here: a fault escaping would leave
        // the session pending behind a killed child until its expiry, and the
        // operator watching a completed login say "still waiting" for ten minutes.
        try
        {
            // The whole of it under one permit. Reading the pair, judging the tier,
            // and revoking it are separate steps, and a switch that slipped between
            // any two of them would take the very pair being compared or refused.
            using IDisposable permit = await _gate.AcquireAsync(_gateWait, CancellationToken.None);
            string? digest;
            try
            {
                digest = await DigestCredentialFileAsync(session.Folder);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Unreadable, so unprovable either way; the one safe disposition is
                // to touch nothing and say so.
                Settle(session, LoginSessionState.Failed, UnreadablePairMessage);
                return;
            }

            if (digest is null)
            {
                // The folder holds no pair: either the login never wrote one, or
                // something that held the gate first moved it. Nothing here to judge.
                SettleUnwritten(session, ExpiredMessage, EndedMessage);
                return;
            }

            if (digest == session.CredentialFileDigestAtStart)
            {
                SettleUnwritten(session, ExpiredKeptMessage, EndedKeptMessage);
                return;
            }

            (LoginSessionState state, string message) = await AdmitAsync(session);
            Settle(session, state, message);
        }
        catch (TimeoutException)
        {
            // Nothing may move, revoke, or delete what is in a folder without the
            // gate, so an unjudged pair is left exactly where it is and the operator
            // is told to remove the account rather than switch to it.
            Settle(session, LoginSessionState.Failed, UnjudgedMessage);
        }
#pragma warning disable CA1031 // Do not catch general exception types
        catch (Exception exception)
#pragma warning restore CA1031
        {
            // Anything else that went wrong is still not a judgment, and the folder
            // holds whatever the login left; the session says so rather than hanging.
            // The fault's own text stays out of the message: a message built from one
            // could carry a path off this machine onto the page, the way nothing built
            // from the child's output ever does. The log keeps it, which is the only
            // place a path is useful.
            LogFinishFaulted(session.Email.Value, exception.GetType().Name, exception);
            Settle(session, LoginSessionState.Failed, FaultedMessage);
        }
    }

    /// <summary>
    /// The end of a login that wrote no pair of its own: expired if its time ran
    /// out, failed otherwise, and only if nothing settled it first, since expiry
    /// is usually noticed and recorded by whichever request found it.
    /// </summary>
    private void SettleUnwritten(Session session, string expiredMessage, string endedMessage)
    {
        bool expired = _clock.GetUtcNow() >= session.ExpiresAt;
        Settle(
            session,
            expired ? LoginSessionState.Expired : LoginSessionState.Failed,
            expired ? expiredMessage : endedMessage,
            onlyWhilePending: true);
    }

    /// <summary>
    /// The SHA-256 of the credential file's bytes, or null when the folder holds
    /// none, read through the shared reader so the CLI's own write to the file
    /// is never blocked by it. Bytes, not the token inside them: an unparsable
    /// file still compares, and a completed login always writes new tokens.
    /// </summary>
    private static async Task<string?> DigestCredentialFileAsync(string folder)
    {
        string path = Path.Combine(folder, FileSystemCredentialPairStore.FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            byte[] bytes = await SharedFileReader.ReadAllBytesAsync(path, CancellationToken.None);
            return Convert.ToHexString(SHA256.HashData(bytes));
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Judges the tier of the account that actually signed in, under that
    /// account's own folder and against the same <see cref="MaxTierAdmission"/>
    /// the roster admits by, from the CLI's answer alone, and adopts the fresh
    /// login only once that judgment is in.
    /// <para>
    /// No recorded identity takes part. A folder logged in before carries the
    /// earlier login's <c>profile.json</c>, and a login whose tidy-up could not
    /// run leaves a state file behind too, so nothing on disk proves which login
    /// wrote the block that names a Max tier; handing any of it to the judgment
    /// would let a stale Max identity vouch for the seat that just signed in.
    /// Anything but Max is refused, including a tier that could not be read,
    /// since the operator's one address can carry both an Enterprise seat and a
    /// personal Max account and a login that cannot say which is no evidence
    /// that it was the second. The one exception is a folder that held a pair
    /// before this login: an unreadable tier there is no proof either way, and
    /// deleting what may be the earlier, admitted login is the greater harm, so
    /// that pair is kept and the operator told to remove the account instead.
    /// Nothing is adopted on that branch, which is why the judgment comes first:
    /// a folder whose credentials are kept keeps the profile and the residue that
    /// belong with them, rather than rewritten in the name of a login nothing could
    /// vouch for. The refused branch does adopt, deliberately, before the pair is
    /// discarded: if the delete fails, the rewritten profile names the refused
    /// seat instead of leaving a stale Max one beside a new pair.
    /// </para>
    /// </summary>
    private async Task<(LoginSessionState State, string Message)> AdmitAsync(Session session)
    {
        (MaxTierVerdict verdict, string? reason) =
            await ParkedFolderAdmission.JudgeFreshLoginAsync(session.Folder, _authStatus, CancellationToken.None);
        if (verdict == MaxTierVerdict.Unknown && session.CredentialFileDigestAtStart is not null)
        {
            return (LoginSessionState.Failed, UnjudgedKeptMessage);
        }

        bool adopted;
        try
        {
            adopted = await _profiles.AdoptFreshLoginAsync(session.Folder, CancellationToken.None);
        }
        catch (IOException)
        {
            // A file the CLI wrote seconds ago is still held by something else.
            adopted = false;
        }
        catch (UnauthorizedAccessException)
        {
            adopted = false;
        }

        if (verdict == MaxTierVerdict.Admitted)
        {
            return (LoginSessionState.Completed, adopted ? "Logged in as " + session.Email.Value + "." : ResidueKeptMessage);
        }

        return (LoginSessionState.Failed, RefusedPrefix + (reason ?? UnreadableTierReason) + await DiscardAsync(session));
    }

    /// <summary>
    /// Revokes and empties the folder of a login that turned out not to be a Max
    /// account, and says which of those two happened.
    /// <para>
    /// The revocation is the honest disposition for a seat that must never be
    /// rotated: the pair exists, and deleted bytes holding a refresh token are
    /// recoverable where a revoked token is not. It is not, however, what keeps
    /// the account out of the rotation. A switch admits any folder holding a
    /// credential file whatever its token is worth, so the file goes too, and it
    /// goes even when the revocation failed: an unrevoked token the operator has
    /// been told about is the smaller harm beside a switchable Enterprise seat.
    /// This is the deliberate opposite of the removal endpoint, which keeps the
    /// folder when a logout fails, because there nothing has yet been admitted.
    /// </para>
    /// </summary>
    private async Task<string> DiscardAsync(Session session)
    {
        // The failure text is not the outcome. The port's error can repeat what
        // the child printed, and that printout is the wrong thing to keep.
        Result<Unit, string> revoked = await _logout.LogoutAsync(session.Folder, CancellationToken.None);
        if (revoked.IsSuccess)
        {
            LogLogoutRevoked(session.Email.Value);
        }
        else
        {
            LogLogoutFailed(session.Email.Value);
        }

        try
        {
            await _profiles.DeleteFolderAsync(session.Folder, CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return NotDeletedSuffix;
        }

        return revoked.IsSuccess ? RevokedSuffix : NotRevokedSuffix;
    }

    /// <summary>
    /// The pending session that owns <paramref name="folder"/>, or null. Expiry
    /// is enforced first, so a session whose ten minutes ran out while nothing
    /// asked releases its folder here rather than holding it until something
    /// polls the session itself.
    /// </summary>
    private Session? RunningAgainst(string folder)
    {
        EvictFinishedSessions();
        foreach (Session running in _sessions.Values)
        {
            EnforceExpiry(running);
            if (running.State == LoginSessionState.Pending
                && string.Equals(running.Folder, folder, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                return running;
            }
        }

        return null;
    }

    private void EnforceExpiry(Session session)
    {
        if (session.State != LoginSessionState.Pending || _clock.GetUtcNow() < session.ExpiresAt)
        {
            return;
        }

        Settle(session, LoginSessionState.Expired, ExpiredMessage);
        Cancel(session);
    }

    private static void Cancel(Session session)
    {
        session.Child.Kill();
        session.Lifetime.Cancel();
    }

    private void Settle(Session session, LoginSessionState state, string? message, bool onlyWhilePending = false)
    {
        TaskCompletionSource? echo;
        bool scheduleEviction = false;
        LoginSessionState? recorded = null;
        lock (session.Sync)
        {
            LoginSessionState previous = session.State;
            if (onlyWhilePending && previous != LoginSessionState.Pending)
            {
                return;
            }

            if (previous == LoginSessionState.Pending && state != LoginSessionState.Pending)
            {
                session.FinishedAt = _clock.GetUtcNow();
                scheduleEviction = true;
                recorded = state;
            }
            else if (previous != LoginSessionState.Pending && state != LoginSessionState.Pending && previous != state)
            {
                // Expiry is recorded while the child is still being killed, and
                // the pump may then admit the pair that child wrote. The page
                // shows the later state, so the log names it too; otherwise the
                // trail says the login expired after the credentials were
                // accepted. The readable window already started on the way out
                // of pending, and a second timer is not armed.
                recorded = state;
            }

            session.State = state;
            session.Message = message;
            echo = session.Echo;
        }

        // The message the page shows is not the outcome: it is prose, and a
        // future wording must not be able to pull the code or a token onto
        // this line. A later settle that replaces one terminal state with
        // another records that later outcome as well.
        if (recorded == LoginSessionState.Completed)
        {
            LogLoginCompleted(session.Email.Value);
        }
        else if (recorded == LoginSessionState.Expired)
        {
            LogLoginExpired(session.Email.Value);
        }
        else if (recorded == LoginSessionState.Failed)
        {
            LogLoginFailed(session.Email.Value);
        }

        if (scheduleEviction)
        {
            // The page does not poll a finished login, so the readable window has
            // to end itself. The timer is the bound; a later request is not.
            session.Eviction = _clock.CreateTimer(
                _ => EvictWhenDue(session),
                null,
                CompletedSessionRetention,
                Timeout.InfiniteTimeSpan);
        }

        echo?.TrySetResult();
    }

    /// <summary>How many sessions are still held. Tests use it to see an eviction that no request triggered.</summary>
    internal int SessionCount => _sessions.Count;

    /// <summary>
    /// Drops sessions whose readable window has elapsed. Pending sessions are
    /// still logins, and a session whose reader has not returned is still
    /// finishing the folder, so neither is removed. A finished session also
    /// arms its own timer, so this sweep is not the only way the window ends.
    /// </summary>
    internal void EvictFinishedSessions()
    {
        DateTimeOffset now = _clock.GetUtcNow();
        foreach (Session session in _sessions.Values)
        {
            if (!IsReadyToEvict(session, now))
            {
                continue;
            }

            if (_sessions.TryRemove(session.Id.Value, out Session? removed))
            {
                removed.Dispose();
            }
        }
    }

    private static bool IsReadyToEvict(Session session, DateTimeOffset now)
    {
        lock (session.Sync)
        {
            if (session.State == LoginSessionState.Pending || session.FinishedAt is not DateTimeOffset finished)
            {
                return false;
            }

            return now - finished >= CompletedSessionRetention && session.Pump.IsCompleted;
        }
    }

    /// <summary>
    /// The readable window elapsed with nobody asking. If the reader is still
    /// finishing the folder, try once more when it returns; the window has
    /// already elapsed by then, and a pending session never gets here.
    /// </summary>
    private void EvictWhenDue(Session session)
    {
        if (Volatile.Read(ref _disposed) != 0 || !IsReadyToEvict(session, _clock.GetUtcNow()))
        {
            if (Volatile.Read(ref _disposed) == 0
                && !session.Pump.IsCompleted
                && Interlocked.Exchange(ref session.EvictWhenPumpCompletes, 1) == 0)
            {
                session.Pump.ContinueWith(
                    completed =>
                    {
                        _ = completed.Exception;
                        EvictWhenDue(session);
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default);
            }

            return;
        }

        if (_sessions.TryRemove(session.Id.Value, out Session? removed))
        {
            removed.Dispose();
        }
    }

    private static void WaitForPumps(IReadOnlyList<Session> sessions)
    {
        if (sessions.Count == 0)
        {
            return;
        }

        Task[] pumps = [.. sessions.Select(static session => session.Pump)];
        using CancellationTokenSource cancel = new();
        var all = Task.WhenAll(pumps);
        var delay = Task.Delay(DisposeWait, cancel.Token);
        Task.WhenAny(all, delay).GetAwaiter().GetResult();
        cancel.Cancel();
        try
        {
            // Observe the delay. It has either elapsed or just been cancelled,
            // and disposing the source first would race that observation.
            delay.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // The pumps finished inside the bound; the leftover wait was cancelled.
        }

        // A faulted pump must be observed here too. WhenAll aggregates those faults.
        ObserveFault(all);
        foreach (Task pump in pumps)
        {
            ObserveFault(pump);
        }
    }

    private static void ObserveFault(Task task)
    {
        if (task.IsFaulted)
        {
            _ = task.Exception;
            return;
        }

        if (!task.IsCompleted)
        {
            task.ContinueWith(
                static completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "login for {Account} stopped unexpectedly ({Failure})")]
    private partial void LogPumpFaulted(string account, string failure, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "login for {Account} could not be finished ({Failure})")]
    private partial void LogFinishFaulted(string account, string failure, Exception exception);

    // Audit lines: the account and the outcome, and nothing the child printed.
    // {Failure} above is the exception type name. These do not take the exception,
    // because its message can quote process output or the one-time code.
    [LoggerMessage(Level = LogLevel.Information, Message = "login for {Account} started")]
    private partial void LogLoginStarted(string account);

    [LoggerMessage(Level = LogLevel.Information, Message = "login for {Account} completed")]
    private partial void LogLoginCompleted(string account);

    [LoggerMessage(Level = LogLevel.Warning, Message = "login for {Account} failed")]
    private partial void LogLoginFailed(string account);

    [LoggerMessage(Level = LogLevel.Warning, Message = "login for {Account} expired")]
    private partial void LogLoginExpired(string account);

    [LoggerMessage(Level = LogLevel.Information, Message = "logout for {Account} revoked")]
    private partial void LogLogoutRevoked(string account);

    [LoggerMessage(Level = LogLevel.Warning, Message = "logout for {Account} failed")]
    private partial void LogLogoutFailed(string account);

    private static LoginSession Snapshot(Session session)
    {
        lock (session.Sync)
        {
            return new LoginSession(session.Id, session.Email, session.SignInUrl, session.State, session.Message, session.ExpiresAt);
        }
    }

    /// <summary>
    /// Waits on the CLI's answer, bounded by the reply budget on the injected
    /// clock and by the caller's own cancellation. A budget that runs out is
    /// not a failure: the session is still pending and the page reports it as
    /// such.
    /// </summary>
    private async Task WaitAsync(Task signal, CancellationToken cancellationToken)
    {
        using CancellationTokenSource budget = new(_replyBudget, _clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);
        try
        {
            await signal.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The CLI has not answered yet; the caller reports the session as it stands.
        }
    }

    /// <summary>One login in flight, with the child it drives and the buffer it reads.</summary>
    private sealed class Session : IDisposable
    {
        public Session(
            LoginSessionId id,
            AccountEmail email,
            string folder,
            ILoginChild child,
            string? credentialFileDigestAtStart,
            DateTimeOffset expiresAt,
            TimeSpan lifetime,
            TimeProvider clock)
        {
            Id = id;
            Email = email;
            Folder = folder;
            Child = child;
            CredentialFileDigestAtStart = credentialFileDigestAtStart;
            ExpiresAt = expiresAt;
            Lifetime = new CancellationTokenSource(lifetime, clock);
        }

        public LoginSessionId Id { get; }

        public AccountEmail Email { get; }

        public string Folder { get; }

        public ILoginChild Child { get; }

        /// <summary>
        /// The digest of the folder's credential file when the child was started,
        /// or null when there was none. The finish takes a file with the same
        /// digest for the earlier login, untouched, and any other for this
        /// login's own. That reading holds only while nothing else writes a
        /// parked pair between start and finish: the switch and the removal
        /// already refuse a folder a login is running against, and a parked-pair
        /// refresh write-back, when one exists, must check the same thing under
        /// the gate, or its rewrite would be judged as a login that never happened.
        /// </summary>
        public string? CredentialFileDigestAtStart { get; }

        public DateTimeOffset ExpiresAt { get; }

        /// <summary>
        /// When the session first left <see cref="LoginSessionState.Pending"/>, which is
        /// where the readable window is measured from. Null while the login is still running.
        /// </summary>
        public DateTimeOffset? FinishedAt { get; set; }

        /// <summary>Cancelled at expiry, at cancel, and at shutdown; unblocks the pump's read.</summary>
        public CancellationTokenSource Lifetime { get; }

        public object Sync { get; } = new();

        /// <summary>The child's output since the last code was submitted, scanned for the URL and the rejection.</summary>
        public StringBuilder Output { get; } = new();

        public Uri? SignInUrl { get; set; }

        public LoginSessionState State { get; set; } = LoginSessionState.Pending;

        public string? Message { get; set; }

        /// <summary>Completed once the URL is captured or the session ends.</summary>
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completed by the pump when the CLI answers the code being waited on.</summary>
        public TaskCompletionSource? Echo { get; set; }

        /// <summary>The reader task; awaited by the tests that assert on a finished login.</summary>
        public Task Pump { get; set; } = Task.CompletedTask;

        /// <summary>Fires once when the readable window ends, whether or not anyone asks again.</summary>
        public ITimer? Eviction { get; set; }

        /// <summary>Set once a due eviction has attached itself to a pump that is still finishing.</summary>
        public int EvictWhenPumpCompletes;

        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            Eviction?.Dispose();
            Child.Dispose();
            Lifetime.Dispose();
        }
    }
}
