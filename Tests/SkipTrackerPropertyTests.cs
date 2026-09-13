// Feature: loadtest-reporting-improvements, Property 14: SkipTracker thread-safety
using EbenezerBackend.LoadTests.Reporting;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace EbenezerBackend.LoadTests.Tests;

/// <summary>
/// Property-based tests for <see cref="SkipTracker"/>.
/// Uses FsCheck 3.x C# API (FsCheck.Fluent namespace).
/// </summary>
public sealed class SkipTrackerPropertyTests
{
    // ---------------------------------------------------------------------------
    // Generators
    // ---------------------------------------------------------------------------

    private static readonly string[] ScenarioPool =
    {
        "write_heavy", "read_heavy", "graph_heavy", "document_heavy", "mixed_graph_documents",
    };

    private static readonly string[] StepPool =
    {
        "01_login", "02_register", "03_create", "04_update",
        "05_delete", "06_get", "07_list", "08_query",
    };

    private static readonly string[] ReasonPool =
    {
        "categoryId is empty", "prayerId is empty", "s01.IsError = true",
        "s03.IsError = true", "userIdB is empty", "reqId is empty",
    };

    /// <summary>
    /// Generates a list of N unique (scenario, stepName, reason) tuples where N ∈ [1..50].
    /// Uniqueness is enforced by appending an index so every tuple is distinct.
    /// </summary>
    private static Gen<List<(string Scenario, string StepName, string Reason)>> UniqueTuplesGen()
    {
        return Gen.Choose(1, 50).SelectMany(n =>
        {
            // Pick random base values then make each tuple unique by appending its index.
            var tupleGens = Enumerable.Range(0, n).Select(i =>
                from scenario in Gen.Elements(ScenarioPool)
                from step     in Gen.Elements(StepPool)
                from reason   in Gen.Elements(ReasonPool)
                select ($"{scenario}_{i}", $"{step}_{i}", $"{reason}_{i}")
            ).ToList();

            // Fold the list of generators into a single generator of a list
            return tupleGens.Aggregate(
                Gen.Constant(new List<(string, string, string)>()),
                (accGen, tupleGen) =>
                    from acc   in accGen
                    from tuple in tupleGen
                    select new List<(string, string, string)>(acc) { tuple });
        });
    }

    // ---------------------------------------------------------------------------
    // Property 14: SkipTracker is thread-safe and preserves all records
    // Validates: Requirement 8.3
    // ---------------------------------------------------------------------------

    /// <summary>
    /// **Validates: Requirements 8.3**
    ///
    /// Property 14: For any list of N unique (scenario, stepName, reason) tuples,
    /// when N concurrent tasks each call <see cref="SkipTracker.Record"/> for one tuple,
    /// <see cref="SkipTracker.Snapshot"/> returns a list containing exactly all inserted
    /// records — no losses, no duplicates.
    /// </summary>
    [Property(MaxTest = 100)]
    public Property Record_Property14_ThreadSafePreservesAllRecords()
    {
        // Feature: loadtest-reporting-improvements, Property 14: SkipTracker thread-safety

        return Prop.ForAll(
            UniqueTuplesGen().ToArbitrary(),
            tuples =>
            {
                var tracker = new SkipTracker();

                // Launch one Task per tuple — all started before any is awaited
                var tasks = tuples
                    .Select(t => Task.Run(() => tracker.Record(t.Item1, t.Item2, t.Item3)))
                    .ToArray();

                Task.WaitAll(tasks);

                var snapshot = tracker.Snapshot();

                // Build expected set and actual set for comparison
                var expected = tuples
                    .Select(t => (t.Item1, t.Item2, t.Item3))
                    .OrderBy(x => x.Item1, StringComparer.Ordinal)
                    .ThenBy(x => x.Item2, StringComparer.Ordinal)
                    .ThenBy(x => x.Item3, StringComparer.Ordinal)
                    .ToList();

                var actual = snapshot
                    .Select(s => (s.Scenario, s.StepName, s.Reason))
                    .OrderBy(x => x.Scenario, StringComparer.Ordinal)
                    .ThenBy(x => x.StepName, StringComparer.Ordinal)
                    .ThenBy(x => x.Reason, StringComparer.Ordinal)
                    .ToList();

                // 1. Count must match (no losses, no duplicates)
                if (actual.Count != expected.Count)
                    return false.Label(
                        $"Snapshot count mismatch: expected {expected.Count}, got {actual.Count}");

                // 2. Each element must match in sorted order
                for (int i = 0; i < expected.Count; i++)
                {
                    if (actual[i] != expected[i])
                        return false.Label(
                            $"Mismatch at index {i}: " +
                            $"expected ({expected[i].Item1}, {expected[i].Item2}, {expected[i].Item3}), " +
                            $"got ({actual[i].Scenario}, {actual[i].StepName}, {actual[i].Reason})");
                }

                return true.Label($"Snapshot preserved all {expected.Count} records correctly");
            });
    }
}
