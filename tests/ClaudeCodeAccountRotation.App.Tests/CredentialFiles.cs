using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.Core.Identity;

namespace ClaudeCodeAccountRotation.App.Tests;

/// <summary>Builds credential files in the CLI's shape for adapter tests.</summary>
internal static class CredentialFiles
{
    public const string FileName = ".credentials.json";

    public static JsonObject Shape(string refreshToken, DateTimeOffset? accessTokenExpiresAt = null, DateTimeOffset? loginExpiresAt = null) =>
        new()
        {
            ["claudeAiOauth"] = new JsonObject
            {
                ["accessToken"] = "access-" + refreshToken,
                ["refreshToken"] = refreshToken,
                ["expiresAt"] = (accessTokenExpiresAt ?? DateTimeOffset.UtcNow.AddHours(8)).ToUnixTimeMilliseconds(),
                ["refreshTokenExpiresAt"] = (loginExpiresAt ?? DateTimeOffset.UtcNow.AddDays(28)).ToUnixTimeMilliseconds(),
                ["scopes"] = new JsonArray("user:inference", "user:profile"),
                ["subscriptionType"] = "max",
            },
        };

    public static CredentialPair Pair(string refreshToken) => CredentialPair.FromJson(Shape(refreshToken)).Value;

    public static async Task WriteAsync(string directory, string refreshToken, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, FileName), Shape(refreshToken).ToJsonString(), cancellationToken);
    }

    public static Task<RefreshTokenFingerprint?> FingerprintAsync(string directory, CancellationToken cancellationToken) =>
        ReadFingerprintAsync(Path.Combine(directory, FileName), cancellationToken);

    /// <summary>The fingerprint of a pair at an exact path, for the files the mailbox holds under names of its own.</summary>
    public static async Task<RefreshTokenFingerprint?> ReadFingerprintAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var raw = JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken));
        return CredentialPair.FromJson(raw!.AsObject()).Value.Fingerprint;
    }
}
