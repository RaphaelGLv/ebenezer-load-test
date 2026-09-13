namespace EbenezerBackend.LoadTests.Reporting;

/// <summary>
/// Neutral projection of an aggregated step failure from NBomber statistics.
/// Maps to <c>ScenarioStats.ScenarioName</c>, <c>StepStats.StepName</c>,
/// <c>StatusCodeStats.Message</c>, <c>StatusCodeStats.StatusCode</c>, and
/// <c>StatusCodeStats.Count</c>.
/// </summary>
/// <param name="ScenarioName">Name of the scenario (e.g. "write_heavy").</param>
/// <param name="StepName">Name of the step/endpoint (e.g. "01_register").</param>
/// <param name="Message">Descriptive error message; may be null or empty (Req 3.5).</param>
/// <param name="StatusCode">Status code string used as fallback message (Req 3.4).</param>
/// <param name="Count">Number of occurrences; must be ≥ 1 (Req 2.5).</param>
public sealed record StepFailure(
    string ScenarioName,
    string StepName,
    string? Message,
    string? StatusCode,
    long Count);
