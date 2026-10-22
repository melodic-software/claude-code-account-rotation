using System.Globalization;
using ClaudeCodeAccountRotation.App.Adapters.Process;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Accounts;

namespace ClaudeCodeAccountRotation.App.Endpoints;

/// <summary>
/// The reads of one limit-stop scan. Per lane repository, newest first: a
/// page of the failed runs of the last <see cref="LimitStopScan.Window"/>,
/// then one GraphQL batch reading the check runs of every attempt of those
/// runs and every job's annotations, failed or not; a check run whose
/// annotations the batch cut off gets one REST read. Every call comes from
/// one <see cref="ScanCallBudget"/> of <see cref="LimitStopScan.MaxCalls"/>.
/// <see cref="ListingRange"/> pages the listing and <see cref="RunCoverage"/>
/// says how far back each repository was read. A run of a fork's pull request
/// is skipped. A failed read ends its repository in part; the budget,
/// GitHub's rate limit, or a batch leaving fewer than
/// <see cref="LimitStopScan.GraphQlPointFloor"/> points ends the scan, and
/// each repository not reached is named. Nothing retries.
/// </summary>
internal sealed class LimitStopScanRunner
{
    private readonly IGhApiReader _reader;
    private readonly IGhGraphQlReader _graphQl;
    private readonly ScanCallBudget _budget = new(LimitStopScan.MaxCalls);
    private bool _stopped;

    private LimitStopScanRunner(IGhApiReader reader, IGhGraphQlReader graphQl)
    {
        _reader = reader;
        _graphQl = graphQl;
    }

    public List<LaneRecord> Records { get; } = [];

    public List<string> Problems { get; } = [];

    public List<RepositoryCoverageView> Coverage { get; } = [];

    public int RunsRead { get; private set; }

    public int Skipped { get; private set; }

    public int Calls => _budget.Taken;

    public static async Task<LimitStopScanRunner> RunAsync(IReadOnlyList<string> repositories, IGhApiReader reader, IGhGraphQlReader graphQl, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repositories);
        LimitStopScanRunner scan = new(reader, graphQl);
        foreach (string repository in repositories)
        {
            if (scan._stopped)
            {
                scan.Problems.Add(repository + ": not reached, as the check stopped before it");
                scan.Coverage.Add(new RepositoryCoverageView(repository, false, null));
                continue;
            }

            scan.Coverage.Add(await scan.ReadRepositoryAsync(repository, now - LimitStopScan.Window, cancellationToken));
        }

        return scan;
    }

    private async Task<RepositoryCoverageView> ReadRepositoryAsync(string repository, DateTimeOffset since, CancellationToken cancellationToken)
    {
        RunCoverage coverage = new();
        HashSet<long> seen = [];
        var range = ListingRange.First(since);
        while (range is not null)
        {
            if (!Take())
            {
                return new RepositoryCoverageView(repository, false, coverage.Back);
            }

            Result<RunPage, GhApiFailure> listed = await _reader.ListFailedRunsPageAsync(repository, range, cancellationToken);
            if (listed.IsFailure)
            {
                Fail(repository, listed.Error);
                return new RepositoryCoverageView(repository, false, coverage.Back);
            }

            List<ListedRun> runs = [.. listed.Value.Runs.Where(listedRun => listedRun.Run.CreatedAt >= since && seen.Add(listedRun.Run.Id))];
            if (runs.Count > 0 && !await ReadBatchAsync(repository, runs, coverage, cancellationToken))
            {
                return new RepositoryCoverageView(repository, false, coverage.Back);
            }

            range = range.After([.. listed.Value.Runs.Select(static listedRun => listedRun.Run)]);
        }

        return new RepositoryCoverageView(repository, true, coverage.Back);
    }

    /// <summary>
    /// Reads the check runs and annotations of <paramref name="runs"/>, newest
    /// first, in one GraphQL call: true when every one so far was read in full.
    /// The point floor ends the scan, not this repository: a last page just
    /// read still completes it.
    /// </summary>
    private async Task<bool> ReadBatchAsync(string repository, List<ListedRun> runs, RunCoverage coverage, CancellationToken cancellationToken)
    {
        List<string> ids = [.. runs.Where(static run => !run.FromFork).Select(static run => run.CheckSuiteNodeId).OfType<string>()];
        SuiteBatch batch = new(int.MaxValue, []);
        if (ids.Count > 0)
        {
            if (!Take())
            {
                return false;
            }

            Result<SuiteBatch, GhApiFailure> read = await _graphQl.ReadCheckSuitesAsync(ids, cancellationToken);
            if (read.IsFailure)
            {
                Fail(repository, read.Error);
                return false;
            }

            batch = read.Value;
        }

        foreach (ListedRun listed in runs)
        {
            string named = repository + ": run " + listed.Run.Id.ToString(CultureInfo.InvariantCulture);
            bool whole;
            if (listed.FromFork)
            {
                Skipped++;
                whole = true;
            }
            else if (listed.CheckSuiteNodeId is not string id)
            {
                whole = Problem(named + " named no check suite, so its annotations were not read");
            }
            else
            {
                // A null suite is a run deleted since it was listed: nothing left to read.
                whole = batch.Suites[ids.IndexOf(id)] is not SuiteRead suite || await ReadSuiteAsync(repository, listed.Run, suite, cancellationToken);
                RunsRead += whole ? 1 : 0;
            }

            coverage.Read(listed.Run, whole);
        }

        if (batch.Remaining < LimitStopScan.GraphQlPointFloor)
        {
            Stop("GitHub's GraphQL points fell to " + batch.Remaining.ToString("N0", CultureInfo.InvariantCulture) + ", under the "
                + LimitStopScan.GraphQlPointFloor.ToString("N0", CultureInfo.InvariantCulture) + " the check leaves for other work, so it stopped there");
        }

        return coverage.Contiguous;
    }

    /// <summary>The records of one check suite's check runs: true when none was cut off.</summary>
    private async Task<bool> ReadSuiteAsync(string repository, LaneRun run, SuiteRead suite, CancellationToken cancellationToken)
    {
        string named = repository + ": run " + run.Id.ToString(CultureInfo.InvariantCulture);
        bool whole = !suite.MoreCheckRuns
            || Problem(named + " has more than " + GhReadQueries.CheckRunsPerSuite.ToString(CultureInfo.InvariantCulture) + " check runs, so only that many were read");
        foreach (CheckRunRead checkRun in suite.CheckRuns)
        {
            IReadOnlyList<string> messages = checkRun.Messages;
            if (checkRun.MoreAnnotations)
            {
                if (!Take())
                {
                    whole = false;
                }
                else
                {
                    Result<IReadOnlyList<string>, GhApiFailure> rest = await _reader.ListAnnotationMessagesAsync(repository, checkRun.Id, cancellationToken);
                    if (rest.IsFailure)
                    {
                        Fail(repository, rest.Error);
                        whole = false;
                    }
                    else
                    {
                        messages = rest.Value;
                        whole &= rest.Value.Count < GhApiReads.AnnotationsPerPage
                            || Problem(named + " has a check run with more than " + GhApiReads.AnnotationsPerPage.ToString(CultureInfo.InvariantCulture) + " annotations, so only that many were read");
                    }
                }
            }

            Records.AddRange(messages.Select(LaneOutcomes.Parse).OfType<LaneOutcome>().Select(outcome => new LaneRecord(run, outcome)));
        }

        return whole;
    }

    /// <summary>Takes one call from the budget; when none is left, ends the scan and says so once.</summary>
    private bool Take()
    {
        if (_stopped)
        {
            return false;
        }

        if (_budget.TryTake())
        {
            return true;
        }

        Stop("the check used its " + LimitStopScan.MaxCalls.ToString(CultureInfo.InvariantCulture) + " GitHub calls and stopped there");
        return false;
    }

    /// <summary>Notes a failed read: a rate limit ends the scan; anything else ends only its repository.</summary>
    private void Fail(string repository, GhApiFailure failure)
    {
        if (failure.RateLimited)
        {
            Stop(failure.Message);
        }
        else
        {
            Problems.Add(repository + ": " + failure.Message);
        }
    }

    private void Stop(string problem)
    {
        if (!_stopped)
        {
            Problems.Add(problem);
            _stopped = true;
        }
    }

    /// <summary>Notes a problem; always false, so a read it ends reads as cut off.</summary>
    private bool Problem(string problem)
    {
        Problems.Add(problem);
        return false;
    }
}
