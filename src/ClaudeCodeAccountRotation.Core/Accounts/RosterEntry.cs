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
/// <see cref="CiTokenGeneratedOn"/> is the operator's own marker: the date they
/// ran <c>claude setup-token</c> on this account for the org secret
/// <c>CLAUDE_CODE_OAUTH_TOKEN</c>, not read or verified against GitHub. At most
/// one entry on a <see cref="Roster"/> carries it; see <see cref="Roster.With"/>.
/// </para>
/// </summary>
public sealed record RosterEntry(
    AccountEmail Email,
    string? Alias = null,
    BrowserFamily? Browser = null,
    string? BrowserProfileDirectory = null,
    bool Paused = false,
    string? Notes = null,
    DateOnly? CiTokenGeneratedOn = null);
