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
    /// One account holds <see cref="RosterEntry.CiTokenGeneratedOn"/> at a time:
    /// marking <paramref name="entry"/> as the CI token holder clears the marker
    /// from every other entry, the way adopting a new live account elsewhere
    /// already leaves only one seat live.
    /// </summary>
    public Roster With(RosterEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        IEnumerable<RosterEntry> others = _entries.Where(existing => existing.Email != entry.Email);
        if (entry.CiTokenGeneratedOn is not null)
        {
            others = others.Select(static existing => existing.CiTokenGeneratedOn is null
                ? existing
                : existing with { CiTokenGeneratedOn = null });
        }

        return new Roster(others.Append(entry));
    }

    public Roster Without(AccountEmail email) => new(_entries.Where(entry => entry.Email != email));
}
