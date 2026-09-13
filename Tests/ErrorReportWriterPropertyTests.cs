// Feature: loadtest-csv-error-report, Property 11: Robustez da gravação (nunca lança)
using EbenezerBackend.LoadTests.Reporting;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace EbenezerBackend.LoadTests.Tests;

/// <summary>
/// Property-based tests for <see cref="ErrorReportWriter"/>.
/// Uses FsCheck 3.x C# API (FsCheck.Fluent namespace).
/// </summary>
public sealed class ErrorReportWriterPropertyTests
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
    /// Small pool of step names reused across failures to force key collisions.
    /// </summary>
    private static readonly string[] StepNamePool =
    {
        "01_register", "02_login", "03_get_profile",
    };

    private static readonly string?[] MessagePool =
    {
        null, "", "   ", "timeout", "connection refused", "500 Internal Server Error",
    };

    private static readonly string?[] StatusCodePool =
    {
        null, "", "200", "400", "500", "503",
    };

    private static Gen<StepFailure> StepFailureGen()
    {
        Gen<string>  stepNameGen   = Gen.Elements(StepNamePool);
        Gen<string?> messageGen    = Gen.Elements(MessagePool);
        Gen<string?> statusCodeGen = Gen.Elements(StatusCodePool);
        Gen<long>    countGen      = Gen.Choose(1, 100).Select(n => (long)n);

        return
            from stepName   in stepNameGen
            from message    in messageGen
            from statusCode in statusCodeGen
            from count      in countGen
            select new StepFailure("scenario", stepName, message, statusCode, count);
    }

    /// <summary>Generates a list of 0–20 <see cref="StepFailure"/> instances (may be empty).</summary>
    private static Gen<List<StepFailure>> StepFailureListGen()
    {
        return Gen.Choose(0, 20).SelectMany(size =>
        {
            if (size == 0)
                return Gen.Constant(new List<StepFailure>());

            return Enumerable.Range(0, size)
                .Select(_ => StepFailureGen())
                .Aggregate(
                    Gen.Constant(new List<StepFailure>()),
                    (accGen, failureGen) =>
                        from acc     in accGen
                        from failure in failureGen
                        select new List<StepFailure>(acc) { failure });
        });
    }

    // ---------------------------------------------------------------------------
    // Property 11: Robustez da gravação (nunca lança)
    // Validates: Requirements 5.5, 6.1, 6.4
    // ---------------------------------------------------------------------------

    /// <summary>
    /// **Validates: Requirements 5.5, 6.1, 6.4**
    ///
    /// Property 11: For every combination of destination folder (including invalid or
    /// inaccessible paths) and collection of failures, <see cref="ErrorReportWriter.TryWrite"/>
    /// returns a boolean without propagating any exception, returning control to the caller
    /// in all cases.
    /// </summary>
    [Property(MaxTest = 100)]
    public Property TryWrite_Property11_NeverThrows()
    {
        // Feature: loadtest-csv-error-report, Property 11: Robustez da gravação (nunca lança)

        var combined = from folder   in FolderGen()
                       from failures in StepFailureListGen()
                       select (folder, failures);

        return Prop.ForAll(
            combined.ToArbitrary(),
            pair =>
            {
                var (folder, failures) = pair;

                bool result;
                try
                {
                    // The method must NOT propagate any exception regardless of the folder or
                    // failure collection it receives. The return value is either true or false.
                    result = ErrorReportWriter.TryWrite(folder, failures);
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
