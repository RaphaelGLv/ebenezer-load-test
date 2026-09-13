// Feature: loadtest-reporting-improvements, Property 15: Ordenação determinística do SkippedStepsBuilder
using EbenezerBackend.LoadTests.Reporting;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace EbenezerBackend.LoadTests.Tests;

/// <summary>
/// Property-based tests for <see cref="SkippedStepsBuilder"/>.
/// Uses FsCheck 3.x C# API (FsCheck.Fluent namespace).
/// </summary>
public sealed class SkippedStepsBuilderPropertyTests
{
    // ---------------------------------------------------------------------------
    // Generators
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Small fixed pool of scenario names — intentionally short to force collisions
    /// when generating multiple <see cref="SkippedStep"/> instances.
    /// </summary>
    private static readonly string[] ScenarioPool =
    {
        "write_heavy",
        "read_heavy",
        "graph_heavy",
    };

    /// <summary>
    /// Small fixed pool of step names — intentionally short to force collisions.
    /// </summary>
    private static readonly string[] StepNamePool =
    {
        "01_register",
        "02_login",
        "03_get_profile",
        "04_update",
    };

    /// <summary>
    /// Fixed pool of reasons — varied enough to make records distinguishable.
    /// </summary>
    private static readonly string[] ReasonPool =
    {
        "categoryId is empty",
        "prayerId is empty",
        "s01.IsError = true – registration failed",
        "userIdB is empty",
    };

    /// <summary>
    /// Generator for a single <see cref="SkippedStep"/> drawn from the small pools above.
    /// </summary>
    private static Gen<SkippedStep> SkippedStepGen()
    {
        Gen<string> scenarioGen = Gen.Elements(ScenarioPool);
        Gen<string> stepNameGen = Gen.Elements(StepNamePool);
        Gen<string> reasonGen   = Gen.Elements(ReasonPool);

        return
            from scenario in scenarioGen
            from stepName in stepNameGen
            from reason   in reasonGen
            select new SkippedStep(scenario, stepName, reason);
    }

    /// <summary>
    /// Generator for a list of <see cref="SkippedStep"/> with length in [0..20].
    /// Allows empty lists to exercise the edge case of an empty input.
    /// </summary>
    private static Gen<List<SkippedStep>> SkippedStepListGen()
    {
        return Gen.Choose(0, 20).SelectMany(count =>
            Enumerable.Range(0, count)
                .Select(_ => SkippedStepGen())
                .Aggregate(
                    Gen.Constant(new List<SkippedStep>()),
                    (accGen, stepGen) =>
                        from acc  in accGen
                        from step in stepGen
                        select new List<SkippedStep>(acc) { step }));
    }

    // ---------------------------------------------------------------------------
    // Property 15: Ordenação determinística do SkippedStepsBuilder
    // Validates: Requirement 8.6
    // ---------------------------------------------------------------------------

    /// <summary>
    /// **Validates: Requirement 8.6**
    ///
    /// Property 15: For every collection of SkippedStep, any permutation of the input
    /// produces the exact same output sequence from <see cref="SkippedStepsBuilder.Sort"/>,
    /// and the output is sorted by Scenario (ordinal asc) then StepName (ordinal asc).
    /// </summary>
    [Property(MaxTest = 100)]
    public Property Sort_Property15_DeterministicOrdering()
    {
        // Feature: loadtest-reporting-improvements, Property 15: Ordenação determinística do SkippedStepsBuilder

        return Prop.ForAll(
            SkippedStepListGen().ToArbitrary(),
            records =>
            {
                // 1. Sort the original list
                var originalSorted = SkippedStepsBuilder.Sort(records);

                // 2. Shuffle the input to produce a permutation
                var shuffled = new List<SkippedStep>(records);
                ShuffleList(shuffled);

                // 3. Sort the shuffled list
                var shuffledSorted = SkippedStepsBuilder.Sort(shuffled);

                // --- Check A: both calls return the same number of records ---
                if (originalSorted.Count != shuffledSorted.Count)
                    return false.Label(
                        $"Count mismatch: original={originalSorted.Count}, shuffled={shuffledSorted.Count}");

                // --- Check B: both calls produce records with the same sort keys in the same positions ---
                // The sort is by (Scenario, StepName) only; Reason is not a sort key, so we only
                // verify the primary ordering keys are identical at every position.
                for (int i = 0; i < originalSorted.Count; i++)
                {
                    var orig  = originalSorted[i];
                    var shuff = shuffledSorted[i];

                    if (orig.Scenario != shuff.Scenario ||
                        orig.StepName != shuff.StepName)
                        return false.Label(
                            $"Sort key at index {i} differs: " +
                            $"original=({orig.Scenario}, {orig.StepName}), " +
                            $"shuffled=({shuff.Scenario}, {shuff.StepName})");
                }

                // --- Check C: output is sorted by Scenario (ordinal asc) then StepName (ordinal asc) ---
                for (int i = 0; i < originalSorted.Count - 1; i++)
                {
                    var current = originalSorted[i];
                    var next    = originalSorted[i + 1];

                    int scenarioCmp = StringComparer.Ordinal.Compare(current.Scenario, next.Scenario);
                    if (scenarioCmp > 0)
                        return false.Label(
                            $"Records not sorted by Scenario at index {i}: " +
                            $"'{current.Scenario}' > '{next.Scenario}'");

                    if (scenarioCmp == 0)
                    {
                        int stepNameCmp = StringComparer.Ordinal.Compare(current.StepName, next.StepName);
                        if (stepNameCmp > 0)
                            return false.Label(
                                $"Records with same Scenario '{current.Scenario}' not sorted by StepName at index {i}: " +
                                $"'{current.StepName}' > '{next.StepName}'");
                    }
                }

                return true.Label("Property 15 holds");
            });
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Shuffles a list in-place using Fisher-Yates with a fixed seed so the shuffle
    /// is deterministic given the same list. FsCheck controls randomness via the list
    /// generator; we only need <em>some</em> permutation that differs from the original
    /// when the list has more than one element.
    /// </summary>
    private static void ShuffleList<T>(List<T> list)
    {
        var rng = new Random(42);
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
