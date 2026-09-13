using System.Text.Json;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Ports;
using ClaudeCodeAccountRotation.Core.Quota;

namespace ClaudeCodeAccountRotation.App.Tests.Quota;

/// <summary>
/// The usage endpoint as a script. Each answer is queued or produced by call
/// number, and every access token it was handed is kept, which is how a test
/// says "the retry went out with the rotated token, not the dead one".
/// <para>
/// Hand-written rather than a mocking library's: the one pinned centrally is
/// referenced by no test project, and adding it would touch the project file
/// and its lock file.
/// </para>
/// </summary>
internal sealed class ScriptedUsage : IUsageEndpointClient
{
    public List<string> AccessTokens { get; } = [];

    public Queue<Result<JsonDocument, UsageReadFailure>> Answers { get; } = new();

    /// <summary>Answers by call number (one-based) once the queue is empty.</summary>
    public Func<int, Result<JsonDocument, UsageReadFailure>>? AnswerAt { get; set; }

    /// <summary>Runs inside the read, before it answers: the seam a test uses to change the world mid-pass.</summary>
    public Action? BeforeAnswer { get; set; }

    /// <summary>
    /// Set when a call arrived that the test never scripted. Recorded rather
    /// than thrown, because the engine's per-account catch turns any exception
    /// from here into a read failure: a test with a gap in its script would
    /// otherwise pass while proving nothing about the engine. The harness
    /// asserts this on dispose, so the gap fails the test that has it.
    /// </summary>
    public bool Unscripted { get; private set; }

    public int Calls => AccessTokens.Count;

    public static Result<JsonDocument, UsageReadFailure> Ok(string body = """{"limits":[{"kind":"session","percent":43,"is_active":true}]}""") =>
        Result<JsonDocument, UsageReadFailure>.Success(JsonDocument.Parse(body));

    public static Result<JsonDocument, UsageReadFailure> Failed(UsageReadFailureKind kind, TimeSpan? retryAfter = null) =>
        Result<JsonDocument, UsageReadFailure>.Failure(new UsageReadFailure(kind, "scripted " + kind, retryAfter));

    public Task<Result<JsonDocument, UsageReadFailure>> ReadUsageAsync(string accessToken, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AccessTokens.Add(accessToken);
        // After the token check, not before: a test that cancels from here is
        // asserting what the pass does between accounts, so this read still answers.
        BeforeAnswer?.Invoke();
        if (Answers.Count > 0)
        {
            return Task.FromResult(Answers.Dequeue());
        }

        if (AnswerAt is not null)
        {
            return Task.FromResult(AnswerAt(AccessTokens.Count));
        }

        Unscripted = true;
        return Task.FromResult(Failed(UsageReadFailureKind.Transport));
    }
}

/// <summary>The token endpoint as a script, keeping every refresh token it was asked to rotate.</summary>
internal sealed class ScriptedTokens : ITokenRefreshClient
{
    public List<string> RefreshTokens { get; } = [];

    public Queue<Result<RefreshedTokens, UsageReadFailure>> Answers { get; } = new();

    public Func<int, Result<RefreshedTokens, UsageReadFailure>>? AnswerAt { get; set; }

    /// <summary>Set when a token refresh arrived that the test never scripted; asserted by the harness on dispose.</summary>
    public bool Unscripted { get; private set; }

    public int Calls => RefreshTokens.Count;

    /// <summary>
    /// A rotation in the endpoint's own shape. The tokens carry the
    /// <c>refresh-</c> and <c>access-</c> prefixes every secrets assertion in this
    /// repository looks for, so a test that leaks one fails rather than passing
    /// quietly.
    /// </summary>
    public static Result<RefreshedTokens, UsageReadFailure> Rotated(
        string suffix,
        DateTimeOffset accessTokenExpiresAt,
        DateTimeOffset? loginExpiresAt) =>
        Result<RefreshedTokens, UsageReadFailure>.Success(
            new RefreshedTokens("access-" + suffix, "refresh-" + suffix, accessTokenExpiresAt, loginExpiresAt, ["user:inference"]));

    public static Result<RefreshedTokens, UsageReadFailure> Failed(UsageReadFailureKind kind, TimeSpan? retryAfter = null) =>
        Result<RefreshedTokens, UsageReadFailure>.Failure(new UsageReadFailure(kind, "scripted " + kind, retryAfter));

    public Task<Result<RefreshedTokens, UsageReadFailure>> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        RefreshTokens.Add(refreshToken);
        if (Answers.Count > 0)
        {
            return Task.FromResult(Answers.Dequeue());
        }

        if (AnswerAt is not null)
        {
            return Task.FromResult(AnswerAt(RefreshTokens.Count));
        }

        Unscripted = true;
        return Task.FromResult(Failed(UsageReadFailureKind.Transport));
    }
}

/// <summary>
/// The login runner reduced to the one question a refresh asks it, with a hook
/// that fires when it is asked. The hook is what lets a test move a credential
/// file in the window between the pass's first read of a pair and the re-read
/// the gated unit makes: the question is asked under the gate, immediately
/// before that re-read.
/// </summary>
internal sealed class ScriptedLogins : ILoginSessionRunner
{
    public HashSet<string> Busy { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Action<string>? OnAsk { get; set; }

    public Task<Result<LoginSession, string>> StartAsync(AccountEmail email, string folderPath, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<Result<LoginSession, string>> SubmitCodeAsync(LoginSessionId id, string code, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public LoginSession? Status(LoginSessionId id) => null;

    public bool IsRunningAgainst(string folderPath)
    {
        OnAsk?.Invoke(folderPath);
        return Busy.Contains(Path.GetFullPath(folderPath));
    }
}

/// <summary>
/// The real credential store with a fault injected into the write-back only.
/// A decorator rather than a read-only file, because file permissions are the
/// one fault that behaves differently on every operating system this runs on,
/// and the engine's retry-then-strand path must be asserted on all of them.
/// </summary>
internal sealed class FaultyPairStore(ICredentialPairStore inner) : ICredentialPairStore
{
    /// <summary>A locked credential file, the transient failure the write-back retries.</summary>
    public static IOException Locked => new("the credential file is locked by another process");

    /// <summary>Every write-back throws this, or none does when it is null.</summary>
    public Exception? ThrowOnWriteWith { get; set; }

    /// <summary>Every write-back returns this failure, the way a fingerprint mismatch does.</summary>
    public string? RefuseWriteWith { get; set; }

    public int WriteAttempts { get; private set; }

    public Task<CredentialPair?> ReadLiveAsync(CancellationToken cancellationToken) =>
        inner.ReadLiveAsync(cancellationToken);

    public Task<CredentialPair?> ReadParkedAsync(string folderPath, CancellationToken cancellationToken) =>
        inner.ReadParkedAsync(folderPath, cancellationToken);

    public Task MoveLiveToParkedAsync(string folderPath, CancellationToken cancellationToken) =>
        inner.MoveLiveToParkedAsync(folderPath, cancellationToken);

    public Task MoveParkedToLiveAsync(string folderPath, CancellationToken cancellationToken) =>
        inner.MoveParkedToLiveAsync(folderPath, cancellationToken);

    public Task MoveParkedToQuarantineAsync(string folderPath, string destinationDirectory, CancellationToken cancellationToken) =>
        inner.MoveParkedToQuarantineAsync(folderPath, destinationDirectory, cancellationToken);

    public Task<Result<Unit, string>> WriteParkedAsync(string folderPath, CredentialPair pair, RefreshTokenFingerprint expected, CancellationToken cancellationToken)
    {
        WriteAttempts++;
        if (ThrowOnWriteWith is Exception fault)
        {
            throw fault;
        }

        return RefuseWriteWith is string refusal
            ? Task.FromResult(Result<Unit, string>.Failure(refusal))
            : inner.WriteParkedAsync(folderPath, pair, expected, cancellationToken);
    }

    public Task<Result<IAsyncDisposable, string>> AcquireRefreshLockAsync(TimeSpan waitBound, CancellationToken cancellationToken) =>
        inner.AcquireRefreshLockAsync(waitBound, cancellationToken);

    public string? FreshLockFileName(TimeSpan maxAge) => inner.FreshLockFileName(maxAge);
}

/// <summary>The CLI's identity check, which a refresh never reaches but the switch executor holds a reference to.</summary>
internal sealed class CannedIdentity : IClaudeCliAuthStatus
{
    public string? Email { get; set; }

    public Task<Result<ClaudeAuthStatus, string>> ReadAsync(string? configDirectory, CancellationToken cancellationToken) =>
        Task.FromResult(Result<ClaudeAuthStatus, string>.Success(new ClaudeAuthStatus(true, Email, "claude.ai", "Personal", "max", null)));
}
