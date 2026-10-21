using ClaudeCodeAccountRotation.Core.Ports;

namespace ClaudeCodeAccountRotation.Core.Accounts;

/// <summary>
/// Which listed key a stored key is: <see cref="Key"/> when found, else null
/// with the <see cref="Reason"/> it cannot be told.
/// </summary>
public sealed record KeyHintMatch
{
    private KeyHintMatch(ListedApiKey? key, string? reason)
    {
        Key = key;
        Reason = reason;
    }

    public ListedApiKey? Key { get; }

    public string? Reason { get; }

    public static KeyHintMatch Found(ListedApiKey key) => new(key, null);

    public static KeyHintMatch CannotTell(string reason) => new(null, reason);
}

/// <summary>
/// Finds a stored key in the Admin API's listing by its <c>partial_key_hint</c>,
/// shown as a prefix, <c>...</c>, and a suffix (the documented example ends <c>R2D...igAA</c>).
/// No spec or collision rule for the hint is published, so this fails closed:
/// a key is found only when exactly one hint fits it and every listed hint
/// could be read, since an unreadable one might be the same key.
/// </summary>
public static class PartialKeyHint
{
    private const string Elision = "...";

    public static KeyHintMatch Match(string key, IEnumerable<ListedApiKey> listed)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(listed);
        List<ListedApiKey> fits = [];
        int unreadable = 0;
        foreach (ListedApiKey candidate in listed)
        {
            bool? fit = Fits(candidate.PartialKeyHint, key);
            if (fit is null)
            {
                unreadable++;
            }
            else if (fit.Value)
            {
                fits.Add(candidate);
            }
        }

        return (fits.Count, unreadable) switch
        {
            (1, 0) => KeyHintMatch.Found(fits[0]),
            (_, > 0) => KeyHintMatch.CannotTell("the listing gives no readable hint for " + Count(unreadable) + ", which could be this one"),
            (0, _) => KeyHintMatch.CannotTell("no listed key's hint fits this key"),
            _ => KeyHintMatch.CannotTell(Count(fits.Count) + " fit this key's hint"),
        };
    }

    /// <summary>Whether the hint fits the key, or null when the hint is missing or not prefix, <c>...</c>, suffix.</summary>
    private static bool? Fits(string? hint, string key)
    {
        if (hint is null)
        {
            return null;
        }

        int at = hint.IndexOf(Elision, StringComparison.Ordinal);
        if (at <= 0 || at + Elision.Length >= hint.Length || hint.IndexOf(Elision, at + 1, StringComparison.Ordinal) >= 0)
        {
            return null;
        }

        string prefix = hint[..at];
        string suffix = hint[(at + Elision.Length)..];
        return key.Length > prefix.Length + suffix.Length
            && key.StartsWith(prefix, StringComparison.Ordinal)
            && key.EndsWith(suffix, StringComparison.Ordinal);
    }

    private static string Count(int keys) =>
        keys.ToString(System.Globalization.CultureInfo.InvariantCulture) + (keys == 1 ? " key" : " keys");
}
