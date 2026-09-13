using System.Globalization;
using ClaudeCodeAccountRotation.Core.Identity;

namespace ClaudeCodeAccountRotation.App.Quota;

/// <summary>
/// What one account's turn in a refresh pass came to. Ordered as the pass
/// decides them: the two that mean numbers landed or are about to, the three
/// refusals that cost the endpoint nothing, then the four ways a credential
/// operation ended badly and the read failure that is none of those.
/// </summary>
internal enum RefreshOutcomeKind
{
    /// <summary>The usage endpoint answered and the card has fresh numbers.</summary>
    Read,

    /// <summary>
    /// The live pair's access token is expired or was rejected. This tool never
    /// refreshes the live pair: the running CLI session owns that lineage and
    /// renews it itself, usually within the minute.
    /// </summary>
    SessionWillRefresh,

    /// <summary>A 429 from one of the two hosts; <see cref="RefreshOutcome.RetryAt"/> says when the lockout lifts.</summary>
    RateLimited,

    /// <summary>The tool's own budget refused the read; nothing was sent.</summary>
    BudgetRefused,

    /// <summary>Another credential operation owned the folder or the gate, so the turn was passed over.</summary>
    Skipped,

    /// <summary>No credential pair exists for this account yet.</summary>
    NeedsLogin,

    /// <summary>
    /// The token endpoint rotated the pair and the write-back could not land it,
    /// so the rotated pair is in the recovery directory and the file in the folder
    /// is dead. A restart or a per-card refresh clears it.
    /// </summary>
    Stranded,

    /// <summary>The rotated pair could not even reach the recovery directory: the lineage is gone.</summary>
    Lost,

    /// <summary>The token endpoint did not answer with new credentials; the old pair is untouched.</summary>
    TokenRefreshFailed,

    /// <summary>The usage endpoint did not answer with numbers this build could read.</summary>
    ReadFailed,
}

/// <summary>
/// One account's outcome from one pass: flat, the shape every result in this
/// codebase takes. <paramref name="Message"/> is what the card renders, so it is
/// always a curated constant or built from an account e-mail and a count of
/// seconds; a string from the credential store never becomes one, because those
/// carry the file's path. <paramref name="RetryAt"/> is set only when something
/// external says when to come back.
/// </summary>
internal sealed record RefreshOutcome(RefreshOutcomeKind Kind, string Message, DateTimeOffset? RetryAt, DateTimeOffset RecordedAt);

/// <summary>
/// Every sentence a card can show for a refresh. They live together so the set
/// is auditable in one read: nothing here interpolates a path, a token, or a
/// message from a lower layer.
/// </summary>
internal static class RefreshMessages
{
    public const string MutationInProgress = "another credential change is in progress";
    public const string LoginInProgress = "a login is in progress";
    public const string PairChanged = "the pair changed underneath the refresh";
    public const string LiveIdentityUnverified = "live identity unverified";
    public const string SessionWillRefresh = "session will refresh";
    public const string NeedsLogin = "no credentials on this machine; log in again";
    public const string Read = "read just now";
    public const string TokenRefreshFailed = "the token endpoint did not answer with new credentials";
    public const string Stranded = "credentials stranded in recovery";
    public const string Lost = "credentials lost; log in again";
    public const string ReadFailedTransport = "the usage endpoint could not be read";
    public const string ReadFailedUnauthorized = "the refreshed credentials were rejected; log this account in again";
    public const string ReadFailedMalformed = "the usage endpoint answered something this build could not read";
    public const string Paused = "paused";

    /// <summary>
    /// A paused account whose login was about to lapse and was renewed instead.
    /// It still carries no numbers, which is why it reports as a skip: nothing
    /// was read, and the operator's read budget was not spent on an account that
    /// is out of the rotation.
    /// </summary>
    public const string PausedLoginRenewed = "paused; login renewed";

    /// <summary>
    /// A paused account whose login was renewed recently enough that renewing it
    /// again would buy nothing. The token response need not say when the login
    /// now expires, and when it does not the recorded expiry stays where it was,
    /// so without this sentence the same account would be posted for on every
    /// pass for the rest of its login.
    /// </summary>
    public const string PausedLoginRenewedRecently = "paused; login renewed recently";

    /// <summary>"rate limited, retry in N s", the countdown clamped at zero.</summary>
    public static string RateLimited(TimeSpan remaining) =>
        "rate limited, retry in " + Seconds(remaining) + " s";

    /// <summary>
    /// "read N s ago", which is what a budget refusal owes the operator: the
    /// refusal is only meaningful next to how recently the numbers on the card
    /// were taken. Measured from the snapshot the card is showing rather than
    /// from the budget, which counts reservations and has no notion of when an
    /// account's numbers were last seen.
    /// </summary>
    public static string ReadRecently(TimeSpan sinceLastRead) =>
        "read " + Seconds(sinceLastRead) + " s ago";

    /// <summary>The refusal for an account that spent its window rather than its gap.</summary>
    public const string BudgetSpent = "the read budget for this account is spent for now";

    /// <summary>The recovery warning for a folder whose parked pair is gone: only the account is named, never the folder.</summary>
    public static string RestoreNeedsLogin(AccountEmail account) =>
        "The rotated credentials for " + account.Value + " are held in the recovery directory and no parked pair is there to replace; log that account in again.";

    /// <summary>
    /// The recovery warning for a file the store refused to apply although the
    /// folder does still hold a pair. Its own sentence rather than the one above,
    /// which says there is nothing in the folder to replace and would be false
    /// here.
    /// </summary>
    public static string RestoreRefused(AccountEmail account) =>
        "The rotated credentials for " + account.Value + " could not be written back and are kept in the recovery directory; the next start, or that card's Refresh, tries again.";

    /// <summary>The recovery warning for a file whose folder was legitimately rewritten since.</summary>
    public static string RestoreStale(AccountEmail account) =>
        "A recovery file for " + account.Value + " no longer matches that account's parked credentials and was moved aside; the parked pair on disk is the live lineage.";

    /// <summary>The recovery warning for a file that could not be read at all.</summary>
    public static string RestoreUnreadable() =>
        "A recovery file could not be read and was moved aside; if an account reports needing a login, log it in again.";

    /// <summary>
    /// The recovery warning for a file whose envelope names a different folder
    /// than the file is filed under. The file name is what the sweep trusts when
    /// it decides which folder to rewrite, so a file that disagrees with itself
    /// is moved aside rather than applied to either.
    /// </summary>
    public static string RestoreMisfiled() =>
        "A recovery file did not name the account folder it was filed under and was moved aside; if an account reports needing a login, log it in again.";

    /// <summary>
    /// The recovery warning for a file still sitting in the recovery directory,
    /// waiting for a start or a refresh that can apply it. Distinct from the
    /// sentence above because nothing moved anywhere: sending the operator to the
    /// stale directory for a file that is not there is the sort of message this
    /// catalogue exists to prevent.
    /// </summary>
    public static string RestoreKept() =>
        "A recovery file could not be applied yet and was kept; if an account reports needing a login, log it in again.";

    private static string Seconds(TimeSpan span) =>
        Math.Max(0, (int)Math.Ceiling(span.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
}
