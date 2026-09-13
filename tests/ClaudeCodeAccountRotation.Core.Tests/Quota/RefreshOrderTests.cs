using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Quota;

namespace ClaudeCodeAccountRotation.Core.Tests.Quota;

/// <summary>
/// Pins the order a pass reads accounts in. If the endpoint's rate bucket turns
/// out to be shared across the client rather than kept per token, a pass
/// populates only what the window allows, so the accounts it could not reach
/// must be the ones the next pass starts with; ordering by last successful read
/// is what makes successive passes rotate coverage instead of starving a tail.
/// </summary>
public sealed class RefreshOrderTests
{
    private static readonly DateTimeOffset _noon = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    private static RefreshCandidate Candidate(string email, DateTimeOffset? lastReadAt) =>
        new(new AccountEmail(email), lastReadAt);

    [Fact]
    public void AnAccountNeverReadComesFirst()
    {
        IReadOnlyList<RefreshCandidate> ordered = RefreshOrder.Order(
        [
            Candidate("dev.a@example.com", _noon),
            Candidate("dev.b@example.com", null),
        ]);

        ordered.Select(candidate => candidate.Email.Value).ShouldBe(["dev.b@example.com", "dev.a@example.com"]);
    }

    [Fact]
    public void TheOldestReadComesBeforeTheNewest()
    {
        IReadOnlyList<RefreshCandidate> ordered = RefreshOrder.Order(
        [
            Candidate("dev.a@example.com", _noon),
            Candidate("dev.b@example.com", _noon.AddMinutes(-30)),
            Candidate("dev.c@example.com", _noon.AddMinutes(-10)),
        ]);

        ordered.Select(candidate => candidate.Email.Value)
            .ShouldBe(["dev.b@example.com", "dev.c@example.com", "dev.a@example.com"]);
    }

    [Fact]
    public void AccountsReadAtTheSameInstantAreOrderedByEmail()
    {
        // Ten accounts read in one pass share an instant often enough that an
        // unspecified tie order would make successive passes non-reproducible.
        IReadOnlyList<RefreshCandidate> ordered = RefreshOrder.Order(
        [
            Candidate("dev.c@example.com", _noon),
            Candidate("dev.a@example.com", _noon),
            Candidate("dev.b@example.com", _noon),
        ]);

        ordered.Select(candidate => candidate.Email.Value)
            .ShouldBe(["dev.a@example.com", "dev.b@example.com", "dev.c@example.com"]);
    }
}
