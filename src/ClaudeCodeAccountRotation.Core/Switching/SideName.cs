namespace ClaudeCodeAccountRotation.Core.Switching;

/// <summary>
/// One operating-system lane of one machine: <c>windows</c> or <c>wsl</c>. A
/// side is never a machine. The laptop runs both sides, each with its own live
/// config directory and its own switch, off the one store the Windows side
/// owns, so "the WSL side" names a lane on this machine and says nothing about
/// any other.
/// <para>
/// Equality is the record struct's own, which compares <see cref="Value"/>
/// ordinally; the two names below are the only values the store writes, both
/// lowercase, so no case-insensitive comparison is wanted.
/// </para>
/// </summary>
public readonly record struct SideName(string Value)
{
    /// <summary>The native Windows lane, which owns the store and is its only writer of slots.</summary>
    public static SideName Windows { get; } = new("windows");

    /// <summary>The WSL lane, which holds pairs in its own live directory and never writes a slot.</summary>
    public static SideName Wsl { get; } = new("wsl");

    public override string ToString() => Value;
}
