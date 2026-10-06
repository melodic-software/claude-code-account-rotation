using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Accounts;
using ClaudeCodeAccountRotation.Core.Identity;
using Microsoft.Extensions.Logging;

namespace ClaudeCodeAccountRotation.App.Adapters.Process;

/// <summary>What the dashboard asks for when it generates a CI token: whose, and where it goes.</summary>
internal sealed record CiTokenRequest(
    AccountEmail Email,
    CiTokenSecret Secret,
    OrgSecretVisibility? Visibility,
    IReadOnlyList<string> Repositories);

/// <summary>Runs <c>claude setup-token</c> sessions for the dashboard.</summary>
internal interface ICiTokenSessionRunner
{
    Task<Result<LoginSession, string>> StartAsync(CiTokenRequest request, CancellationToken cancellationToken);

    Task<Result<LoginSession, string>> SubmitCodeAsync(LoginSessionId id, string code, CancellationToken cancellationToken);

    LoginSession? Status(LoginSessionId id);
}

/// <summary>
/// Drives <c>claude setup-token</c> under a pseudo-terminal and hands the token
/// it prints to <c>gh secret set</c> in memory, then records which account backs
/// which secret on the roster.
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
/// never logged, returned, stored, or put on a command line: the output buffer
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
        " To do it by hand: run `claude setup-token`, sign in as the account, then `gh secret set <name> --repo <owner/name>` (or `--org <org>`) and paste the token.";

    private const string RejectedMessage = "That code was rejected. Start again and paste the whole code from the browser." + ManualFallback;
    private const string ExpiredMessage = "This CI token session expired after ten minutes. Start it again.";
    private const string EndedMessage = "claude setup-token ended without printing a token." + ManualFallback;
    private const string AmbiguousMessage = "claude setup-token printed its token in a way that could not be read with certainty (more than one token, or one broken across rows), so none was used." + ManualFallback;
    private const string CancelledMessage = "The request that started this CI token session was cancelled. Start it again.";
    private const string NoUrlMessage = "claude setup-token printed no sign-in URL; run it by hand once to see what it says.";

    private readonly LoginChildFactory _start;
    private readonly ICiSecretWriter _secrets;
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
        TimeSpan? replyBudget = null)
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

        LogStarted(request.Email.Value, request.Secret.Name);
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
                session.SignInUrl = url;
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
                    // From here the secret may be written, so the session ends as
                    // what that write did, never as an expiry noticed meanwhile.
                    session.Delivering = true;
                }

                await DeliverAsync(session, tokens[0]);
                return;
            }

            if (visible.Contains("OAuth error", StringComparison.OrdinalIgnoreCase)
                || visible.Contains("to retry", StringComparison.OrdinalIgnoreCase))
            {
                Settle(session, LoginSessionState.Failed, RejectedMessage);
                return;
            }

            if (_clock.GetUtcNow() >= session.ExpiresAt)
            {
                EnforceExpiry(session);
                return;
            }
        }
    }

    /// <summary>Sets the secret, then marks the account on the roster. The token goes nowhere else.</summary>
    private async Task DeliverAsync(Session session, string token)
    {
        CiTokenRequest request = session.Request;
        Result<Unit, string> set = await _secrets.SetAsync(request.Secret, request.Visibility, request.Repositories, token, CancellationToken.None);
        if (set.IsFailure)
        {
            LogSecretFailed(request.Email.Value, request.Secret.Name);
            Settle(
                session,
                LoginSessionState.Failed,
                "The token was made but could not be stored: " + set.Error
                + ". The token was not kept anywhere, so it can never be used; start again once that is fixed.");
            return;
        }

        var today = DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);
        try
        {
            await _roster.UpdateAsync(
                roster => roster.Find(request.Email) is RosterEntry entry
                    ? roster.With(entry with { CiTokenGeneratedOn = today, CiTokenSecret = request.Secret })
                    : roster,
                CancellationToken.None);
        }
#pragma warning disable CA1031 // Do not catch general exception types
        catch (Exception)
#pragma warning restore CA1031
        {
            // The secret is set whatever went wrong here, and the session must
            // say so rather than fall through to "no token was printed".
            LogSecretSet(request.Email.Value, request.Secret.Name);
            Settle(
                session,
                LoginSessionState.Completed,
                "Set " + request.Secret + ", but the roster could not be updated, so the card does not show it yet. On this card open Edit, set"
                + " \"CI token generated on\" to " + today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ", the CI secret name to "
                + request.Secret.Name + " and where to " + request.Secret.Owner + ", then Save.");
            return;
        }

        LogSecretSet(request.Email.Value, request.Secret.Name);
        Settle(
            session,
            LoginSessionState.Completed,
            "Set " + request.Secret + " from " + request.Email.Value + ". The token expires around "
            + today.AddDays((int)TokenLifetime.TotalDays).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".");
    }

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

    // Audit lines name the account and the secret, never the token or the code.
    [LoggerMessage(Level = LogLevel.Information, Message = "CI token for {Account} started (secret {Secret})")]
    private partial void LogStarted(string account, string secret);

    [LoggerMessage(Level = LogLevel.Information, Message = "CI token for {Account} set as secret {Secret}")]
    private partial void LogSecretSet(string account, string secret);

    [LoggerMessage(Level = LogLevel.Warning, Message = "CI token for {Account} could not be stored as secret {Secret}")]
    private partial void LogSecretFailed(string account, string secret);

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
