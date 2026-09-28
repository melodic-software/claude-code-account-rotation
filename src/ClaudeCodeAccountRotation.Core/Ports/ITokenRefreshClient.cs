using ClaudeCodeAccountRotation.Core.Quota;

namespace ClaudeCodeAccountRotation.Core.Ports;

/// <summary>
/// Renews one parked pair's access token, which also rotates its refresh token.
/// The login's own four-week lifetime is fixed and does not move. The implementation returns
/// the rotated tokens to the caller and writes nothing: only
/// <see cref="ICredentialPairStore"/> touches a credential file. The caller owns
/// the audit line for a rotation, recording the refresh token's fingerprint and
/// never the token itself.
/// </summary>
public interface ITokenRefreshClient
{
    Task<Result<RefreshedTokens, UsageReadFailure>> RefreshAsync(string refreshToken, CancellationToken cancellationToken);
}
