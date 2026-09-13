using System.Diagnostics;
using EbenezerBackend.LoadTests.Reporting;
using Xunit;

namespace EbenezerBackend.LoadTests.Tests;

/// <summary>
/// Example (Fact) tests for <see cref="ErrorReportWriter"/> I/O behaviour.
/// Each test uses an isolated temp directory and cleans up afterwards.
/// </summary>
public sealed class ErrorReportWriterExampleTests : IDisposable
{
    // -------------------------------------------------------------------------
    // Temp-directory management
    // -------------------------------------------------------------------------

    /// <summary>
    /// Root temp dir shared by ALL tests in this class instance.
    /// Each test carves its own sub-directory from this root.
    /// </summary>
    private readonly string _rootTempDir =
        Path.Combine(Path.GetTempPath(), "ErrorReportWriterTests_" + Guid.NewGuid().ToString("N"));

    /// <summary>Creates a fresh, isolated sub-directory for one test.</summary>
    private string NewTempDir(string label = "")
    {
        string dir = Path.Combine(_rootTempDir, label + "_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_rootTempDir))
                Directory.Delete(_rootTempDir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup; don't fail the test suite on teardown issues.
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>Returns a few representative <see cref="StepFailure"/> items.</summary>
    private static IEnumerable<StepFailure> SampleFailures() =>
    [
        new StepFailure("scenario_a", "01_login",    "Connection refused", "503", 3),
        new StepFailure("scenario_a", "02_register", "Bad request",        "400", 1),
        new StepFailure("scenario_b", "01_login",    "Timeout",            "408", 5),
    ];

    // -------------------------------------------------------------------------
    // Test 1 – Fixed header and file name (Req 2.2, 5.1, 5.2)
    // -------------------------------------------------------------------------

    /// <summary>
    /// TryWrite writes a file whose first line is exactly the fixed CSV header
    /// <c>scenario,endpoint,error_message,count\r\n</c> and whose name is
    /// <see cref="ErrorReportWriter.FileName"/>.
    /// </summary>
    [Fact]
    public void TryWrite_WritesExpectedHeaderAndFilename()
    {
        // Arrange
        string folder = NewTempDir("header");
        var failures = SampleFailures();

        // Act
        bool result = ErrorReportWriter.TryWrite(folder, failures);

        // Assert – returned true (write succeeded)
        Assert.True(result);

        // Assert – file exists with the correct name (Req 5.1, 5.2)
        string expectedPath = Path.Combine(folder, ErrorReportWriter.FileName);
        Assert.True(File.Exists(expectedPath),
            $"Expected file '{ErrorReportWriter.FileName}' inside '{folder}'.");

        // Assert – constant is exactly "errors-by-endpoint.csv" (Req 5.2)
        Assert.Equal("errors-by-endpoint.csv", ErrorReportWriter.FileName);

        // Assert – first line of the written file is the fixed header (Req 2.2, 7.1)
        string content = File.ReadAllText(expectedPath);
        string firstLine = content.Substring(0, content.IndexOf("\r\n", StringComparison.Ordinal) + 2);
        Assert.Equal("scenario,endpoint,error_message,count\r\n", firstLine);
    }

    // -------------------------------------------------------------------------
    // Test 2 – Creates non-existent subfolder (Req 5.3)
    // -------------------------------------------------------------------------

    /// <summary>
    /// TryWrite creates any missing intermediate directories before writing the file.
    /// </summary>
    [Fact]
    public void TryWrite_CreatesNonExistentSubfolder()
    {
        // Arrange – point to a path that does NOT yet exist
        string baseDir = NewTempDir("createdir_base");
        string nestedFolder = Path.Combine(baseDir, "level1", "level2", "run_" + Guid.NewGuid().ToString("N"));

        Assert.False(Directory.Exists(nestedFolder), "Pre-condition: folder must not exist yet.");

        // Act
        bool result = ErrorReportWriter.TryWrite(nestedFolder, SampleFailures());

        // Assert – succeeded
        Assert.True(result);

        // Assert – directory was created (Req 5.3)
        Assert.True(Directory.Exists(nestedFolder),
            "TryWrite must create the destination folder if it does not exist.");

        // Assert – file exists inside the newly-created directory
        string expectedPath = Path.Combine(nestedFolder, ErrorReportWriter.FileName);
        Assert.True(File.Exists(expectedPath),
            $"File '{ErrorReportWriter.FileName}' must exist inside the newly created folder.");
    }

    // -------------------------------------------------------------------------
    // Test 3 – Overwrites pre-existing file (Req 5.4)
    // -------------------------------------------------------------------------

    /// <summary>
    /// When <c>errors-by-endpoint.csv</c> already exists in the destination folder,
    /// TryWrite replaces its content entirely with the new report.
    /// </summary>
    [Fact]
    public void TryWrite_OverwritesExistingFile()
    {
        // Arrange – pre-create the file with distinct "old" content
        string folder = NewTempDir("overwrite");
        string filePath = Path.Combine(folder, ErrorReportWriter.FileName);
        const string oldContent = "old content that must be replaced";
        File.WriteAllText(filePath, oldContent);

        var newFailures = new[]
        {
            new StepFailure("scenario_a", "03_checkout", "Payment failed", "402", 7),
        };

        // Act
        bool result = ErrorReportWriter.TryWrite(folder, newFailures);

        // Assert – write succeeded
        Assert.True(result);

        // Assert – file still exists
        Assert.True(File.Exists(filePath));

        // Assert – old content is gone (Req 5.4)
        string newContent = File.ReadAllText(filePath);
        Assert.NotEqual(oldContent, newContent);
        Assert.DoesNotContain("old content", newContent);

        // Assert – new content starts with the expected header
        Assert.StartsWith("scenario,endpoint,error_message,count\r\n", newContent);
    }

    // -------------------------------------------------------------------------
    // Test 4 – Returns false on I/O failure and preserves existing artefact
    //          (Req 5.5, 6.2)
    // -------------------------------------------------------------------------

    /// <summary>
    /// When writing is impossible (the reportFolder argument is a path to an
    /// existing FILE, so <c>Directory.CreateDirectory</c> will either fail or
    /// <c>File.WriteAllText</c> will fail due to the path being a directory
    /// disguised as a file), TryWrite returns <c>false</c> and leaves any
    /// pre-existing <c>errors-by-endpoint.csv</c> file intact.
    /// </summary>
    [Fact]
    public void TryWrite_ReturnsFalse_OnIoFailure_AndPreservesExistingArtifact()
    {
        // Arrange – create a real temp dir that contains a known artefact
        string baseDir = NewTempDir("iofail");

        // Pre-existing artefact in a sibling directory (simulates another run's output)
        string existingArtifactDir = Path.Combine(baseDir, "existing_run");
        Directory.CreateDirectory(existingArtifactDir);
        string existingFilePath = Path.Combine(existingArtifactDir, ErrorReportWriter.FileName);
        const string preservedContent = "scenario,endpoint,error_message,count\r\nexisting_scenario,existing_endpoint,existing_error,1\r\n";
        File.WriteAllText(existingFilePath, preservedContent);

        // Fabricate an invalid reportFolder: use an existing FILE path as the folder
        // so that Directory.CreateDirectory (or the subsequent write) must fail.
        string existingFileTreatedAsFolder = Path.Combine(baseDir, "i_am_a_file.txt");
        File.WriteAllText(existingFileTreatedAsFolder, "I am a plain file, not a directory.");

        // Act – pass the FILE path as if it were the report folder
        bool result = ErrorReportWriter.TryWrite(existingFileTreatedAsFolder, SampleFailures());

        // Assert – returns false on failure (Req 5.5)
        Assert.False(result,
            "TryWrite must return false when the destination folder cannot be used for writing.");

        // Assert – pre-existing artefact in the sibling directory is untouched (Req 6.2)
        Assert.True(File.Exists(existingFilePath),
            "Pre-existing artefact must not be removed on write failure.");
        string readBack = File.ReadAllText(existingFilePath);
        Assert.Equal(preservedContent, readBack);
    }

    // -------------------------------------------------------------------------
    // Test 5 – Completes within 5 seconds for a typical workload (Req 2.1)
    // -------------------------------------------------------------------------

    /// <summary>
    /// A typical write of 100 <see cref="StepFailure"/> items completes well under 5 s.
    /// </summary>
    [Fact]
    public void TryWrite_CompletesWithinFiveSeconds()
    {
        // Arrange – generate 100 StepFailure items spread across 10 endpoints
        var failures = Enumerable.Range(0, 100)
            .Select(i => new StepFailure(
                ScenarioName: $"scenario_{i % 5:D2}",
                StepName:   $"endpoint_{i % 10:D2}",
                Message:    $"Error message number {i}",
                StatusCode: $"{400 + (i % 10)}",
                Count:      i + 1))
            .ToList();

        string folder = NewTempDir("perf");

        // Act – measure wall-clock time
        var sw = Stopwatch.StartNew();
        bool result = ErrorReportWriter.TryWrite(folder, failures);
        sw.Stop();

        // Assert – write succeeded
        Assert.True(result);

        // Assert – completed in < 5 000 ms (Req 2.1)
        Assert.True(sw.ElapsedMilliseconds < 5_000,
            $"TryWrite took {sw.ElapsedMilliseconds} ms, expected < 5 000 ms.");
    }
}
