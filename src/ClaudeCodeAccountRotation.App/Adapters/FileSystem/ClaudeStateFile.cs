using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.Core.Identity;

namespace ClaudeCodeAccountRotation.App.Adapters.FileSystem;

/// <summary>
/// Claude Code's state file (<c>~/.claude.json</c>, or <c>&lt;CLAUDE_CONFIG_DIR&gt;/.claude.json</c>
/// when that variable is set). <c>oauthAccount</c> is rewritten by splicing the new
/// value into the original bytes. The onboarding flag is the one extra key, and only
/// when the caller asks and the flag is not already true. Every other byte of the
/// file, including per-project trust and MCP state, is preserved.
/// </summary>
internal sealed class ClaudeStateFile
{
    private const string AccountPropertyName = "oauthAccount";
    private const string OnboardingPropertyName = "hasCompletedOnboarding";
    private const int MaxPatchAttempts = 5;

    private static readonly byte[] _jsonTrue = "true"u8.ToArray();

    private readonly Func<CancellationToken, Task> _beforeReplace;

    public ClaudeStateFile(string path)
        : this(path, beforeReplace: null)
    {
    }

    /// <summary>
    /// <paramref name="beforeReplace"/> runs after the patched bytes are staged and
    /// before the file is re-read and replaced; tests use it to interleave a
    /// session's rewrite at the one moment the detector must see it.
    /// </summary>
    internal ClaudeStateFile(string path, Func<CancellationToken, Task>? beforeReplace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
        _beforeReplace = beforeReplace ?? (static _ => Task.CompletedTask);
    }

    public string Path { get; }

    public async Task<OAuthAccountBlock?> ReadAccountBlockAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(Path))
        {
            return null;
        }

        byte[] bytes = await SharedFileReader.ReadAllBytesAsync(Path, cancellationToken);
        if (LocateRootValues(bytes, locateOnboarding: false).Account is not ValueSpan span)
        {
            return null;
        }

        try
        {
            var node = JsonNode.Parse(bytes.AsSpan(span.Start, span.Length));
            return node is JsonObject raw ? OAuthAccountBlock.FromJson(raw) : null;
        }
        catch (JsonException exception)
        {
            throw CaughtHalfWritten(exception);
        }
    }

    /// <summary>
    /// Patches against the bytes on disk at the moment of the replace: the file is
    /// read, the patched bytes are staged in a flushed temp file, then the file is
    /// read again and replaced only if every byte is still what was read, so a
    /// session's own rewrite of an unrelated key during the patch is re-read and
    /// kept rather than overwritten with stale bytes. Bytes, not a length and a
    /// last-write time: the CLI's rewrites often keep the length and can land
    /// inside one timestamp tick. The only unguarded window is the rename itself.
    /// A file that keeps changing across <see cref="MaxPatchAttempts"/> attempts
    /// fails the patch instead of guessing; the journal then completes it at the
    /// next startup. A read that lands on a half-written file fails the same way,
    /// from <see cref="LocateRootValues"/>: splicing a block into bytes that are
    /// not a whole document would write the file out as garbage.
    /// This method rewrites the account block only and does not write
    /// <c>hasCompletedOnboarding</c>.
    /// </summary>
    public Task PatchAccountBlockAsync(OAuthAccountBlock account, CancellationToken cancellationToken) =>
        PatchAccountBlockCoreAsync(account, recordCompletedOnboarding: false, cancellationToken);

    /// <summary>
    /// Patches <paramref name="account"/> the way <see cref="PatchAccountBlockAsync"/>
    /// does, and in that same compare-and-swap sets <c>hasCompletedOnboarding</c> to
    /// JSON true when the key is absent or not already boolean true. A value that is
    /// already boolean true is left as it stands, and <c>lastOnboardingVersion</c> is
    /// never written. A release does not call this: clearing the account must not
    /// add the key.
    /// </summary>
    public Task PatchAccountBlockAndOnboardingAsync(OAuthAccountBlock account, CancellationToken cancellationToken) =>
        PatchAccountBlockCoreAsync(account, recordCompletedOnboarding: true, cancellationToken);

    private async Task PatchAccountBlockCoreAsync(OAuthAccountBlock account, bool recordCompletedOnboarding, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        byte[] value = JsonSerializer.SerializeToUtf8Bytes(account.Raw);

        for (int attempt = 1; attempt <= MaxPatchAttempts; attempt++)
        {
            byte[]? original = await ReadBytesOrNullAsync(cancellationToken);
            byte[] basis = original ?? Encoding.UTF8.GetBytes("{}");
            byte[] patched = BuildPatchedBytes(basis, value, recordCompletedOnboarding);

            string temporaryPath = await AtomicBytesFile.WriteTemporaryAsync(Path, patched, cancellationToken);
            try
            {
                await _beforeReplace(cancellationToken);
                byte[]? current = await ReadBytesOrNullAsync(cancellationToken);
                bool unchanged = original is null
                    ? current is null
                    : current is not null && current.AsSpan().SequenceEqual(original);
                if (unchanged)
                {
                    await AtomicBytesFile.MoveIntoPlaceWithRetryAsync(temporaryPath, Path, cancellationToken);
                    return;
                }
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }

        throw new IOException("The state file " + Path + " kept changing while the account block was being patched; the switch is journaled and completes at the next startup.");
    }

    /// <summary>
    /// Splices the account value, and when asked the onboarding flag, into
    /// <paramref name="basis"/>. Edits are applied from the last byte backward so
    /// each earlier offset stays valid. A flag that is already boolean true is
    /// not an edit. A missing property is inserted before the root object's
    /// closing brace rather than rewritten over some other key.
    /// </summary>
    private byte[] BuildPatchedBytes(byte[] basis, byte[] accountValue, bool recordCompletedOnboarding)
    {
        (ValueSpan? account, ValueSpan? onboarding) = LocateRootValues(basis, recordCompletedOnboarding);

        List<(int Start, int Length, byte[] Bytes)> edits = [];
        if (account is ValueSpan accountSpan)
        {
            edits.Add((accountSpan.Start, accountSpan.Length, accountValue));
        }

        if (recordCompletedOnboarding && onboarding is ValueSpan onboardingSpan && onboardingSpan.TokenType != JsonTokenType.True)
        {
            edits.Add((onboardingSpan.Start, onboardingSpan.Length, _jsonTrue));
        }

        // Last edit first: splicing an earlier span first would move every later offset.
        edits.Sort(static (left, right) => right.Start.CompareTo(left.Start));
        byte[] patched = basis;
        foreach ((int start, int length, byte[] bytes) in edits)
        {
            patched = Splice(patched, start, length, bytes);
        }

        bool appendOnboarding = recordCompletedOnboarding && onboarding is null;
        if (account is null || appendOnboarding)
        {
            List<(string Name, byte[] Value)> appended = [];
            if (account is null)
            {
                appended.Add((AccountPropertyName, accountValue));
            }

            if (appendOnboarding)
            {
                appended.Add((OnboardingPropertyName, _jsonTrue));
            }

            patched = AppendProperties(patched, appended);
        }

        return patched;
    }

    private async Task<byte[]?> ReadBytesOrNullAsync(CancellationToken cancellationToken) =>
        File.Exists(Path) ? await SharedFileReader.ReadAllBytesAsync(Path, cancellationToken) : null;

    /// <summary>
    /// The spans of the root <c>oauthAccount</c> value and, when
    /// <paramref name="locateOnboarding"/> is true, the root
    /// <c>hasCompletedOnboarding</c> value. Either is null when the file is a
    /// JSON object without that property, read out of the whole document every time:
    /// a file cut off after a block still yields a sound span, so the rest of the
    /// bytes have to be read before one is handed back. Bytes that are not a whole
    /// JSON document throw <see cref="InvalidDataException"/> rather than reading
    /// as null or as a usable span: Claude
    /// Code truncates this file and rewrites it in place, so a read lands on zero
    /// bytes or half a document often enough to have taken the tool down, and "no
    /// account block" is the answer every caller reads as "nobody is logged in". A
    /// switch planned over that would park the live pair without the outgoing
    /// account's block. The state file watcher already logs this exception and waits
    /// for the next change; a switch fails with it and the journal completes at the
    /// next startup.
    /// </summary>
    private (ValueSpan? Account, ValueSpan? Onboarding) LocateRootValues(byte[] bytes, bool locateOnboarding)
    {
        try
        {
            Utf8JsonReader reader = new(bytes, new JsonReaderOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return (null, null);
            }

            ValueSpan? account = null;
            ValueSpan? onboarding = null;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 0)
                {
                    // Read on past the root object rather than returning here: that
                    // last read is what proves these bytes are a whole document and
                    // not a prefix of one. A rewrite cut off after the account block
                    // leaves a span that is sound on its own, and splicing into those
                    // bytes would replace the file with JSON nothing can read.
                    // Trailing bytes throw from the read; the answer is asserted
                    // rather than discarded so the proof does not rest on which
                    // reader options are set here.
                    if (reader.Read())
                    {
                        throw CaughtHalfWritten(cause: null);
                    }

                    return (account, onboarding);
                }

                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1)
                {
                    continue;
                }

                bool isAccount = reader.ValueTextEquals(AccountPropertyName);
                bool isOnboarding = locateOnboarding && reader.ValueTextEquals(OnboardingPropertyName);
                reader.Read();
                if (!isAccount && !isOnboarding)
                {
                    reader.TrySkip();
                    continue;
                }

                int start = checked((int)reader.TokenStartIndex);
                JsonTokenType tokenType = reader.TokenType;
                reader.TrySkip();
                int end = checked((int)reader.BytesConsumed);
                ValueSpan span = new(start, end - start, tokenType);
                if (isAccount)
                {
                    account = span;
                }

                if (isOnboarding)
                {
                    onboarding = span;
                }
            }

            return (account, onboarding);
        }
        catch (JsonException exception)
        {
            throw CaughtHalfWritten(exception);
        }
    }

    private InvalidDataException CaughtHalfWritten(JsonException? cause) =>
        new("The state file " + Path + " did not hold one whole JSON document when it was read; it was empty or only partly written. Claude Code rewrites it in place, so this is normally a read that landed mid-write.", cause);

    private static byte[] Splice(byte[] original, int start, int length, byte[] value)
    {
        byte[] result = new byte[original.Length - length + value.Length];
        original.AsSpan(0, start).CopyTo(result);
        value.CopyTo(result.AsSpan(start));
        original.AsSpan(start + length).CopyTo(result.AsSpan(start + value.Length));
        return result;
    }

    /// <summary>
    /// Inserts each <c>"name": value</c> before the root object's closing brace.
    /// A comma separates a new property from one already in the object, and from
    /// a property inserted just before it. A comma already sitting between the
    /// last value and that brace is the separator, so it is not written again:
    /// the reader accepts that trailing comma, and a second one would make the
    /// document unreadable.
    /// </summary>
    private static byte[] AppendProperties(byte[] original, List<(string Name, byte[] Value)> properties)
    {
        int closingBrace = Array.LastIndexOf(original, (byte)'}');
        if (closingBrace < 0)
        {
            throw new InvalidDataException("The state file is not a JSON object.");
        }

        bool hasProperties = original.AsSpan(0, closingBrace).IndexOf((byte)':') >= 0;
        bool separatorAlreadyPresent = HasTrailingComma(original, closingBrace);
        byte[] insertion = [];
        foreach ((string name, byte[] value) in properties)
        {
            bool needsComma = hasProperties && !separatorAlreadyPresent;
            byte[] prefix = Encoding.UTF8.GetBytes((needsComma ? "," : string.Empty) + "\n  \"" + name + "\": ");
            insertion = [.. insertion, .. prefix, .. value];
            hasProperties = true;
            separatorAlreadyPresent = false;
        }

        insertion = [.. insertion, (byte)'\n'];
        return Splice(original, closingBrace, 0, insertion);
    }

    /// <summary>
    /// True when the last non-whitespace byte before the root object's closing
    /// brace is a comma. JSON whitespace is space, tab, line feed, and carriage
    /// return; anything else, including a comma inside a string, stays put.
    /// </summary>
    private static bool HasTrailingComma(byte[] original, int closingBrace)
    {
        int index = closingBrace - 1;
        while (index >= 0 && IsJsonWhitespace(original[index]))
        {
            index--;
        }

        return index >= 0 && original[index] == (byte)',';
    }

    private static bool IsJsonWhitespace(byte value) =>
        value is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r';

    private readonly record struct ValueSpan(int Start, int Length, JsonTokenType TokenType);
}
