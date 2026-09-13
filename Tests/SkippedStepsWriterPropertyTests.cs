// Feature: loadtest-reporting-improvements, Property 16: Robustez da gravação (nunca lança)
using EbenezerBackend.LoadTests.Reporting;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace EbenezerBackend.LoadTests.Tests;

/// <summary>
/// Property-based tests for <see cref="SkippedStepsWriter"/>.
/// Uses FsCheck 3.x C# API (FsCheck.Fluent namespace).
/// </summary>
public sealed class SkippedStepsWriterPropertyTests
{
    // ---------------------------------------------------------------------------
    // Generators
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Generates a mix of valid and invalid destination folder paths:
    /// <list type="bullet">
    ///   <item>Valid writable path: a new Guid subdirectory under <see cref="Path.GetTempPath"/></item>
    ///   <item>Empty string (invalid)</item>
    ///   <item>Path with illegal characters on Windows (e.g., <c>&lt;|&gt;</c>)</item>
    ///   <item>Null (cast to <c>string!</c> to exercise the null path through the method)</item>
    ///   <item>Path pointing to an existing FILE (not a directory)</item>
    /// </list>
    /// The last case pre-creates a file at the generated path so that
    /// <c>Directory.CreateDirectory</c> will fail or produce unexpected behaviour.
    /// </summary>
    private static Gen<string> FolderGen()
    {
        // 1. Valid writable temp path (unique per call so tests don't interfere)
        Gen<string> validTempGen = Gen.Fresh(() =>
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));

        // 2. Empty string
        Gen<string> emptyGen = Gen.Constant(string.Empty);

        // 3. Windows path with illegal characters
        Gen<string> illegalCharsGen = Gen.Constant("Z:\\invalid\\path\\<|>");

        // 4. Null passed as string! (exercises the null-input path)
        Gen<string> nullGen = Gen.Constant<string>(null!);

        // 5. Path that already exists as a FILE (not a directory)
        Gen<string> fileAsPathGen = Gen.Fresh(() =>
        {
            string filePath = Path.Combine(Path.GetTempPath(), $"pbt_file_{Guid.NewGuid()}.tmp");
            // Pre-create the file so the path exists but is NOT a directory
            File.WriteAllText(filePath, "pre-existing file content");
            return filePath;
        });

        // Weighted frequency: higher weight on valid paths to exercise actual write path
        return Gen.Frequency(
            (3, validTempGen),
            (1, emptyGen),
            (1, illegalCharsGen),
            (1, nullGen),
            (1, fileAsPathGen));
    }

    /// <summary>
    /// Small pool of scenario names reused across records.
    /// </summary>
    private static readonly string[] ScenarioPool =
    {
        "write_heavy", "read_heavy", "graph_heavy", "document_heavy", "mixed_graph_documents",
    };

    private static readonly string[] StepNamePool =
    {
        "01_register", "02_login", "03_create", "04_update", "05_delete",
    };

    private static readonly string[] ReasonPool =
    {
        "categoryId is empty", "prayerId is empty", "s01.IsError = true – registration failed",
        "userIdB is empty", "reqId is empty",
    };

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

    /// <summary>Generates a list of 0–20 <see cref="SkippedStep"/> instances (may be empty).</summary>
    private static Gen<List<SkippedStep>> SkippedStepListGen()
    {
        return Gen.Choose(0, 20).SelectMany(size =>
        {
            if (size == 0)
                return Gen.Constant(new List<SkippedStep>());

            return Enumerable.Range(0, size)
                .Select(_ => SkippedStepGen())
                .Aggregate(
                    Gen.Constant(new List<SkippedStep>()),
                    (accGen, stepGen) =>
                        from acc  in accGen
                        from step in stepGen
                        select new List<SkippedStep>(acc) { step });
        });
    }

    // ---------------------------------------------------------------------------
    // Property 16: Robustez da gravação (nunca lança)
    // Validates: Requirement 8.7
    // ---------------------------------------------------------------------------

    /// <summary>
    /// **Validates: Requirements 8.7**
    ///
    /// Property 16: For every combination of destination folder (including invalid or
    /// inaccessible paths) and collection of skipped steps, <see cref="SkippedStepsWriter.TryWrite"/>
    /// returns a boolean without propagating any exception, returning control to the caller
    /// in all cases.
    /// </summary>
    [Property(MaxTest = 100)]
    public Property TryWrite_Property16_NeverThrows()
    {
        // Feature: loadtest-reporting-improvements, Property 16: Robustez da gravação (nunca lança)

        var combined = from folder  in FolderGen()
                       from records in SkippedStepListGen()
                       select (folder, records);

        return Prop.ForAll(
            combined.ToArbitrary(),
            pair =>
            {
                var (folder, records) = pair;

                bool result;
                try
                {
                    // The method must NOT propagate any exception regardless of the folder or
                    // records collection it receives. The return value is either true or false.
                    result = SkippedStepsWriter.TryWrite(folder, records);
                }
                catch (Exception ex)
                {
                    return false.Label(
                        $"TryWrite propagated an exception (must never throw): " +
                        $"{ex.GetType().Name}: {ex.Message}. " +
                        $"Folder = '{folder ?? "<null>"}'");
                }

                // result must be a valid bool — in C# this is always satisfied, but we make the
                // assertion explicit so the property clearly documents the expected return type.
                bool resultIsBool = result == true || result == false;
                return resultIsBool.Label($"result '{result}' is a valid bool");
            });
    }
}
