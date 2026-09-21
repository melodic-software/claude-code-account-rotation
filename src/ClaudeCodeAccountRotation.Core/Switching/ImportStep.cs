namespace ClaudeCodeAccountRotation.Core.Switching;

/// <summary>
/// How far a follower's staged import got before the journal was last written.
/// Design 9.2's F2 to F7.
/// <para>
/// The journal is a hint and the fingerprints on disk are the evidence. A
/// crash between a disk mutation and the journal write leaves the journal one
/// step behind what the files say, which is why the follower's reconciler
/// decides every row of design 9.3 by reading the live, staging, claimed and
/// export files rather than by trusting this value.
/// </para>
/// </summary>
public enum ImportStep
{
    /// <summary>F2: the refresh lock is held and the outgoing pair has been read. Nothing has moved.</summary>
    Planned,

    /// <summary>F3: the incoming pair is staged beside the live file and verified by fingerprint.</summary>
    Staged,

    /// <summary>F4: the outgoing pair is copied to the mailbox and verified. The follower stops here until a commit arrives.</summary>
    Exported,

    /// <summary>F5: the staging file has replaced the live file. The outgoing pair's last local copy is gone.</summary>
    Swapped,

    /// <summary>F6: the claimed file in the mailbox has been deleted.</summary>
    Released,

    /// <summary>F7: the state file's account block and the live owner record name the incoming account.</summary>
    Patched,
}
