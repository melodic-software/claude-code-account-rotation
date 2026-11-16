using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Accounts;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Ports;

namespace ClaudeCodeAccountRotation.App.Accounts;

/// <summary>
/// The one tier judgment for a folder that is not the live directory, asked
/// both when an account joins the roster and at the end of a login into that
/// folder. The roster asks two sources, the identity the folder records and
/// the CLI's own <c>auth status --json</c> run under it whenever there is a
/// login there to read; the login asks the CLI alone, for the reason on
/// <see cref="JudgeFreshLoginAsync"/>.
/// </summary>
internal static class ParkedFolderAdmission
{
    /// <summary>
    /// Judges the login that just ended in <paramref name="folderPath"/>, which
    /// holds the pair it wrote, from the CLI's answer alone. The identity the
    /// folder records is not consulted: a folder logged in before carries the
    /// earlier login's <c>profile.json</c>, and a login whose tidy-up could not
    /// run leaves its state file standing, so nothing on disk proves which login
    /// wrote a block that names a Max tier, and a block that cannot be tied to
    /// this login is no evidence about the seat that just signed in.
    /// <para>
    /// The same answer names the address that signed in, which is the caller's
    /// to compare with the row the login was started from: the browser step
    /// signs in whichever account its profile holds, whatever address the row
    /// asked for. Null when the CLI named none, or named something that is not
    /// an address.
    /// </para>
    /// </summary>
    public static async Task<(MaxTierVerdict Verdict, string? Reason, AccountEmail? SignedInAs)> JudgeFreshLoginAsync(
        string folderPath,
        IClaudeCliAuthStatus cli,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        ArgumentNullException.ThrowIfNull(cli);
        Result<ClaudeAuthStatus, string> read = await cli.ReadAsync(folderPath, cancellationToken);
        ClaudeAuthStatus? status = read.IsSuccess ? read.Value : null;
        (MaxTierVerdict verdict, string? reason) = MaxTierAdmission.Evaluate(status, account: null);
        AccountEmail? signedInAs =
            status is { LoggedIn: true, Email: string address } && AccountEmail.Parse(address) is { IsSuccess: true } parsed
                ? parsed.Value
                : null;
        return (verdict, reason, signedInAs);
    }

    /// <summary>
    /// Judges <paramref name="folderPath"/>, which must exist. A folder holding
    /// no credential pair is judged from its recorded identity alone; spawning
    /// the CLI on a folder with no login to read would cost a process and leave
    /// residue to say nothing.
    /// </summary>
    public static async Task<(MaxTierVerdict Verdict, string? Reason)> JudgeAsync(
        string folderPath,
        ProfileFolderStore profiles,
        IClaudeCliAuthStatus cli,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(cli);
        OAuthAccountBlock? account = await ReadAccountAsync(profiles, folderPath, cancellationToken);
        if (!File.Exists(Path.Combine(folderPath, FileSystemCredentialPairStore.FileName)))
        {
            return MaxTierAdmission.Evaluate(null, account);
        }

        Result<ClaudeAuthStatus, string> status = await cli.ReadAsync(folderPath, cancellationToken);
        return MaxTierAdmission.Evaluate(status.IsSuccess ? status.Value : null, account);
    }

    /// <summary>
    /// Best effort. A file the CLI wrote seconds ago can still be held open by
    /// something else, and an identity that cannot be read is evidence of
    /// nothing either way; the CLI's own answer is the authority and
    /// <see cref="MaxTierAdmission.Evaluate"/> takes a null block. Throwing here
    /// instead would turn a held file into a refusal.
    /// </summary>
    private static async Task<OAuthAccountBlock?> ReadAccountAsync(ProfileFolderStore profiles, string folderPath, CancellationToken cancellationToken)
    {
        try
        {
            return await profiles.ReadAccountAsync(folderPath, cancellationToken);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
