using ClaudeCodeAccountRotation.Core.Switching;

namespace ClaudeCodeAccountRotation.App.Adapters.FileSystem;

/// <summary>
/// <c>holder.json</c> in a store slot: which side took the slot's pair, which
/// pair it was, and since when. Read and written by <see cref="HolderRecordJson"/>.
/// <para>
/// A missing, torn, or unparsable file answers "no record" and leaves the slot
/// to be judged by what it holds, which is the rule the design settles on
/// wherever the two disagree.
/// </para>
/// </summary>
internal static class HolderRecordFile
{
    public const string FileName = "holder.json";

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
