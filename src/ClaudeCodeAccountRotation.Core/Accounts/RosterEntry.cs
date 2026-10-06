using ClaudeCodeAccountRotation.Core.Identity;

namespace ClaudeCodeAccountRotation.Core.Accounts;

/// <summary>The Chromium-family browsers the launcher knows how to open a profile in.</summary>
public enum BrowserFamily
{
    Chrome,
    Edge,
    Brave,
}

/// <summary>
/// One account the operator put on this machine: its e-mail, what to call it,
/// which browser profile signs it in, whether it is out of the rotation, and
/// whatever the operator wrote about it. No token field exists here; the
/// credential pair lives in the profile folder, never in the roster.
/// <para>
/// <see cref="CiTokenGeneratedOn"/> is the date a <c>claude setup-token</c>
/// token for this account was made, and <see cref="CiTokenSecret"/> the GitHub
/// secret it went to, when the dashboard set it. Neither is read back from
/// GitHub. A marker with no secret is one the operator set by hand, for the
/// secret the README names. At most one entry on a <see cref="Roster"/> backs
/// each secret; see <see cref="Roster.With"/>.
/// </para>
/// </summary>
public sealed record RosterEntry(
    AccountEmail Email,
    string? Alias = null,
    BrowserFamily? Browser = null,
    string? BrowserProfileDirectory = null,
    bool Paused = false,
    string? Notes = null,
    DateOnly? CiTokenGeneratedOn = null,
    CiTokenSecret? CiTokenSecret = null);
