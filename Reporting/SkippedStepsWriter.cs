namespace EbenezerBackend.LoadTests.Reporting;

/// <summary>
/// Orchestrates sorting and writing the <c>skipped-steps.csv</c> report
/// to the run's output folder (Requirements 8.1, 8.7, 8.8).
/// </summary>
public static class SkippedStepsWriter
{
    /// <summary>Fixed file name for the skipped steps report (Req 8.1).</summary>
    public const string FileName = "skipped-steps.csv";

    /// <summary>
    /// Sorts and writes the skipped steps report to the given folder.
    /// Never throws: on failure, writes a warning to the console and returns <c>false</c>
    /// (Req 8.7). Already-written artifacts are never removed (Req 8.8).
    /// </summary>
    /// <param name="reportFolder">
    /// Destination folder. Created if absent (Req 8.1).
    /// </param>
    /// <param name="records">Skipped step records to write.</param>
    /// <returns>
    /// <c>true</c> when the file was written successfully; <c>false</c> on any error.
    /// </returns>
    public static bool TryWrite(string reportFolder, IEnumerable<SkippedStep> records)
    {
        try
        {
            Directory.CreateDirectory(reportFolder);
            string path = Path.Combine(reportFolder, FileName);
            var sorted = SkippedStepsBuilder.Sort(records);
            var rows = sorted
                .Select(s => (IReadOnlyList<string?>)new string?[] { s.Scenario, s.StepName, s.Reason })
                .ToList();
            string content = CsvWriter.Write(SkippedStepsBuilder.Header, rows);
            File.WriteAllText(path, content, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Warning] Failed to write skipped steps report '{FileName}': {ex.Message}");
            return false;
        }
    }
}
