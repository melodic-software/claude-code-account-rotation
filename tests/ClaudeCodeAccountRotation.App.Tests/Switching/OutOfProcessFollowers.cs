namespace ClaudeCodeAccountRotation.App.Tests.Switching;

/// <summary>
/// The two crash-injection suites, which launch and kill published followers,
/// share one collection so they run one after the other rather than beside each
/// other. Twenty-four out-of-process followers at once starve the timing-bound
/// facts elsewhere in the run — the state-file watcher's repair budget is ten
/// seconds, and one full run failed it here and passed on a re-run — and a test
/// that fails because the machine was busy proves nothing about the product.
/// Twelve at once is what this suite already ran at before the release
/// direction was added, so this restores that peak rather than inventing a new
/// one; the collection is not marked <c>DisableParallelization</c>, which would
/// serialize it against every other collection as well.
/// </summary>
[CollectionDefinition(Name)]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515:Consider making public types internal", Justification = "xUnit1027 requires a collection definition class to be public.")]
public sealed class OutOfProcessFollowers
{
    public const string Name = "out-of-process followers";
}
