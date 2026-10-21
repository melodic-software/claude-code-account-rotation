using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.Process;
using ClaudeCodeAccountRotation.Core;

namespace ClaudeCodeAccountRotation.App.Tests.Adapters;

/// <summary>
/// A variable GitHub answers 404 for reads as absent, so the kill-switch
/// table decides its state, rather than as a read that failed.
/// </summary>
public sealed class GhApiReaderVariableTests
{
    private const string Path404 = "orgs/zz-no-such-org/actions/variables/CLAUDE_LANES_DISABLED";

    /// <summary>
    /// The <c>gh api -i --method GET</c> answer for this path, recorded against
    /// an organization that does not exist (gh 2.x, 2026-10-21), headers
    /// trimmed: printed on standard output, exit 1.
    /// </summary>
    private const string Recorded404 =
        "HTTP/2.0 404 Not Found\r\n"
        + "Content-Type: application/json; charset=utf-8\r\n"
        + "X-Ratelimit-Limit: 5000\r\n"
        + "X-Ratelimit-Remaining: 4722\r\n"
        + "\r\n"
        + "{\"message\":\"Not Found\",\"documentation_url\":\"https://docs.github.com/rest/actions/variables#get-an-organization-variable\",\"status\":\"404\"}";

    [Fact]
    public void ARecordedNotFoundCarriesItsStatus() =>
        GhApiReader.Read(Path404, new GhExit(1, Recorded404, "gh: Not Found (HTTP 404)")).Error.Status.ShouldBe(404);

    [Fact]
    public async Task ARecordedNotFoundReadsAsNoVariable()
    {
        Result<string?, GhApiFailure> read = await new CannedReader(GhApiReader.Read(Path404, new GhExit(1, Recorded404, "gh: Not Found (HTTP 404)")))
            .GetVariableAsync(Path404, TestContext.Current.CancellationToken);

        read.IsSuccess.ShouldBeTrue();
        read.Value.ShouldBeNull();
    }

    [Fact]
    public async Task AForbiddenIsAFailedReadNotAnAbsentVariable()
    {
        Result<string?, GhApiFailure> read = await new CannedReader(GhApiReader.Read(
                Path404,
                new GhExit(1, "HTTP/2.0 403 Forbidden\nX-Ratelimit-Remaining: 4000\n\n{\"message\":\"Must have admin rights\"}", string.Empty)))
            .GetVariableAsync(Path404, TestContext.Current.CancellationToken);

        read.IsFailure.ShouldBeTrue();
        read.Error.Status.ShouldBe(403);
    }

    private sealed class CannedReader(Result<JsonNode, GhApiFailure> answer) : IGhApiReader
    {
        public Task<Result<JsonNode, GhApiFailure>> GetAsync(string path, CancellationToken cancellationToken) => Task.FromResult(answer);
    }
}
