using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Ports;

namespace ClaudeCodeAccountRotation.App.Adapters.SecretStore;

/// <summary>
/// The store on a platform with none this tool supports: every call is
/// refused with the reason, a delete included, since it cannot show a key is gone.
/// </summary>
internal sealed class UnavailableSecretStore(string reason) : ISecretStore
{
    public Task<Result<Unit, string>> ProbeAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Result<Unit, string>.Failure(reason));

    public Task<Result<Unit, string>> WriteAsync(SecretKind kind, AccountEmail email, string value, CancellationToken cancellationToken) =>
        Task.FromResult(Result<Unit, string>.Failure(reason));

    public Task<Result<string?, string>> ReadAsync(SecretKind kind, AccountEmail email, CancellationToken cancellationToken) =>
        Task.FromResult(Result<string?, string>.Failure(reason));

    public Task<Result<Unit, string>> DeleteAsync(SecretKind kind, AccountEmail email, CancellationToken cancellationToken) =>
        Task.FromResult(Result<Unit, string>.Failure(reason));
}
