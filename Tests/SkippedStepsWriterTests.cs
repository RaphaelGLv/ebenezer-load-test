using EbenezerBackend.LoadTests.Reporting;
using Xunit;

namespace EbenezerBackend.LoadTests.Tests;

/// <summary>
/// Example (Fact) tests for <see cref="SkippedStepsWriter"/> I/O behaviour.
/// Each test uses an isolated temp directory and cleans up afterwards.
/// </summary>
public sealed class SkippedStepsWriterTests : IDisposable
{
    // -------------------------------------------------------------------------
    // Temp-directory management
    // -------------------------------------------------------------------------

    /// <summary>
    /// Root temp dir shared by ALL tests in this class instance.
    /// Each test carves its own sub-directory from this root.
    /// </summary>
    private readonly string _rootTempDir =
        Path.Combine(Path.GetTempPath(), "SkippedStepsWriterTests_" + Guid.NewGuid().ToString("N"));

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

    /// <summary>Returns a few representative <see cref="SkippedStep"/> items.</summary>
    private static IEnumerable<SkippedStep> SampleRecords() =>
    [
        new SkippedStep("write_heavy",  "04_update_category", "categoryId is empty"),
        new SkippedStep("read_heavy",   "04_get_prayer_by_id", "prayerId is empty"),
        new SkippedStep("graph_heavy",  "06_accept_request",   "s04.IsError = true – friend request failed"),
    ];

    // -------------------------------------------------------------------------
    // Test 1 – Fixed header and file name (Req 8.1, 8.2)
    // -------------------------------------------------------------------------

    /// <summary>
    /// TryWrite writes a file whose first line is exactly the fixed CSV header
    /// <c>scenario,step_name,reason\r\n</c> and whose name is
    /// <see cref="SkippedStepsWriter.FileName"/>.
    /// </summary>
    [Fact]
    public void TryWrite_WritesExpectedHeaderAndFilename()
    {
        // Arrange
        string folder = NewTempDir("header");
        var records = SampleRecords();

        // Act
        bool result = SkippedStepsWriter.TryWrite(folder, records);

        // Assert – returned true (write succeeded)
        Assert.True(result);

        // Assert – file exists with the correct name (Req 8.1)
        string expectedPath = Path.Combine(folder, SkippedStepsWriter.FileName);
        Assert.True(File.Exists(expectedPath),
            $"Expected file '{SkippedStepsWriter.FileName}' inside '{folder}'.");

        // Assert – constant is exactly "skipped-steps.csv" (Req 8.1)
        Assert.Equal("skipped-steps.csv", SkippedStepsWriter.FileName);

        // Assert – first line of the written file is the fixed header
        string content = File.ReadAllText(expectedPath);
        string firstLine = content.Substring(0, content.IndexOf("\r\n", StringComparison.Ordinal) + 2);
        Assert.Equal("scenario,step_name,reason\r\n", firstLine);
    }

    // -------------------------------------------------------------------------
    // Test 2 – Creates non-existent subfolder
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
        bool result = SkippedStepsWriter.TryWrite(nestedFolder, SampleRecords());

        // Assert – succeeded
        Assert.True(result);

        // Assert – directory was created
        Assert.True(Directory.Exists(nestedFolder),
            "TryWrite must create the destination folder if it does not exist.");

        // Assert – file exists inside the newly-created directory
        string expectedPath = Path.Combine(nestedFolder, SkippedStepsWriter.FileName);
        Assert.True(File.Exists(expectedPath),
            $"File '{SkippedStepsWriter.FileName}' must exist inside the newly created folder.");
    }

    // -------------------------------------------------------------------------
    // Test 3 – Overwrites pre-existing file
    // -------------------------------------------------------------------------

    /// <summary>
    /// When <c>skipped-steps.csv</c> already exists in the destination folder,
    /// TryWrite replaces its content entirely with the new report.
    /// </summary>
    [Fact]
    public void TryWrite_OverwritesExistingFile()
    {
        // Arrange – pre-create the file with distinct "old" content
        string folder = NewTempDir("overwrite");
        string filePath = Path.Combine(folder, SkippedStepsWriter.FileName);
        const string oldContent = "old content that must be replaced";
        File.WriteAllText(filePath, oldContent);

        var newRecords = new[]
        {
            new SkippedStep("document_heavy", "05_update_prayer", "prayerId is empty"),
        };

        // Act
        bool result = SkippedStepsWriter.TryWrite(folder, newRecords);

        // Assert – write succeeded
        Assert.True(result);

        // Assert – file still exists
        Assert.True(File.Exists(filePath));

        // Assert – old content is gone
        string newContent = File.ReadAllText(filePath);
        Assert.NotEqual(oldContent, newContent);
        Assert.DoesNotContain("old content", newContent);

        // Assert – new content starts with the expected header
        Assert.StartsWith("scenario,step_name,reason\r\n", newContent);
    }

    // -------------------------------------------------------------------------
    // Test 4 – Empty collection writes only the header (Req 8.5)
    // -------------------------------------------------------------------------

    /// <summary>
    /// When records is empty, TryWrite writes a CSV with only the header line
    /// (Req 8.5).
    /// </summary>
    [Fact]
    public void TryWrite_EmptyCollection_WritesOnlyHeader()
    {
        // Arrange
        string folder = NewTempDir("empty");

        // Act
        bool result = SkippedStepsWriter.TryWrite(folder, []);

        // Assert – write succeeded
        Assert.True(result);

        // Assert – file exists
        string filePath = Path.Combine(folder, SkippedStepsWriter.FileName);
        Assert.True(File.Exists(filePath));

        // Assert – content is exactly the header line (no data rows)
        string content = File.ReadAllText(filePath);
        var parsed = MinimalCsvParser.Parse(content);

        // Only one record (the header row)
        Assert.Single(parsed);
        Assert.Equal(["scenario", "step_name", "reason"], parsed[0]);
    }
}
