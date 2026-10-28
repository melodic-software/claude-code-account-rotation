namespace ClaudeCodeAccountRotation.App.Tests;

/// <summary>
/// The test classes that write a stand-in script and run it at once. While a
/// script is open for writing, any other test that forks inherits that
/// descriptor until its child execs, and running the script then fails with
/// "Text file busy" (ETXTBSY). These classes run alone, with nothing beside
/// them that could fork, so a script is never written while another test
/// forks.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515:Consider making public types internal", Justification = "xUnit1027 requires a collection definition class to be public.")]
public sealed class StandInScripts
{
    public const string Name = "stand-in scripts";

    /// <summary>
    /// A shell expression for the monotonic clock in nanoseconds, the clock the
    /// write gate spaces by. <c>date +%s%N</c> reads the wall clock, which time
    /// sync can step back between two stamps.
    /// </summary>
    public const string MonotonicNanoseconds =
        "$(perl -MTime::HiRes=clock_gettime,CLOCK_MONOTONIC -e 'printf \"%.0f\", clock_gettime(CLOCK_MONOTONIC) * 1e9')";
}
