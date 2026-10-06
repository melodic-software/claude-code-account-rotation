using System.Threading.Channels;
using ClaudeCodeAccountRotation.App.Adapters.Process;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Accounts;

namespace ClaudeCodeAccountRotation.App.Tests;

/// <summary>
/// Stands in for a running <c>claude setup-token</c> as a pseudo-terminal hands
/// it over: the screen's text with colors and cursor moves between the words,
/// the sign-in URL on one row, and, once a code is typed, either the token or
/// the retry screen. What a code does is the test's to choose.
/// </summary>
internal sealed class SetupTokenScript
{
    private const char Escape = (char)0x1b;

    /// <summary>
    /// The prefix setup-token's tokens carry. Every fake token in these tests is
    /// built from it at run time, so the source holds nothing a secret scanner
    /// would take for a real one.
    /// </summary>
    public const string TokenPrefix = "sk-ant-" + "oat01-";

    /// <summary>A fake token of a real one's shape: low entropy by construction, and never a literal.</summary>
    public static readonly string Token = FakeToken('T', 95);

    public static string FakeToken(char filler, int length) => TokenPrefix + new string(filler, length) + "AA";

    public const string SignInUrl =
        "https://claude.com/cai/oauth/authorize?code=true&client_id=cli&response_type=code&scope=user%3Ainference&code_challenge=c0de&code_challenge_method=S256&state=scripted";

    public List<ScriptedSetupTokenChild> Children { get; } = [];

    public ScriptedSetupTokenChild Last => Children[^1];

    public string? StartError { get; set; }

    public bool PrintsUrl { get; set; } = true;

    /// <summary>The default answer to a code: the success screen, colored and cursor-moved the way a terminal renders it.</summary>
    public Action<ScriptedSetupTokenChild, string> OnCode { get; set; } = static (child, _) => child.Emit(SuccessScreen(Token));

    public static string SuccessScreen(string token) =>
        Escape + "[2K" + Escape + "[32m✓ Long-lived authentication token created successfully!" + Escape + "[39m\r\n\r\n"
        + "Your OAuth token (valid for 1 year):\r\n\r\n"
        + Escape + "[1m" + token + Escape + "[22m\r\n\r\n"
        + "Store this token securely. You won't be able to see it again.\r\n";

    public static string RetryScreen =>
        Escape + "[31mOAuth error: Request failed with status code 400" + Escape + "[39m\r\n\r\n Press Enter to retry.\r\n";

    public Result<ILoginChild, string> Start(IReadOnlyList<string> arguments, string configDirectory)
    {
        if (StartError is string error)
        {
            return Result<ILoginChild, string>.Failure(error);
        }

        ScriptedSetupTokenChild child = new(this, arguments, configDirectory);
        Children.Add(child);
        return Result<ILoginChild, string>.Success(child);
    }
}

internal sealed class ScriptedSetupTokenChild : ILoginChild
{
    private const char Escape = (char)0x1b;
    private readonly SetupTokenScript _script;
    private readonly Channel<string> _chunks = Channel.CreateUnbounded<string>();

    public ScriptedSetupTokenChild(SetupTokenScript script, IReadOnlyList<string> arguments, string configDirectory)
    {
        _script = script;
        Arguments = [.. arguments];
        ConfigDirectory = configDirectory;
        Emit(Escape + "[?25lWelcome to Claude Code v2.1.290\r\n\r\n");
        if (script.PrintsUrl)
        {
            // Split across two reads, the way a pipe can deliver it.
            Emit(" Browser didn't open? Use the url below to sign in (c to copy)\r\n\r\n" + Escape + "[2m" + SetupTokenScript.SignInUrl[..40]);
            Emit(SetupTokenScript.SignInUrl[40..] + Escape + "[22m\r\n\r\n Paste code here if prompted > ");
        }
    }

    public IReadOnlyList<string> Arguments { get; }

    public string ConfigDirectory { get; }

    public List<string> CodesWritten { get; } = [];

    public bool Killed { get; private set; }

    public void Emit(string chunk) => _chunks.Writer.TryWrite(chunk);

    public void Exit() => _chunks.Writer.TryComplete();

    public async Task<string?> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _chunks.Reader.ReadAsync(cancellationToken);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    public Task WriteCodeAsync(string code, CancellationToken cancellationToken)
    {
        CodesWritten.Add(code);
        _script.OnCode(this, code);
        return Task.CompletedTask;
    }

    public void Kill()
    {
        Killed = true;
        _chunks.Writer.TryComplete();
    }

    public void Dispose() => Kill();
}

/// <summary>Records what would have gone to <c>gh</c>, and answers the way the test says.</summary>
internal sealed class RecordingSecretWriter : ICiSecretWriter
{
    public string? CheckError { get; set; }

    public string? SetError { get; set; }

    public List<(CiTokenSecret Secret, OrgSecretVisibility? Visibility, IReadOnlyList<string> Repositories, string Value)> Sets { get; } = [];

    public int Checks { get; private set; }

    public Task<Result<Unit, string>> CheckAsync(CancellationToken cancellationToken)
    {
        Checks++;
        return Task.FromResult(CheckError is string error ? Result<Unit, string>.Failure(error) : Result<Unit, string>.Success(Unit.Value));
    }

    public Task<Result<Unit, string>> SetAsync(
        CiTokenSecret secret,
        OrgSecretVisibility? visibility,
        IReadOnlyList<string> repositories,
        string value,
        CancellationToken cancellationToken)
    {
        Sets.Add((secret, visibility, repositories, value));
        return Task.FromResult(SetError is string error ? Result<Unit, string>.Failure(error) : Result<Unit, string>.Success(Unit.Value));
    }
}
