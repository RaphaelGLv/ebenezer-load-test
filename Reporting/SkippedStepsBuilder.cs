namespace EbenezerBackend.LoadTests.Reporting;

public static class SkippedStepsBuilder
{
    public static IReadOnlyList<string> Header =>
        new[] { "scenario", "step_name", "reason" };

    /// <summary>Ordena os registros: scenario (ordinal asc), step_name (ordinal asc).</summary>
    public static IReadOnlyList<SkippedStep> Sort(IEnumerable<SkippedStep> records) =>
        records
            .OrderBy(s => s.Scenario, StringComparer.Ordinal)
            .ThenBy(s => s.StepName, StringComparer.Ordinal)
            .ToList()
            .AsReadOnly();
}
