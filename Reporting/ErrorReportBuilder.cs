using System.Collections.Generic;
using System.Linq;

namespace EbenezerBackend.LoadTests.Reporting;

/// <summary>
/// Pure aggregation logic for the error-by-endpoint CSV report.
/// All methods are deterministic and perform no I/O.
/// </summary>
public static class ErrorReportBuilder
{
    /// <summary>Reserved scenario identifier used when the scenario name is null or whitespace (Req 7.5).</summary>
    public const string UnknownScenario = "unknown";

    /// <summary>Reserved endpoint identifier used when the step name is not a known endpoint (Req 3.6).</summary>
    public const string UnknownEndpoint = "unknown";

    /// <summary>Reserved message identifier used when no descriptive message or status code is available (Req 3.5).</summary>
    public const string UnknownMessage = "unknown";

    /// <summary>Fixed header columns for the errors-by-endpoint CSV report (Req 7.1, 2.2).</summary>
    public static IReadOnlyList<string> Header => new[] { "scenario", "endpoint", "error_message", "count" };

    /// <summary>
    /// Normalizes the scenario name of a failure (Req 7.1, 7.5).
    /// <list type="bullet">
    ///   <item>Non-blank <paramref name="scenarioName"/> → <paramref name="scenarioName"/></item>
    ///   <item>Else → <see cref="UnknownScenario"/> (<c>"unknown"</c>)</item>
    /// </list>
    /// </summary>
    /// <param name="scenarioName">Scenario name from the step failure; may be null or whitespace.</param>
    /// <returns>The resolved, non-null scenario name string.</returns>
    public static string ResolveScenario(string? scenarioName)
    {
        if (!string.IsNullOrWhiteSpace(scenarioName))
            return scenarioName;

        return UnknownScenario;
    }

    /// <summary>
    /// Normalizes the error message of a failure (Req 3.4, 3.5).
    /// <list type="bullet">
    ///   <item>Non-blank <paramref name="message"/> → <paramref name="message"/></item>
    ///   <item>Else non-blank <paramref name="statusCode"/> → <paramref name="statusCode"/></item>
    ///   <item>Else → <see cref="UnknownMessage"/> (<c>"unknown"</c>)</item>
    /// </list>
    /// </summary>
    /// <param name="message">Descriptive error message from the step failure; may be null or whitespace.</param>
    /// <param name="statusCode">Status code string used as fallback; may be null or whitespace.</param>
    /// <returns>The resolved, non-null error message string.</returns>
    public static string ResolveMessage(string? message, string? statusCode)
    {
        if (!string.IsNullOrWhiteSpace(message))
            return message;

        if (!string.IsNullOrWhiteSpace(statusCode))
            return statusCode;

        return UnknownMessage;
    }

    /// <summary>
    /// Normalizes the endpoint of a failure (Req 3.1, 3.6).
    /// <list type="bullet">
    ///   <item><paramref name="knownEndpoints"/> is <c>null</c> OR contains <paramref name="stepName"/> → <paramref name="stepName"/></item>
    ///   <item><paramref name="knownEndpoints"/> is provided and does NOT contain <paramref name="stepName"/> → <see cref="UnknownEndpoint"/> (<c>"unknown"</c>)</item>
    /// </list>
    /// The original <paramref name="stepName"/> is preserved in the <see cref="StepFailure"/> record (Req 3.6).
    /// </summary>
    /// <param name="stepName">The step name from NBomber statistics; used as the endpoint identifier.</param>
    /// <param name="knownEndpoints">
    /// Optional set of known endpoint names. When <c>null</c>, all step names are treated as valid endpoints.
    /// </param>
    /// <returns>The resolved endpoint string.</returns>
    public static string ResolveEndpoint(string stepName, ISet<string>? knownEndpoints)
    {
        if (knownEndpoints is null || knownEndpoints.Contains(stepName))
            return stepName;

        return UnknownEndpoint;
    }

    /// <summary>
    /// Aggregates failures into ordered ErrorRow list.
    /// Groups by (scenario, endpoint, error_message), sums Count.
    /// Sorted by scenario (ordinal asc), then endpoint (ordinal asc), then error_message (ordinal asc).
    /// </summary>
    /// <param name="failures">Step failures to aggregate. Must not be null.</param>
    /// <param name="knownEndpoints">
    /// Optional set of known endpoint names. When <c>null</c>, all step names are
    /// treated as valid endpoints. When provided, unrecognized step names resolve to
    /// <see cref="UnknownEndpoint"/> and the original step name is appended to the
    /// error message (Req 3.6).
    /// </param>
    /// <returns>
    /// An immutable, deterministically ordered list of <see cref="ErrorRow"/> instances.
    /// </returns>
    public static IReadOnlyList<ErrorRow> Build(IEnumerable<StepFailure> failures,
                                                ISet<string>? knownEndpoints = null)
    {
        var aggregated = new Dictionary<(string scenario, string endpoint, string message), long>();

        foreach (var failure in failures)
        {
            var scenario = ResolveScenario(failure.ScenarioName);
            var endpoint = ResolveEndpoint(failure.StepName, knownEndpoints);
            var message  = ResolveMessage(failure.Message, failure.StatusCode);

            // Req 3.6: when step name falls into unknown, preserve original name in message
            if (endpoint == UnknownEndpoint)
                message = $"{message} (step: {failure.StepName})";

            var key = (scenario, endpoint, message);
            aggregated.TryGetValue(key, out var existing);
            aggregated[key] = existing + failure.Count;
        }

        return aggregated
            .OrderBy(kvp => kvp.Key.scenario, StringComparer.Ordinal)
            .ThenBy(kvp => kvp.Key.endpoint,  StringComparer.Ordinal)
            .ThenBy(kvp => kvp.Key.message,   StringComparer.Ordinal)
            .Select(kvp => new ErrorRow(kvp.Key.scenario, kvp.Key.endpoint, kvp.Key.message, kvp.Value))
            .ToList()
            .AsReadOnly();
    }
}
