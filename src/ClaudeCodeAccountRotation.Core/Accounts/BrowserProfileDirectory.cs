namespace ClaudeCodeAccountRotation.Core.Accounts;

/// <summary>
/// The one rule for a browser-profile directory name, shared by the roster
/// endpoints that accept one and the launcher that emits one. The value
/// becomes <c>--profile-directory=&lt;value&gt;</c>, which the browser appends
/// to its own user-data directory without normalizing it, so anything that is
/// not exactly one plain directory name can select a profile the roster never
/// named: a separator or a dot-segment walks elsewhere, a trailing dot or an
/// NTFS stream suffix aliases a sibling on Windows, a control character
/// truncates the command line, and a reserved device name never opens.
/// The roster stores the name exactly as the browser wrote it, so nothing is
/// trimmed or repaired here; a value that fails is refused with the reason.
/// </summary>
public static class BrowserProfileDirectory
{
    private const int MaximumLength = 255;

    private static readonly System.Buffers.SearchValues<char> _refused = System.Buffers.SearchValues.Create("/\\<>:\"|?*");

    // Win32's list, including the three superscript digits it accepts as
    // digits in a device name (COM¹ is COM1 to the kernel, in every directory).
    private static readonly HashSet<string> _reservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    public static Result<string, string> Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0 || value.Trim().Length != value.Length)
        {
            return Failure("carries leading or trailing whitespace");
        }

        if (value.Length > MaximumLength)
        {
            return Failure("is longer than any directory name");
        }

        if (value.AsSpan().IndexOfAny(_refused) >= 0)
        {
            return Failure("contains a path separator or a character no directory name can hold");
        }

        if (value.Any(char.IsControl))
        {
            return Failure("contains a control character");
        }

        if (value is "." or ".." || value.EndsWith('.'))
        {
            return Failure("is a dot-segment or ends in a dot");
        }

        // "CON.txt" is still CON to Win32; the check is on the stem.
        int dot = value.IndexOf('.', StringComparison.Ordinal);
        string stem = dot < 0 ? value : value[..dot];
        return _reservedDeviceNames.Contains(stem)
            ? Failure("is a reserved device name")
            : Result<string, string>.Success(value);
    }

    private static Result<string, string> Failure(string reason) =>
        Result<string, string>.Failure("a browser profile directory must be a single directory name such as \"Profile 3\"; this one " + reason);
}
