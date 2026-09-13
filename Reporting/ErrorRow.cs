namespace EbenezerBackend.LoadTests.Reporting;

/// <summary>
/// Represents a single data row in the <c>errors-by-endpoint.csv</c> report.
/// Each instance corresponds to one distinct (Scenario, Endpoint, ErrorMessage) triplet
/// and its total occurrence count (Req 7.3).
/// </summary>
/// <param name="Scenario">Scenario identifier (scenario name or "unknown") (Req 7.1, 7.5).</param>
/// <param name="Endpoint">Endpoint identifier (step name or "unknown") (Req 3.1, 3.6).</param>
/// <param name="ErrorMessage">Resolved error message (Req 3.4, 3.5).</param>
/// <param name="Count">Total occurrences of this triplet; ≥ 1 (Req 2.5).</param>
public sealed record ErrorRow(
    string Scenario,
    string Endpoint,
    string ErrorMessage,
    long Count);
