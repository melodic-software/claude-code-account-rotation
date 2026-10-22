namespace ClaudeCodeAccountRotation.Core.Accounts;

/// <summary>
/// The GitHub calls one limit-stop scan may make: each call takes one before
/// it is made, and none is given back. Not thread-safe: one scan, one budget.
/// </summary>
public sealed class ScanCallBudget(int ceiling)
{
    /// <summary>The calls taken so far.</summary>
    public int Taken { get; private set; }

    /// <summary>Takes one call, or returns false, taking none, when the ceiling is reached.</summary>
    public bool TryTake()
    {
        if (Taken >= ceiling)
        {
            return false;
        }

        Taken++;
        return true;
    }
}
