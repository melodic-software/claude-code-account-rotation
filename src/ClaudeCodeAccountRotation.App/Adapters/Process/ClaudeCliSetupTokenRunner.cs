using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.App.Adapters.SecretStore;
using ClaudeCodeAccountRotation.App.Switching;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Accounts;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Ports;
using Microsoft.Extensions.Logging;

namespace ClaudeCodeAccountRotation.App.Adapters.Process;

/// <summary>One role a CI token session writes: its token secret, and its key secret when the key half goes too.</summary>
internal sealed record RoleTarget(CiRole Role, SecretTarget Token, SecretTarget? Key);

/// <summary>What the dashboard asks for when it generates a CI token: whose, and the roles it is to back, in order.</summary>
internal sealed record CiTokenRequest(AccountEmail Email, IReadOnlyList<RoleTarget> Roles)
{
    /// <summary>One token secret, set as named, with no key half: the 2.1.0 request.</summary>
    public CiTokenRequest(AccountEmail email, CiTokenSecret secret, OrgSecretVisibility? visibility, IReadOnlyList<string> repositories)
        : this(email, [new RoleTarget(CiRole.FromTokenSecret(secret), new SecretTarget(secret, visibility, repositories), Key: null)])
    {
    }
}

/// <summary>Runs <c>claude setup-token</c> sessions for the dashboard.</summary>
internal interface ICiTokenSessionRunner
{
    Task<Result<LoginSession, string>> StartAsync(CiTokenRequest request, CancellationToken cancellationToken);

    Task<Result<LoginSession, string>> SubmitCodeAsync(LoginSessionId id, string code, CancellationToken cancellationToken);

    LoginSession? Status(LoginSessionId id);
}

/// <summary>
/// Drives <c>claude setup-token</c> under a pseudo-terminal and hands the token
/// it prints to <c>gh secret set</c> in memory, once per role, with the
/// account's stored Console key going to each role's key secret beside it, then
/// records on the roster which halves were set from which account.
/// <para>
/// <c>setup-token</c> draws its interactive screen only when its standard input
/// is a terminal, so the child comes from a <see cref="LoginChildFactory"/> that
/// gives it one (<see cref="PseudoTerminalChild"/>). The sign-in itself is the
/// operator's, on Anthropic's own page in the account's mapped browser profile;
/// this class only reads the URL, types the code the operator pasted, and reads
/// the token.
/// </para>
/// <para>
/// The two things read from the child are matched by shape, not by the wording
/// around them: the authorize URL by its path, and the token by its
/// <c>sk-ant-oat01-</c> prefix and character set, with exactly one distinct
/// token required. Anything else (no token, two tokens, an error, the child
/// ending) fails closed with the manual commands in the message. The token is
/// never logged, returned, written to a file, or put on a command line; it is
/// kept only in the OS store, for Assign to role to send again: the output buffer
/// is cleared as soon as it is read, and the child is killed before
/// <c>gh</c> runs. Each session gets an empty config directory of its own,
/// deleted when the session ends, so the CLI's bookkeeping never touches an
/// account's folder.
/// </para>
/// </summary>
internal sealed partial class ClaudeCliSetupTokenRunner : ICiTokenSessionRunner, IDisposable
{
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(10);

    internal static readonly TimeSpan CompletedSessionRetention = SessionLifetime;

    /// <summary>The token lifetime <c>setup-token</c> asks for: one year.</summary>
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromDays(365);

    internal static readonly string[] Arguments = ["setup-token"];

    private const int MaximumCodeLength = 256;

    internal const string ManualFallback =
        " To do it by hand: run `claude setup-token`, sign in as the account, then `gh secret set <name> --org <org>` with the secret's current visibility (`--visibility all`, `--visibility private`, or `--repos a,b` for selected repositories, since gh otherwise makes it private) and paste the token.";

    private const string NoAnswerMessage = "claude setup-token did not answer the code, so the session was stopped rather than left waiting.";
    private const int ScreenTailLines = 6;
    private const int ScreenTailLength = 600;

    // The shortest run of the code's characters that marks a word as the code
    // echoed back: setup-token masks what is typed but its last six characters.
    private const int EchoedCodeRun = 6;
    private const string ExpiredMessage = "This CI token session expired after ten minutes. Start it again.";
    private const string EndedSentence = "claude setup-token ended without printing a token.";
    private const string EndedMessage = EndedSentence + ManualFallback;
    private const string AmbiguousMessage = "claude setup-token printed its token in a way that could not be read with certainty (more than one token, or one broken across rows), so none was used." + ManualFallback;
    private const string CancelledMessage = "The request that started this CI token session was cancelled. Start it again.";
    private const string NoUrlMessage = "claude setup-token printed no sign-in URL; run it by hand once to see what it says.";

    private readonly LoginChildFactory _start;
    private readonly ICiSecretWriter _secrets;
    private readonly ISecretStore _keys;
    private readonly CredentialMutationGate? _gate;
    private readonly TimeSpan _gateTimeout;
    private readonly RosterFile _roster;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _replyBudget;
    private readonly ILogger<ClaudeCliSetupTokenRunner> _logger;
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly Lock _admission = new();
    private int _disposed;

    public ClaudeCliSetupTokenRunner(
        LoginChildFactory start,
        ICiSecretWriter secrets,
        RosterFile roster,
        TimeProvider clock,
        ILogger<ClaudeCliSetupTokenRunner> logger,
        TimeSpan? replyBudget = null,
        ISecretStore? keys = null,
        CredentialMutationGate? gate = null,
        TimeSpan? gateTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _start = start;
        _secrets = secrets;
        _roster = roster;
        _clock = clock;
        _logger = logger;
        _replyBudget = replyBudget ?? TimeSpan.FromSeconds(30);
        _keys = keys ?? new UnavailableSecretStore("no OS store was given to read the key from");
        _gate = gate;
        _gateTimeout = gateTimeout ?? TimeSpan.FromSeconds(10);
    }

    public async Task<Result<LoginSession, string>> StartAsync(CiTokenRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Before any token exists: one that could not then be stored would be a
        // credential made for nothing.
        Result<Unit, string> writable = await _secrets.CheckAsync(cancellationToken);
        if (writable.IsFailure)
        {
            return Result<LoginSession, string>.Failure(writable.Error);
        }

        Session session;
        lock (_admission)
        {
            EvictFinishedSessions();
            // One at a time: two setup-token screens side by side would leave the
            // operator pasting a code into the wrong one.
            if (_sessions.Values.FirstOrDefault(static running => running.State == LoginSessionState.Pending) is Session running)
            {
                EnforceExpiry(running);
                if (running.State == LoginSessionState.Pending)
                {
                    return Result<LoginSession, string>.Failure(
                        "a CI token for " + running.Request.Email.Value + " is already being generated; finish it or let it expire first");
                }
            }

            string configDirectory = Directory.CreateTempSubdirectory("claude-code-account-rotation-setup-token-").FullName;
            Result<ILoginChild, string> child = _start(Arguments, configDirectory);
            if (child.IsFailure)
            {
                DeleteQuietly(configDirectory);
                return Result<LoginSession, string>.Failure(child.Error);
            }

            session = new Session(LoginSessionId.New(), request, configDirectory, child.Value, _clock.GetUtcNow() + SessionLifetime, _clock);
            _sessions[session.Id.Value] = session;
            session.Pump = Task.Run(() => PumpAsync(session), CancellationToken.None);
        }

        string secrets = string.Join(", ", request.Roles.Select(static role => role.Token.Secret.Name));
        LogStarted(request.Email.Value, secrets);
        try
        {
            await WaitAsync(session.Started.Task, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The page that asked never learns this session's id, so nothing
            // could finish it; left pending it would refuse every start for ten minutes.
            Settle(session, LoginSessionState.Failed, CancelledMessage);
            Cancel(session);
            throw;
        }

        if (session.SignInUrl is null)
        {
            Settle(session, LoginSessionState.Failed, NoUrlMessage);
            Cancel(session);
            return Result<LoginSession, string>.Failure(NoUrlMessage);
        }

        return Result<LoginSession, string>.Success(Snapshot(session));
    }

    public async Task<Result<LoginSession, string>> SubmitCodeAsync(LoginSessionId id, string code, CancellationToken cancellationToken)
    {
        EvictFinishedSessions();
        if (!_sessions.TryGetValue(id.Value, out Session? session))
        {
            return Result<LoginSession, string>.Failure("no CI token session with that id is running");
        }

        EnforceExpiry(session);
        if (session.State != LoginSessionState.Pending)
        {
            return Result<LoginSession, string>.Failure("that CI token session is no longer running; start a new one");
        }

        string trimmed = (code ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return Result<LoginSession, string>.Failure("paste the code from the browser first");
        }

        if (trimmed.Length > MaximumCodeLength)
        {
            return Result<LoginSession, string>.Failure("that is longer than any sign-in code; paste just the code");
        }

        if (trimmed.Any(char.IsControl))
        {
            return Result<LoginSession, string>.Failure("a sign-in code holds no line breaks or control characters");
        }

        lock (session.Sync)
        {
            // One code per session: setup-token answers a rejected code with a
            // retry screen, and this runner ends the session there instead.
            if (session.CodeSubmitted)
            {
                return Result<LoginSession, string>.Failure("a code was already submitted to this session; wait for its answer or start again");
            }

            session.CodeSubmitted = true;
            session.Code = trimmed;
            session.Output.Clear();
        }

        try
        {
            await session.Child.WriteCodeAsync(trimmed, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // The child ended between the state check and the write.
            Settle(session, LoginSessionState.Failed, EndedMessage);
            Cancel(session);
            return Result<LoginSession, string>.Failure(EndedMessage);
        }

        await WaitAsync(session.Finished.Task, cancellationToken);
        bool unanswered;
        lock (session.Sync)
        {
            // No token, no error and no exit within the reply budget: what the
            // child shows now is all the operator can act on, so it ends here
            // with that screen rather than waiting out the session's lifetime.
            // Checked and settled under one lock, so a token read meanwhile
            // (which sets Delivering under it too) is never cut off.
            unanswered = session.State == LoginSessionState.Pending && !session.Delivering;
            if (unanswered)
            {
                Settle(session, LoginSessionState.Failed, NoAnswerMessage + Said(ScreenTail(Visible(session.Output.ToString()), session.Code)) + ManualFallback);
            }
        }

        if (unanswered)
        {
            Cancel(session);
        }

        return Result<LoginSession, string>.Success(Snapshot(session));
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

    /// <summary>Completes once the session's pump has returned. Tests await it.</summary>
    internal Task FinishedAsync(LoginSessionId id) =>
        _sessions.TryGetValue(id.Value, out Session? session)
            ? session.Pump
            : throw new ArgumentException("no CI token session " + id.Value, nameof(id));

    internal int SessionCount => _sessions.Count;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (Session session in _sessions.Values)
        {
            Cancel(session);
            session.Pump.Wait(TimeSpan.FromSeconds(2));
            session.Dispose();
        }

        _sessions.Clear();
    }

    /// <summary>
    /// The text a person would read on the terminal, with its control sequences
    /// replaced by what they do to the layout. A sequence that moves to another
    /// row or erases (cursor position, up, down, erase in line or display)
    /// becomes a line break, and one that moves along the row becomes a space:
    /// a console repainting its screen reaches the next row by moving the
    /// cursor rather than printing a line break, and dropping that move would
    /// glue the token to the word after it. Colors, hyperlink wrappers and
    /// other controls are dropped.
    /// </summary>
    internal static string Visible(string text) => ControlSequencePattern().Replace(text, static match =>
    {
        string sequence = match.Value;
        if (!sequence.StartsWith("\u001b[", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return sequence[^1] switch
        {
            'H' or 'f' or 'd' or 'A' or 'B' or 'E' or 'F' or 'J' or 'K' => "\n",
            'C' or 'G' or 'X' => " ",
            _ => string.Empty,
        };
    });

    /// <summary>
    /// Every distinct token in the visible text, each standing alone between
    /// whitespace: a token is taken only once the whitespace after it has
    /// arrived, so one split across two reads is never taken for a shorter one,
    /// and one run into other text is not taken at all.
    /// </summary>
    internal static IReadOnlyList<string> ExtractTokens(string visible) =>
        [.. TokenPattern().Matches(visible).Select(static match => match.Value).Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// Whether any token in the visible text looks wrapped: the row after it
    /// holds nothing but token characters, which is what the rest of a token
    /// cut at the terminal's edge looks like. The terminal is wide enough that
    /// this should never happen; if it does, the token is refused, not guessed at.
    /// </summary>
    internal static bool AnyTokenLooksWrapped(string visible) =>
        TokenPattern().Matches(visible).Any(match => WrappedTailPattern().IsMatch(visible, match.Index + match.Length));

    /// <summary>
    /// The first whole OAuth authorize URL in the visible text: followed by
    /// whitespace, so one split across two reads is never taken for the shorter
    /// one in the first, and carrying the <c>code_challenge</c> and
    /// <c>state</c> parameters a sign-in cannot complete without, so a URL cut
    /// short is refused rather than opened.
    /// </summary>
    internal static Uri? ExtractAuthorizeUrl(string visible)
    {
        foreach (Match match in CompleteUrlPattern().Matches(visible))
        {
            if (Uri.TryCreate(match.Value, UriKind.Absolute, out Uri? url)
                && url.AbsolutePath.EndsWith("/oauth/authorize", StringComparison.Ordinal)
                && HasParameter(url, "code_challenge")
                && HasParameter(url, "state"))
            {
                return url;
            }
        }

        return null;
    }

    /// <summary>
    /// The last rows of the visible text, for a message the operator reads,
    /// with every word that could be a secret replaced: anything token-shaped
    /// (the prefix, or a long run of token characters, which is what a token
    /// still arriving or cut at a row's edge looks like) and the submitted
    /// code wherever it is echoed back.
    /// </summary>
    internal static string ScreenTail(string visible, string? code)
    {
        IEnumerable<string> rows = visible.Split('\n')
            .Select(static row => row.Trim())
            .Where(static row => row.Length > 0)
            .TakeLast(ScreenTailLines)
            .Select(row => string.Join(' ', row.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(word => Redact(word, code))));
        string tail = string.Join(" / ", rows);
        return tail.Length <= ScreenTailLength ? tail : "..." + tail[^ScreenTailLength..];
    }

    private static string Redact(string word, string? code)
    {
        string bare = word.TrimStart('*');
        bool secret = bare.Contains("sk-ant", StringComparison.OrdinalIgnoreCase)
            || LongTokenRunPattern().IsMatch(bare)
            || (code is not null && SharesRun(bare, code));
        return secret ? word[..(word.Length - bare.Length)] + "[redacted]" : word;
    }

    private static bool SharesRun(string word, string code)
    {
        for (int start = 0; start + EchoedCodeRun <= word.Length; start++)
        {
            if (code.Contains(word.AsSpan(start, EchoedCodeRun), StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string Said(string tail) => tail.Length == 0 ? " It showed nothing after the code." : " Its screen ended with: \"" + tail + "\".";

    private static bool HasParameter(Uri url, string name) =>
        url.Query.TrimStart('?').Split('&').Any(pair => pair.StartsWith(name + "=", StringComparison.Ordinal) && pair.Length > name.Length + 1);

    private async Task PumpAsync(Session session)
    {
        try
        {
            await ReadLoopAsync(session);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // The child was killed or its pipes closed; the settle below says how it ended.
        }
#pragma warning disable CA1031 // Do not catch general exception types
        catch (Exception exception)
#pragma warning restore CA1031
        {
            LogPumpFaulted(session.Request.Email.Value, exception.GetType().Name, exception);
        }
        finally
        {
            session.Child.Kill();
            bool expired = _clock.GetUtcNow() >= session.ExpiresAt;
            Settle(session, expired ? LoginSessionState.Expired : LoginSessionState.Failed, expired ? ExpiredMessage : EndedMessage);
            session.Started.TrySetResult();
            DeleteQuietly(session.ConfigDirectory);
        }
    }

    private async Task ReadLoopAsync(Session session)
    {
        while (true)
        {
            string? chunk = await session.Child.ReadAsync(session.Lifetime.Token);
            if (chunk is null)
            {
                string? last;
                lock (session.Sync)
                {
                    last = session.CodeSubmitted ? ScreenTail(Visible(session.Output.ToString()), session.Code) : null;
                }

                // Before a code the pump's own settle says how it ended.
                if (last is not null)
                {
                    Settle(session, LoginSessionState.Failed, EndedSentence + Said(last) + ManualFallback);
                }

                return;
            }

            string visible;
            bool codeSubmitted;
            lock (session.Sync)
            {
                session.Output.Append(chunk);
                visible = Visible(session.Output.ToString());
                codeSubmitted = session.CodeSubmitted;
            }

            if (session.SignInUrl is null && ExtractAuthorizeUrl(visible) is Uri url)
            {
                session.SignInUrl = SignInUrl.WithLoginHint(url, session.Request.Email);
                session.Started.TrySetResult();
            }

            if (!codeSubmitted)
            {
                continue;
            }

            IReadOnlyList<string> tokens = ExtractTokens(visible);
            if (tokens.Count > 0)
            {
                lock (session.Sync)
                {
                    session.Output.Clear();
                }

                // The child has printed what it is for; nothing it does next is wanted.
                session.Child.Kill();
                if (tokens.Count > 1 || AnyTokenLooksWrapped(visible))
                {
                    Settle(session, LoginSessionState.Failed, AmbiguousMessage);
                    return;
                }

                lock (session.Sync)
                {
                    // A reply budget that ran out first has already ended the
                    // session as failed; a secret written now would contradict it.
                    if (session.State != LoginSessionState.Pending)
                    {
                        return;
                    }

                    // From here the secret may be written, so the session ends as
                    // what that write did, never as an expiry noticed meanwhile.
                    session.Delivering = true;
                }

                await DeliverAsync(session, tokens[0]);
                return;
            }

            // After the code, any word of failure ends the session on the
            // child's own answer: the retry screen ("OAuth error ... Press Enter
            // to retry") and whatever other refusal a later CLI words differently.
            if (FailureWordPattern().IsMatch(visible))
            {
                string said;
                lock (session.Sync)
                {
                    said = ScreenTail(visible, session.Code);
                }

                Settle(session, LoginSessionState.Failed, "That code was rejected; claude setup-token said: \"" + said + "\". Start again and paste the whole code from the browser." + ManualFallback);
                return;
            }

            if (_clock.GetUtcNow() >= session.ExpiresAt)
            {
                EnforceExpiry(session);
                return;
            }
        }
    }

    /// <summary>
    /// Sets each role's token secret, then its key secret, one at a time, then
    /// marks on the roster the halves that were set. A failed half does not stop
    /// the rest, except a rate limit, which stops every write after it. The token
    /// and the key go nowhere else.
    /// </summary>
    private async Task DeliverAsync(Session session, string token)
    {
        CiTokenRequest request = session.Request;
        // Kept in the OS store first, so a token no secret took can still be
        // sent with Assign to role; a store that refuses stops nothing.
        Result<Unit, string> kept = await _keys.WriteAsync(SecretKind.CiToken, request.Email, token, CancellationToken.None);
        if (kept.IsFailure)
        {
            LogTokenNotKept(request.Email.Value);
        }

        string keptNote = kept.IsSuccess ? string.Empty : " The token could not be kept in the OS store (" + kept.Error.TrimEnd('.') + "), so Assign to role cannot send it again.";
        (string? key, string? keyError) = request.Roles.Any(static role => role.Key is not null)
            ? await ReadKeyAsync(request.Email)
            : (null, null);

        List<(RoleTarget Role, bool IsKey, DateTimeOffset SetAt)> set = [];
        List<(RoleTarget Role, bool IsKey, string Error)> failed = [];
        string? stop = null;
        foreach ((RoleTarget role, bool isKey, SecretTarget target) in request.Roles.SelectMany(static role => role.Key is SecretTarget keyTarget
            ? new[] { (role, false, role.Token), (role, true, keyTarget) }
            : [(role, false, role.Token)]))
        {
            string? value = isKey ? key : token;
            Result<Unit, string> written = stop is not null ? Result<Unit, string>.Failure(stop)
                : value is null ? Result<Unit, string>.Failure(keyError!)
                : await _secrets.SetAsync(target.Secret, target.Visibility, target.Repositories, value, CancellationToken.None);
            if (written.IsSuccess)
            {
                LogSecretSet(request.Email.Value, target.Secret.Name);
                set.Add((role, isKey, _clock.GetUtcNow()));
                continue;
            }

            LogSecretFailed(request.Email.Value, target.Secret.Name);
            failed.Add((role, isKey, written.Error));
            if (stop is null && written.Error == GhSecretWriter.RateLimitMessage)
            {
                stop = "not sent, because an earlier write hit GitHub's rate limit";
            }
        }

        // Under the credential gate, so a Store or Delete CI token clicked
        // while this ran cannot land between the check and the record: the
        // token is recorded only when the store still holds this one.
        using IDisposable? permit = kept.IsSuccess ? await TryHoldGateAsync() : null;
        bool stillKept = kept.IsSuccess
            && await _keys.ReadAsync(SecretKind.CiToken, request.Email, CancellationToken.None) is { IsSuccess: true, Value: string held }
            && string.Equals(held, token, StringComparison.Ordinal);
        if (kept.IsSuccess && !stillKept)
        {
            keptNote = " The stored CI token was replaced or deleted while this ran, so this one is not kept.";
        }

        var today = DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);
        if (set.Count == 0)
        {
            bool recorded = stillKept && await RecordKeptAsync(request.Email, today);
            Settle(
                session,
                LoginSessionState.Failed,
                "The token was made but could not be stored: " + (failed.Count == 1 ? failed[0].Error.TrimEnd('.') : NotSet(failed, holders: null).TrimEnd('.'))
                + (recorded
                    ? ". It is kept in this machine's OS store; send it with Assign to role once that is fixed."
                    : ". The token was not kept anywhere, so it can never be used; start again once that is fixed."));
            return;
        }

        string setNames = string.Join(", ", set.Select(static half => Secret(half.Role, half.IsKey).ToString()));
        string tokenNames = string.Join("; ", set.Where(static half => !half.IsKey).Select(static half => half.Role.Token.Secret.ToString()));
        Dictionary<(RoleTarget, bool), string> holders = [];
        bool removed = false;
        try
        {
            await _roster.UpdateAsync(
                roster =>
                {
                    // An account removed while its session ran is not put back,
                    // but each half it set is still taken off the account that
                    // held it: that secret no longer holds their value.
                    RosterEntry? found = roster.Find(request.Email);
                    removed = found is null;
                    RosterEntry entry = found ?? new RosterEntry(request.Email);

                    // Who set each half that was not set this time: that secret
                    // still holds their value, so the message names them.
                    foreach ((RoleTarget role, bool isKey, _) in failed)
                    {
                        if (roster.Entries.FirstOrDefault(other => other.Email != request.Email && other.CiRoles.Any(held => held.Role.Equals(role.Role) && (isKey ? held.HasKey : held.HasToken))) is RosterEntry holder)
                        {
                            holders[(role, isKey)] = holder.Email.Value;
                        }
                    }

                    foreach ((RoleTarget role, bool isKey, DateTimeOffset setAt) in set)
                    {
                        entry = isKey ? entry.ClaimingKey(role.Role, today, setAt) : entry.ClaimingToken(role.Role, today, setAt);
                    }

                    if (stillKept)
                    {
                        entry = entry with { CiTokenStoredOn = today };
                    }

                    // Claimed by halves: a half not set this time still holds its
                    // holder's value, and stays theirs.
                    return removed ? roster.With(entry).Without(request.Email) : roster.With(entry);
                },
                CancellationToken.None,
                via: LaneVia.Assign);
        }
#pragma warning disable CA1031 // Do not catch general exception types
        catch (Exception)
#pragma warning restore CA1031
        {
            // A kept token the roster does not record is one the page cannot
            // show or delete, so it goes back out of the store.
            if (stillKept)
            {
                _ = await _keys.DeleteAsync(SecretKind.CiToken, request.Email, CancellationToken.None);
                keptNote = " The token was not kept in the OS store.";
            }

            // The secrets are set whatever went wrong here, and the session must
            // say so rather than fall through to "no token was printed".
            Settle(
                session,
                failed.Count == 0 ? LoginSessionState.Completed : LoginSessionState.Failed,
                "Set " + setNames + ", but the roster could not be updated, so the card does not show it yet."
                + (tokenNames.Length == 0 ? string.Empty : " On this card open Edit, set \"CI token generated on\" to "
                    + today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " and the CI secret name and where to each of these in turn, saving each: " + tokenNames + ".")
                + (failed.Count == 0 ? string.Empty : " " + NotSet(failed, holders: null)) + keptNote);
            return;
        }

        if (removed && stillKept)
        {
            _ = await _keys.DeleteAsync(SecretKind.CiToken, request.Email, CancellationToken.None);
            keptNote = string.Empty;
        }

        string expires = " The token expires around " + today.AddDays((int)TokenLifetime.TotalDays).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".";
        string outcome = removed
            ? "Set " + setNames + " from " + request.Email.Value + ", but the roster was not updated because that account was removed while this ran; no account on the roster backs those halves now."
            : "Set " + setNames + " from " + request.Email.Value + ".";
        Settle(
            session,
            failed.Count == 0 ? LoginSessionState.Completed : LoginSessionState.Failed,
            outcome + (failed.Count == 0 ? string.Empty : " " + NotSet(failed, holders, request.Email.Value)) + expires + keptNote);
    }

    /// <summary>The credential gate, or null when none was given or it stays busy past the timeout.</summary>
    private async Task<IDisposable?> TryHoldGateAsync()
    {
        if (_gate is null)
        {
            return null;
        }

        try
        {
            return await _gate.AcquireAsync(_gateTimeout, CancellationToken.None);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    /// <summary>
    /// Records the day a token no secret took was kept, so the page shows it.
    /// An account gone from the roster, or a roster that cannot be written,
    /// takes the token back out of the store.
    /// </summary>
    private async Task<bool> RecordKeptAsync(AccountEmail email, DateOnly today)
    {
        bool recorded = false;
        try
        {
            await _roster.UpdateAsync(
                roster =>
                {
                    if (roster.Find(email) is not RosterEntry entry)
                    {
                        return roster;
                    }

                    recorded = true;
                    return roster.With(entry with { CiTokenStoredOn = today });
                },
                CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            recorded = false;
        }

        if (!recorded)
        {
            _ = await _keys.DeleteAsync(SecretKind.CiToken, email, CancellationToken.None);
        }

        return recorded;
    }

    /// <summary>The account's stored Console key, or why there is none.</summary>
    private async Task<(string? Key, string? Error)> ReadKeyAsync(AccountEmail email)
    {
        Result<string?, string> read = await _keys.ReadAsync(SecretKind.ConsoleApiKey, email, CancellationToken.None);
        return read.IsFailure ? (null, "the OS store could not read this account's Console key: " + read.Error)
            : read.Value is null ? (null, "this account has no Console key in the OS store; paste it with Set API key")
            : (read.Value, null);
    }

    private static CiTokenSecret Secret(RoleTarget role, bool isKey) => isKey ? role.Key!.Secret : role.Token.Secret;

    /// <summary>
    /// Every half that was not set, with why. A role whose other half was set is
    /// split, and when another account set the half that failed, it is named:
    /// its secret still holds that account's value.
    /// </summary>
    private static string NotSet(List<(RoleTarget Role, bool IsKey, string Error)> failed, Dictionary<(RoleTarget, bool), string>? holders, string? email = null) =>
        "Not set: " + string.Join("; ", failed.Select(half =>
        {
            string line = half.Role.Role.Name + " " + (half.IsKey ? "key" : "token") + " (" + Secret(half.Role, half.IsKey) + "): " + half.Error.TrimEnd('.');
            bool split = half.Role.Key is not null && !failed.Any(other => other.Role == half.Role && other.IsKey != half.IsKey);
            if (!split || email is null)
            {
                return line;
            }

            string other = holders is not null && holders.TryGetValue((half.Role, half.IsKey), out string? holder) ? "from " + holder : "not from any account on the roster";
            return line + ". The role is split: " + (half.IsKey ? "token from " + email + ", key " + other : "token " + other + ", key from " + email)
                + "; " + (half.IsKey ? "re-send the key with Push API key" : "re-send the token with Generate CI token");
        })) + ".";

    private void EnforceExpiry(Session session)
    {
        lock (session.Sync)
        {
            if (session.State != LoginSessionState.Pending || session.Delivering || _clock.GetUtcNow() < session.ExpiresAt)
            {
                return;
            }
        }

        Settle(session, LoginSessionState.Expired, ExpiredMessage);
        Cancel(session);
    }

    private static void Cancel(Session session)
    {
        session.Child.Kill();
        session.Lifetime.Cancel();
    }

    private void Settle(Session session, LoginSessionState state, string message)
    {
        lock (session.Sync)
        {
            // The first terminal state is the one kept: the pump's closing settle
            // runs after every other and must not overwrite how the session ended.
            if (session.State != LoginSessionState.Pending)
            {
                return;
            }

            session.State = state;
            session.Message = message;
            session.FinishedAt = _clock.GetUtcNow();
        }

        if (state == LoginSessionState.Failed)
        {
            LogFailed(session.Request.Email.Value);
        }
        else if (state == LoginSessionState.Expired)
        {
            LogExpired(session.Request.Email.Value);
        }

        session.Finished.TrySetResult();
    }

    private void EvictFinishedSessions()
    {
        DateTimeOffset now = _clock.GetUtcNow();
        foreach (Session session in _sessions.Values)
        {
            bool due;
            lock (session.Sync)
            {
                due = session.FinishedAt is DateTimeOffset finished && now - finished >= CompletedSessionRetention && session.Pump.IsCompleted;
            }

            if (due && _sessions.TryRemove(session.Id.Value, out Session? removed))
            {
                removed.Dispose();
            }
        }
    }

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
            // Still running; the caller reports the session as it stands.
        }
    }

    private static LoginSession Snapshot(Session session)
    {
        lock (session.Sync)
        {
            return new LoginSession(session.Id, session.Request.Email, session.SignInUrl, session.State, session.Message, session.ExpiresAt);
        }
    }

    private static void DeleteQuietly(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A temp directory the CLI still held; the OS temp cleanup has it.
        }
    }

    // CSI (ESC [ ... final), OSC (ESC ] ... BEL or ESC \), other two-byte
    // escapes, and the C0/C1 controls a person never reads.
    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]|\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)?|\x1B[@-Z\\-_]|[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex ControlSequencePattern();

    // The prefix setup-token's tokens carry, then the base64url alphabet, alone
    // between whitespace (or the start of the text) and the whitespace after it.
    [GeneratedRegex(@"(?<![^\s])sk-ant-oat01-[A-Za-z0-9_-]{32,}(?=\s)", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex TokenPattern();

    // From the end of a token: the line break, then a row that is one run of
    // token characters and nothing else.
    [GeneratedRegex(@"\G[ \t]*\r?\n[ \t]*[A-Za-z0-9_-]+[ \t]*(?:\r?\n|$)", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex WrappedTailPattern();

    [GeneratedRegex(@"https://[^\s\p{Cc}""'<>]+(?=\s)", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex CompleteUrlPattern();

    [GeneratedRegex(@"[A-Za-z0-9_-]{24,}", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex LongTokenRunPattern();

    [GeneratedRegex(@"\b(?:error|failed|failure|invalid|expired|denied|unauthorized|retry)\b", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex FailureWordPattern();

    // Audit lines name the account and the secret, never the token or the code.
    [LoggerMessage(Level = LogLevel.Information, Message = "CI token for {Account} started (secret {Secret})")]
    private partial void LogStarted(string account, string secret);

    [LoggerMessage(Level = LogLevel.Information, Message = "CI token for {Account} set as secret {Secret}")]
    private partial void LogSecretSet(string account, string secret);

    [LoggerMessage(Level = LogLevel.Warning, Message = "CI token for {Account} could not be stored as secret {Secret}")]
    private partial void LogSecretFailed(string account, string secret);

    [LoggerMessage(Level = LogLevel.Warning, Message = "CI token for {Account} could not be kept in the OS store")]
    private partial void LogTokenNotKept(string account);

    [LoggerMessage(Level = LogLevel.Warning, Message = "CI token for {Account} failed")]
    private partial void LogFailed(string account);

    [LoggerMessage(Level = LogLevel.Warning, Message = "CI token for {Account} expired")]
    private partial void LogExpired(string account);

    [LoggerMessage(Level = LogLevel.Error, Message = "CI token for {Account} stopped unexpectedly ({Failure})")]
    private partial void LogPumpFaulted(string account, string failure, Exception exception);

    private sealed class Session : IDisposable
    {
        public Session(LoginSessionId id, CiTokenRequest request, string configDirectory, ILoginChild child, DateTimeOffset expiresAt, TimeProvider clock)
        {
            Id = id;
            Request = request;
            ConfigDirectory = configDirectory;
            Child = child;
            ExpiresAt = expiresAt;
            Lifetime = new CancellationTokenSource(SessionLifetime, clock);
        }

        public LoginSessionId Id { get; }

        public CiTokenRequest Request { get; }

        public string ConfigDirectory { get; }

        public ILoginChild Child { get; }

        public DateTimeOffset ExpiresAt { get; }

        public CancellationTokenSource Lifetime { get; }

        public object Sync { get; } = new();

        public StringBuilder Output { get; } = new();

        public Uri? SignInUrl { get; set; }

        public bool CodeSubmitted { get; set; }

        /// <summary>The code typed in, held only to keep it out of any screen text shown back.</summary>
        public string? Code { get; set; }

        /// <summary>Set once a token was read and the secret write may have begun.</summary>
        public bool Delivering { get; set; }

        public LoginSessionState State { get; set; } = LoginSessionState.Pending;

        public string? Message { get; set; }

        public DateTimeOffset? FinishedAt { get; set; }

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Pump { get; set; } = Task.CompletedTask;

        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            Child.Dispose();
            Lifetime.Dispose();
        }
    }
}
