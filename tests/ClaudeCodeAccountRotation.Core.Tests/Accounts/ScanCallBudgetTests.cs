using ClaudeCodeAccountRotation.Core.Accounts;

namespace ClaudeCodeAccountRotation.Core.Tests.Accounts;

/// <summary>The per-click call counter: a ceiling of 30 gives 30 calls and no 31st.</summary>
public sealed class ScanCallBudgetTests
{
    [Fact]
    public void ThirtyCallsAreGivenAndTheThirtyFirstIsRefused()
    {
        ScanCallBudget budget = new(30);

        Enumerable.Range(0, 30).Select(_ => budget.TryTake()).ShouldAllBe(static taken => taken);

        budget.TryTake().ShouldBeFalse();
        budget.Taken.ShouldBe(30);
    }

    [Fact]
    public void ANewBudgetHasTakenNone() => new ScanCallBudget(30).Taken.ShouldBe(0);

    [Fact]
    public void AZeroCeilingGivesNothing()
    {
        ScanCallBudget budget = new(0);

        budget.TryTake().ShouldBeFalse();
        budget.Taken.ShouldBe(0);
    }
}
