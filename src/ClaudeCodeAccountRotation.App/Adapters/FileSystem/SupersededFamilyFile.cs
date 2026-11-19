using ClaudeCodeAccountRotation.Core.Switching;

namespace ClaudeCodeAccountRotation.App.Adapters.FileSystem;

/// <summary>
/// <c>superseded.json</c> in a store slot: a token family for this account that
/// another side still holds and the store no longer owns. It is written by the
/// one control that is allowed to create a second family — <c>Log in again on
/// Windows</c> while that side is unreachable — and it is what makes that
/// family visible afterwards instead of silent.
/// <para>
/// Deliberately a second file rather than the <c>holder.json</c> the design
/// text says to overwrite. A holder record is dropped the moment the slot holds
/// a pair again (design 9.4 row 2), and the fresh login puts a pair in the slot
/// immediately, so a superseded family written into that file would be erased
/// by the next dashboard poll — and the first Windows switch to this account
/// would overwrite it with a <c>windows</c> record besides. The two facts have
/// different lifetimes, so they are different files.
/// </para>
/// <para>
/// The shape is <see cref="HolderRecord"/>'s, read the same way by
/// <see cref="HolderRecordJson"/>: a missing, torn, or unparsable file answers
/// "no superseded family", because the consequence of misreading one is a
/// refused switch and never a moved pair. The fingerprint is the family's as it
/// was when the slot was superseded; the other side refreshes its own token on
/// its own schedule, so it is evidence for the operator and never a key
/// anything matches on.
/// </para>
/// </summary>
internal static class SupersededFamilyFile
{
    public const string FileName = "superseded.json";

    public static string PathIn(string folderPath) =>
        Path.Combine(folderPath, FileName);

    public static Task<HolderRecord?> ReadAsync(string folderPath, CancellationToken cancellationToken) =>
        HolderRecordJson.ReadAsync(folderPath, FileName, cancellationToken);

    public static Task WriteAsync(string folderPath, HolderRecord record, CancellationToken cancellationToken) =>
        HolderRecordJson.WriteAsync(folderPath, FileName, record, cancellationToken);

    /// <inheritdoc cref="HolderRecordJson.DeleteAsync"/>
    public static Task DeleteAsync(string folderPath, CancellationToken cancellationToken) =>
        HolderRecordJson.DeleteAsync(folderPath, FileName, cancellationToken);
}
