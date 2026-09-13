using NBomber.Contracts.Stats;

namespace EbenezerBackend.LoadTests.Reporting;

/// <summary>
/// Adapter that projects <see cref="NodeStats"/> into a flat sequence of
/// <see cref="StepFailure"/> records, isolating the concrete NBomber API from the
/// pure aggregation logic in <see cref="ErrorReportBuilder"/>.
/// </summary>
public static class ErrorReportExtensions
{
    /// <summary>
    /// Iterates all scenario/step failure status codes in <paramref name="stats"/>
    /// and yields one <see cref="StepFailure"/> per entry whose <c>Count</c> is
    /// greater than zero.
    /// </summary>
    /// <param name="stats">The <see cref="NodeStats"/> returned by <c>NBomberRunner.Run()</c>.</param>
    /// <returns>
    /// A lazy sequence of <see cref="StepFailure"/> values, one per distinct
    /// (StepName, StatusCode/Message) combination that has at least one recorded failure.
    /// </returns>
    public static IEnumerable<StepFailure> ExtractFailures(NodeStats stats)
    {
        foreach (var scenario in stats.ScenarioStats)
            foreach (var step in scenario.StepStats)
                foreach (var sc in step.Fail.StatusCodes)
                    if (sc.Count > 0)
                        yield return new StepFailure(
                            scenario.ScenarioName,
                            step.StepName,
                            sc.Message,
                            sc.StatusCode,
                            sc.Count);
    }
}
