using ClaudeCodeAccountRotation.Core.Identity;

namespace ClaudeCodeAccountRotation.Core.Accounts;

/// <summary>
/// Every account the operator has put on this machine, in e-mail order. The
/// roster is the list the page edits; which of those accounts actually holds a
/// credential pair is the profile folders' business, not this type's.
/// </summary>
public sealed class Roster
{
    public static readonly Roster Empty = new([]);

    private readonly List<RosterEntry> _entries;

    public Roster(IEnumerable<RosterEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _entries = [.. entries];
        _entries.Sort(static (left, right) => string.CompareOrdinal(left.Email.Value, right.Email.Value));
    }

    public IReadOnlyList<RosterEntry> Entries => _entries;

    public RosterEntry? Find(AccountEmail email) => _entries.Find(entry => entry.Email == email);

    /// <summary>
    /// Adds <paramref name="entry"/>, replacing any entry for the same account.
    /// One account backs each CI secret at a time: marking <paramref name="entry"/>
    /// as the holder of a secret clears the marker from every other entry that
    /// held the same one, the way adopting a new live account elsewhere already
    /// leaves only one seat live. Markers set by hand, with no secret named,
    /// count as one secret among themselves, so they keep their old behavior.
    /// </summary>
    public Roster With(RosterEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        IEnumerable<RosterEntry> others = _entries.Where(existing => existing.Email != entry.Email);
        if (entry.CiTokenGeneratedOn is not null)
        {
            others = others.Select(existing => existing.CiTokenGeneratedOn is not null && SameSecret(existing.CiTokenSecret, entry.CiTokenSecret)
                ? existing with { CiTokenGeneratedOn = null, CiTokenSecret = null }
                : existing);
        }

        return new Roster(others.Append(entry));
    }

    private static bool SameSecret(CiTokenSecret? left, CiTokenSecret? right) =>
        left is null ? right is null : left.SameSecretAs(right);

    public Roster Without(AccountEmail email) => new(_entries.Where(entry => entry.Email != email));
}
