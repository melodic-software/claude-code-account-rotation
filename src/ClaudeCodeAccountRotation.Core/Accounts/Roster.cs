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
    /// One account backs each CI role half at a time. A role <paramref name="entry"/>
    /// claims anew is taken whole off every other entry, whatever either's
    /// dates, the way adopting a new live account elsewhere already leaves only
    /// one seat live. For a role the entry already held, only the halves it now
    /// holds that changed are taken off the others: a split role's other half
    /// stays with the account that set it, through an edit of this entry's
    /// other fields or a re-send of its own half. A role in
    /// <paramref name="halvesOnly"/> is claimed by its halves even when new:
    /// the caller set only some of its secrets, and the others still hold
    /// their holder's values.
    /// The other entry keeps its other roles; its token date becomes that of its
    /// newest remaining token half, or none once its last token half goes (see
    /// <see cref="RosterEntry.WithCiRoles"/>). Markers set by hand, with no role, count as one
    /// role among themselves, so they keep their old behavior.
    /// </summary>
    public Roster With(RosterEntry entry, IReadOnlyCollection<CiRole>? halvesOnly = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        IEnumerable<RosterEntry> others = _entries.Where(existing => existing.Email != entry.Email);
        if (!entry.CiRoles.IsEmpty)
        {
            RosterEntry? before = Find(entry.Email);
            others = others.Select(existing => existing.WithCiRoles(
                existing.CiRoles.Select(held => Released(held, entry, before, halvesOnly)).OfType<CiRoleAssignment>()));
        }
        else if (entry.CiTokenGeneratedOn is not null)
        {
            others = others.Select(static existing => existing.CiRoles.IsEmpty && existing.CiTokenGeneratedOn is not null
                ? existing with { CiTokenGeneratedOn = null }
                : existing);
        }

        return new Roster(others.Append(entry));
    }

    /// <summary>
    /// What another entry keeps of <paramref name="held"/> once <paramref name="entry"/>
    /// is stored, or null when it keeps no half.
    /// </summary>
    private static CiRoleAssignment? Released(CiRoleAssignment held, RosterEntry entry, RosterEntry? before, IReadOnlyCollection<CiRole>? halvesOnly)
    {
        CiRoleAssignment? claim = entry.CiRoles.FirstOrDefault(assignment => assignment.Role.Equals(held.Role));
        if (claim is null)
        {
            return held;
        }

        CiRoleAssignment? prior = before?.CiRoles.FirstOrDefault(assignment => assignment.Role.Equals(held.Role));
        if (prior is null && halvesOnly?.Contains(held.Role) != true)
        {
            return null;
        }

        bool tokenTaken = claim.HasToken && (claim.TokenSetOn, claim.TokenSetAt) != (prior?.TokenSetOn, prior?.TokenSetAt);
        bool keyTaken = claim.HasKey && (claim.KeySetOn, claim.KeySetAt) != (prior?.KeySetOn, prior?.KeySetAt);
        CiRoleAssignment kept = held with
        {
            TokenSetOn = tokenTaken ? null : held.TokenSetOn,
            TokenSetAt = tokenTaken ? null : held.TokenSetAt,
            KeySetOn = keyTaken ? null : held.KeySetOn,
            KeySetAt = keyTaken ? null : held.KeySetAt,
        };
        return kept.HasToken || kept.HasKey ? kept : null;
    }

    public Roster Without(AccountEmail email) => new(_entries.Where(entry => entry.Email != email));
}
