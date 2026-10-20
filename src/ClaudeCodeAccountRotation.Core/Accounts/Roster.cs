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
    /// One account backs each CI role at a time: every role <paramref name="entry"/>
    /// backs is taken off every other entry, whatever either's dates, the way
    /// adopting a new live account elsewhere already leaves only one seat live.
    /// The other entry keeps its other roles; its token date becomes that of its
    /// newest remaining token half, or none once its last token half goes (see
    /// <see cref="RosterEntry.WithCiRoles"/>). Markers set by hand, with no role, count as one
    /// role among themselves, so they keep their old behavior.
    /// </summary>
    public Roster With(RosterEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        IEnumerable<RosterEntry> others = _entries.Where(existing => existing.Email != entry.Email);
        if (!entry.CiRoles.IsEmpty)
        {
            others = others.Select(existing => existing.WithCiRoles(
                existing.CiRoles.Where(held => !entry.CiRoles.Any(claimed => claimed.Role.Equals(held.Role)))));
        }
        else if (entry.CiTokenGeneratedOn is not null)
        {
            others = others.Select(static existing => existing.CiRoles.IsEmpty && existing.CiTokenGeneratedOn is not null
                ? existing with { CiTokenGeneratedOn = null }
                : existing);
        }

        return new Roster(others.Append(entry));
    }

    public Roster Without(AccountEmail email) => new(_entries.Where(entry => entry.Email != email));
}
