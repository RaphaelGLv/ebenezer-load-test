// Feature: loadtest-csv-error-report, Property 6: Agrupamento por chave composta e cobertura completa
using EbenezerBackend.LoadTests.Reporting;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace EbenezerBackend.LoadTests.Tests;

/// <summary>
/// Property-based tests for <see cref="ErrorReportBuilder"/>.
/// Uses FsCheck 3.x C# API (FsCheck.Fluent namespace).
/// </summary>
public sealed class ErrorReportBuilderPropertyTests
{
    // ---------------------------------------------------------------------------
    // Generators
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Small fixed pool of step names — intentionally short to force key collisions
    /// when generating multiple <see cref="StepFailure"/> instances.
    /// </summary>
    private static readonly string[] StepNamePool =
    {
        "01_register",
        "02_login",
        "03_get_profile",
    };

    /// <summary>
    /// Small fixed pool of nullable error messages.
    /// Includes null and whitespace-only entries to exercise the fallback paths
    /// of <see cref="ErrorReportBuilder.ResolveMessage"/>.
    /// </summary>
    private static readonly string?[] MessagePool =
    {
        null,
        "",
        "   ",
        "timeout",
        "connection refused",
        "500 Internal Server Error",
        "404 Not Found",
    };

    /// <summary>
    /// Small fixed pool of nullable status codes.
    /// Includes null and whitespace-only entries to exercise the fallback paths.
    /// </summary>
    private static readonly string?[] StatusCodePool =
    {
        null,
        "",
        "200",
        "400",
        "500",
        "503",
    };

    /// <summary>
    /// Generator for a single <see cref="StepFailure"/> drawn from the small pools
    /// above, with Count in [1..100].
    /// </summary>
    private static Gen<StepFailure> StepFailureGen()
    {
        Gen<string> stepNameGen = Gen.Elements(StepNamePool);
        Gen<string?> messageGen = Gen.Elements(MessagePool);
        Gen<string?> statusCodeGen = Gen.Elements(StatusCodePool);
        Gen<long> countGen = Gen.Choose(1, 100).Select(n => (long)n);

        return
            from stepName in stepNameGen
            from message in messageGen
            from statusCode in statusCodeGen
            from count in countGen
            select new StepFailure("scenario", stepName, message, statusCode, count);
    }

    /// <summary>
    /// Generator for a non-empty list of <see cref="StepFailure"/> (length in [1..20]).
    /// Uses the small step-name pool so duplicate (endpoint, message) keys appear frequently,
    /// exercising the grouping logic.
    /// </summary>
    private static Gen<List<StepFailure>> StepFailureListGen()
    {
        return Gen.Choose(1, 20).SelectMany(count =>
            Enumerable.Range(0, count)
                .Select(_ => StepFailureGen())
                .Aggregate(
                    Gen.Constant(new List<StepFailure>()),
                    (accGen, failureGen) =>
                        from acc in accGen
                        from failure in failureGen
                        select new List<StepFailure>(acc) { failure }));
    }

    // ---------------------------------------------------------------------------
    // Property 6: Agrupamento por chave composta e cobertura completa
    // Validates: Requirements 2.3, 3.2, 3.3, 6.3
    // ---------------------------------------------------------------------------

    /// <summary>
    /// **Validates: Requirements 2.3, 3.2, 3.3, 6.3**
    ///
    /// Property 6: For every collection of StepFailure, the set of keys (endpoint, error_message)
    /// of the rows produced by ErrorReportBuilder.Build is exactly the distinct set of pairs
    /// (endpoint, error_message) resolved from the input failures — each distinct pair generates
    /// exactly one row, with no omissions or duplicates.
    /// </summary>
    [Property(MaxTest = 100)]
    public Property Build_Property6_GroupingAndFullCoverage()
    {
        // Feature: loadtest-csv-error-report, Property 6: Agrupamento por chave composta e cobertura completa

        return Prop.ForAll(
            StepFailureListGen().ToArbitrary(),
            failures =>
            {
                // 1. Compute expected distinct (endpoint, error_message) keys from the input,
                //    using the same resolver methods that Build uses internally.
                var expectedKeys = failures
                    .Select(f => (
                        endpoint: ErrorReportBuilder.ResolveEndpoint(f.StepName, knownEndpoints: null),
                        message: ResolveMessageWithUnknownStepHandling(f)))
                    .Distinct()
                    .ToHashSet();

                // 2. Call Build with knownEndpoints = null
                var outputRows = ErrorReportBuilder.Build(failures, knownEndpoints: null);

                // --- Check A: output row count equals the number of distinct expected keys ---
                if (outputRows.Count != expectedKeys.Count)
                    return false.Label(
                        $"Row count mismatch: expected {expectedKeys.Count} distinct keys, " +
                        $"got {outputRows.Count} rows");

                // --- Check B: every expected key appears exactly once in the output ---
                foreach (var (endpoint, message) in expectedKeys)
                {
                    var matchCount = outputRows.Count(r =>
                        r.Endpoint == endpoint && r.ErrorMessage == message);

                    if (matchCount != 1)
                        return false.Label(
                            $"Expected key ({endpoint}, {message}) appears {matchCount} times " +
                            $"in output (expected exactly 1)");
                }

                // --- Check C: every output key was expected (no spurious keys) ---
                foreach (var row in outputRows)
                {
                    var key = (endpoint: row.Endpoint, message: row.ErrorMessage);
                    if (!expectedKeys.Contains(key))
                        return false.Label(
                            $"Output row ({row.Endpoint}, {row.ErrorMessage}) was not expected");
                }

                return true.Label("Property 6 holds");
            });
    }

    // ---------------------------------------------------------------------------
    // Property 7: Ordenação determinística
    // Validates: Requirements 2.4
    // ---------------------------------------------------------------------------

    /// <summary>
    /// **Validates: Requirements 2.4**
    ///
    /// Property 7: For every collection of StepFailure, the rows produced by
    /// ErrorReportBuilder.Build are sorted in ascending order first by endpoint and,
    /// in tie-break, by error_message; and any permutation of the input collection
    /// produces the exact same output sequence.
    /// </summary>
    [Property(MaxTest = 100)]
    public Property Build_Property7_DeterministicOrdering()
    {
        // Feature: loadtest-csv-error-report, Property 7: Ordenação determinística

        return Prop.ForAll(
            StepFailureListGen().ToArbitrary(),
            failures =>
            {
                // 1. Call Build on the original list
                var originalRows = ErrorReportBuilder.Build(failures, knownEndpoints: null);

                // 2. Shuffle the input to produce a permutation
                var shuffled = new List<StepFailure>(failures);
                ShuffleList(shuffled);

                // 3. Call Build on the permuted list
                var permutedRows = ErrorReportBuilder.Build(shuffled, knownEndpoints: null);

                // --- Check A: both calls return the same number of rows ---
                if (originalRows.Count != permutedRows.Count)
                    return false.Label(
                        $"Row count mismatch: original={originalRows.Count}, permuted={permutedRows.Count}");

                // --- Check B: both calls return the exact same sequence (same order) ---
                for (int i = 0; i < originalRows.Count; i++)
                {
                    var orig = originalRows[i];
                    var perm = permutedRows[i];
                    if (orig.Endpoint != perm.Endpoint ||
                        orig.ErrorMessage != perm.ErrorMessage ||
                        orig.Count != perm.Count)
                        return false.Label(
                            $"Row {i} differs: original=({orig.Endpoint}, {orig.ErrorMessage}, {orig.Count}), " +
                            $"permuted=({perm.Endpoint}, {perm.ErrorMessage}, {perm.Count})");
                }

                // --- Check C: rows are sorted by endpoint (ordinal asc), then error_message (ordinal asc) ---
                for (int i = 0; i < originalRows.Count - 1; i++)
                {
                    var current = originalRows[i];
                    var next    = originalRows[i + 1];

                    int endpointCmp = StringComparer.Ordinal.Compare(current.Endpoint, next.Endpoint);
                    if (endpointCmp > 0)
                        return false.Label(
                            $"Rows not sorted by endpoint at index {i}: " +
                            $"'{current.Endpoint}' > '{next.Endpoint}'");

                    if (endpointCmp == 0)
                    {
                        int messageCmp = StringComparer.Ordinal.Compare(current.ErrorMessage, next.ErrorMessage);
                        if (messageCmp > 0)
                            return false.Label(
                                $"Rows with same endpoint '{current.Endpoint}' not sorted by error_message at index {i}: " +
                                $"'{current.ErrorMessage}' > '{next.ErrorMessage}'");
                    }
                }

                return true.Label("Property 7 holds");
            });
    }

    // ---------------------------------------------------------------------------
    // Property 8: Correção da contagem
    // Validates: Requirements 2.5
    // ---------------------------------------------------------------------------

    /// <summary>
    /// **Validates: Requirements 2.5**
    ///
    /// Property 8: For every collection of failures, the count of each output row equals
    /// the sum of Count values of all input failures that resolve to that same
    /// (endpoint, error_message) pair; every count is >= 1; and the total sum of all
    /// output counts equals the total sum of all input Count values.
    /// </summary>
    [Property(MaxTest = 100)]
    public Property Build_Property8_CountCorrectness()
    {
        // Feature: loadtest-csv-error-report, Property 8: Correção da contagem

        return Prop.ForAll(
            StepFailureListGen().ToArbitrary(),
            failures =>
            {
                // 1. Call Build with knownEndpoints = null
                var outputRows = ErrorReportBuilder.Build(failures, knownEndpoints: null);

                // --- Check A: total sum of output counts equals total sum of input counts ---
                var totalInputCount  = failures.Sum(f => f.Count);
                var totalOutputCount = outputRows.Sum(r => r.Count);

                if (totalOutputCount != totalInputCount)
                    return false.Label(
                        $"Total count mismatch: input sum={totalInputCount}, output sum={totalOutputCount}");

                // --- Check B: every output count >= 1 ---
                foreach (var row in outputRows)
                {
                    if (row.Count < 1)
                        return false.Label(
                            $"Output row ({row.Endpoint}, {row.ErrorMessage}) has count={row.Count}, expected >= 1");
                }

                // --- Check C: per-key count accuracy ---
                // For each output row, compute the expected sum from matching input failures
                foreach (var row in outputRows)
                {
                    var expectedSum = failures
                        .Where(f =>
                            ErrorReportBuilder.ResolveEndpoint(f.StepName, knownEndpoints: null) == row.Endpoint &&
                            ResolveMessageWithUnknownStepHandling(f) == row.ErrorMessage)
                        .Sum(f => f.Count);

                    if (row.Count != expectedSum)
                        return false.Label(
                            $"Count mismatch for key ({row.Endpoint}, {row.ErrorMessage}): " +
                            $"expected sum={expectedSum}, got row.Count={row.Count}");
                }

                return true.Label("Property 8 holds");
            });
    }

    // ---------------------------------------------------------------------------
    // Generator helpers for Properties 12 and 13
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Small fixed pool of distinct scenario names used to force scenario-level
    /// grouping in Properties 12 and 13.
    /// </summary>
    private static readonly string[] ScenarioPool =
    {
        "write_heavy",
        "read_heavy",
        "graph_heavy",
        "document_heavy",
        "mixed_graph_documents",
    };

    /// <summary>
    /// Generator for a single <see cref="StepFailure"/> where the scenario name is
    /// drawn from <see cref="ScenarioPool"/> rather than the fixed <c>"scenario"</c>
    /// string used by <see cref="StepFailureGen"/>.
    /// </summary>
    private static Gen<StepFailure> StepFailureWithScenarioGen()
    {
        Gen<string>  scenarioGen   = Gen.Elements(ScenarioPool);
        Gen<string>  stepNameGen   = Gen.Elements(StepNamePool);
        Gen<string?> messageGen    = Gen.Elements(MessagePool);
        Gen<string?> statusCodeGen = Gen.Elements(StatusCodePool);
        Gen<long>    countGen      = Gen.Choose(1, 100).Select(n => (long)n);

        return
            from scenario   in scenarioGen
            from stepName   in stepNameGen
            from message    in messageGen
            from statusCode in statusCodeGen
            from count      in countGen
            select new StepFailure(scenario, stepName, message, statusCode, count);
    }

    /// <summary>
    /// Generator for a non-empty list of <see cref="StepFailure"/> (length in [1..20])
    /// where scenario names vary across the entries, making scenario-key collisions
    /// frequent while keeping the key space finite.
    /// </summary>
    private static Gen<List<StepFailure>> StepFailureWithScenarioListGen()
    {
        return Gen.Choose(1, 20).SelectMany(count =>
            Enumerable.Range(0, count)
                .Select(_ => StepFailureWithScenarioGen())
                .Aggregate(
                    Gen.Constant(new List<StepFailure>()),
                    (accGen, failureGen) =>
                        from acc     in accGen
                        from failure in failureGen
                        select new List<StepFailure>(acc) { failure }));
    }

    // ---------------------------------------------------------------------------
    // Property 12: `scenario` as grouping key
    // Validates: Requirement 7.3
    // ---------------------------------------------------------------------------

    /// <summary>
    /// **Validates: Requirements 7.3**
    ///
    /// Property 12: For every collection of StepFailure where the same (endpoint,
    /// error_message) pair appears under at least two distinct scenario names,
    /// ErrorReportBuilder.Build produces separate rows for each distinct scenario —
    /// i.e., the number of distinct (scenario, endpoint, error_message) triplets in
    /// the output equals the number of distinct such triplets in the input.
    /// </summary>
    [Property(MaxTest = 200)]
    public Property Build_Property12_ScenarioAsGroupingKey()
    {
        // Feature: loadtest-reporting-improvements, Property 12: scenario as grouping key

        return Prop.ForAll(
            StepFailureWithScenarioListGen().ToArbitrary(),
            failures =>
            {
                // 1. Compute the expected distinct (scenario, endpoint, message) triplets
                //    using the same resolver methods that Build uses internally.
                var expectedKeys = failures
                    .Select(f => (
                        scenario: ErrorReportBuilder.ResolveScenario(f.ScenarioName),
                        endpoint: ErrorReportBuilder.ResolveEndpoint(f.StepName, knownEndpoints: null),
                        message:  ResolveMessageWithUnknownStepHandling(f)))
                    .Distinct()
                    .ToHashSet();

                // 2. Call Build
                var outputRows = ErrorReportBuilder.Build(failures, knownEndpoints: null);

                // --- Check A: one row per distinct (scenario, endpoint, message) triplet ---
                if (outputRows.Count != expectedKeys.Count)
                    return false.Label(
                        $"Row count mismatch: expected {expectedKeys.Count} distinct triplets, " +
                        $"got {outputRows.Count} rows");

                // --- Check B: every expected triplet appears exactly once ---
                foreach (var (scenario, endpoint, message) in expectedKeys)
                {
                    var matchCount = outputRows.Count(r =>
                        r.Scenario    == scenario &&
                        r.Endpoint    == endpoint &&
                        r.ErrorMessage == message);

                    if (matchCount != 1)
                        return false.Label(
                            $"Expected triplet ({scenario}, {endpoint}, {message}) appears " +
                            $"{matchCount} times in output (expected exactly 1)");
                }

                // --- Check C: scenarios with the same (endpoint, message) are NOT merged ---
                // Group output rows by (endpoint, message) and verify that when multiple
                // distinct scenarios share the same pair they each get their own row.
                var byEndpointMessage = outputRows
                    .GroupBy(r => (r.Endpoint, r.ErrorMessage))
                    .ToList();

                foreach (var group in byEndpointMessage)
                {
                    var distinctScenariosInOutput = group.Select(r => r.Scenario).Distinct().Count();
                    var distinctScenariosInInput  = failures
                        .Where(f =>
                            ErrorReportBuilder.ResolveEndpoint(f.StepName, null) == group.Key.Endpoint &&
                            ResolveMessageWithUnknownStepHandling(f)             == group.Key.ErrorMessage)
                        .Select(f => ErrorReportBuilder.ResolveScenario(f.ScenarioName))
                        .Distinct()
                        .Count();

                    if (distinctScenariosInOutput != distinctScenariosInInput)
                        return false.Label(
                            $"For (endpoint={group.Key.Endpoint}, message={group.Key.ErrorMessage}): " +
                            $"input has {distinctScenariosInInput} distinct scenarios but output has " +
                            $"{distinctScenariosInOutput} — scenarios must not be merged");
                }

                return true.Label("Property 12 holds");
            });
    }

    // ---------------------------------------------------------------------------
    // Property 13: 3-key deterministic ordering
    // Validates: Requirement 7.4
    // ---------------------------------------------------------------------------

    /// <summary>
    /// **Validates: Requirements 7.4**
    ///
    /// Property 13: For every collection of failures, the rows produced by
    /// ErrorReportBuilder.Build are sorted by scenario (ordinal asc) → endpoint
    /// (ordinal asc) → error_message (ordinal asc); and any permutation of the input
    /// produces the exact same output sequence.
    /// </summary>
    [Property(MaxTest = 200)]
    public Property Build_Property13_ThreeKeyDeterministicOrdering()
    {
        // Feature: loadtest-reporting-improvements, Property 13: 3-key deterministic ordering

        return Prop.ForAll(
            StepFailureWithScenarioListGen().ToArbitrary(),
            failures =>
            {
                // 1. Call Build on the original list
                var originalRows = ErrorReportBuilder.Build(failures, knownEndpoints: null);

                // 2. Shuffle the input to produce a permutation
                var shuffled = new List<StepFailure>(failures);
                ShuffleList(shuffled);

                // 3. Call Build on the permuted list
                var permutedRows = ErrorReportBuilder.Build(shuffled, knownEndpoints: null);

                // --- Check A: both calls return the same number of rows ---
                if (originalRows.Count != permutedRows.Count)
                    return false.Label(
                        $"Row count mismatch: original={originalRows.Count}, permuted={permutedRows.Count}");

                // --- Check B: both calls return the exact same sequence ---
                for (int i = 0; i < originalRows.Count; i++)
                {
                    var orig = originalRows[i];
                    var perm = permutedRows[i];
                    if (orig.Scenario    != perm.Scenario    ||
                        orig.Endpoint    != perm.Endpoint    ||
                        orig.ErrorMessage != perm.ErrorMessage ||
                        orig.Count       != perm.Count)
                        return false.Label(
                            $"Row {i} differs between original and permuted: " +
                            $"original=({orig.Scenario}, {orig.Endpoint}, {orig.ErrorMessage}, {orig.Count}), " +
                            $"permuted=({perm.Scenario}, {perm.Endpoint}, {perm.ErrorMessage}, {perm.Count})");
                }

                // --- Check C: rows are sorted by scenario → endpoint → error_message (ordinal asc) ---
                for (int i = 0; i < originalRows.Count - 1; i++)
                {
                    var current = originalRows[i];
                    var next    = originalRows[i + 1];

                    int scenarioCmp = StringComparer.Ordinal.Compare(current.Scenario, next.Scenario);
                    if (scenarioCmp > 0)
                        return false.Label(
                            $"Rows not sorted by scenario at index {i}: " +
                            $"'{current.Scenario}' > '{next.Scenario}'");

                    if (scenarioCmp == 0)
                    {
                        int endpointCmp = StringComparer.Ordinal.Compare(current.Endpoint, next.Endpoint);
                        if (endpointCmp > 0)
                            return false.Label(
                                $"Rows with same scenario '{current.Scenario}' not sorted by endpoint at index {i}: " +
                                $"'{current.Endpoint}' > '{next.Endpoint}'");

                        if (endpointCmp == 0)
                        {
                            int messageCmp = StringComparer.Ordinal.Compare(current.ErrorMessage, next.ErrorMessage);
                            if (messageCmp > 0)
                                return false.Label(
                                    $"Rows with same (scenario='{current.Scenario}', endpoint='{current.Endpoint}') " +
                                    $"not sorted by error_message at index {i}: " +
                                    $"'{current.ErrorMessage}' > '{next.ErrorMessage}'");
                        }
                    }
                }

                return true.Label("Property 13 holds");
            });
    }

    /// <summary>
    /// Shuffles a list in-place using a simple Fisher-Yates algorithm with a fixed seed
    /// so that permutations are deterministic across test runs (FsCheck controls randomness
    /// via the list generator; we only need <em>some</em> permutation that differs from
    /// the original when the list has more than one element).
    /// </summary>
    private static void ShuffleList<T>(List<T> list)
    {
        // Use a fixed seed so the shuffle is deterministic given the same list.
        // FsCheck already controls the random selection of the list contents.
        var rng = new Random(42);
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Replicates the full message-resolution logic that <see cref="ErrorReportBuilder.Build"/>
    /// applies internally, including the "step: &lt;stepName&gt;" suffix appended when the
    /// endpoint resolves to "unknown" (Req 3.6).
    /// </summary>
    private static string ResolveMessageWithUnknownStepHandling(StepFailure failure)
    {
        var endpoint = ErrorReportBuilder.ResolveEndpoint(failure.StepName, knownEndpoints: null);
        var message  = ErrorReportBuilder.ResolveMessage(failure.Message, failure.StatusCode);

        // Mirror the Req 3.6 suffix logic from ErrorReportBuilder.Build:
        // when the step name is not a known endpoint (i.e. endpoint == "unknown"),
        // the original step name is appended to the message.
        // However, with knownEndpoints = null, ResolveEndpoint always returns stepName
        // (never "unknown"), so this branch is here for completeness and correctness
        // if the behaviour ever changes.
        if (endpoint == ErrorReportBuilder.UnknownEndpoint)
            message = $"{message} (step: {failure.StepName})";

        return message;
    }
}
