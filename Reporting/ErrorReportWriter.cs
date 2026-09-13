namespace EbenezerBackend.LoadTests.Reporting;

/// <summary>
/// Orchestrates building and writing the <c>errors-by-endpoint.csv</c> report
/// to the run's output folder (Requirements 5.1, 5.2).
/// </summary>
public static class ErrorReportWriter
{
    /// <summary>Fixed file name for the error report (Req 5.2).</summary>
    public const string FileName = "errors-by-endpoint.csv";

    /// <summary>
    /// Builds and writes the error report to the given folder.
    /// Never throws: on failure, writes a warning to the console and returns <c>false</c>
    /// (Req 5.5, 6.1, 6.4). Already-written artifacts are never removed (Req 6.2).
    /// </summary>
    /// <param name="reportFolder">
    /// Destination folder (<c>reports/{approach}/{runName}</c>). Created if absent (Req 5.3).
    /// </param>
    /// <param name="failures">Step failures to aggregate into the report.</param>
    /// <param name="knownEndpoints">
    /// Optional set of known endpoint names. When <c>null</c>, all step names are treated
    /// as valid endpoints (see <see cref="ErrorReportBuilder.ResolveEndpoint"/>).
    /// </param>
    /// <returns>
    /// <c>true</c> when the file was written successfully; <c>false</c> on any error.
    /// </returns>
    public static bool TryWrite(string reportFolder,
                                IEnumerable<StepFailure> failures,
                                ISet<string>? knownEndpoints = null)
    {
        try
        {
            // 1. Ensure destination directory exists, including any intermediate dirs (Req 5.3)
            Directory.CreateDirectory(reportFolder);

            // 2. Build the full file path (Req 5.1, 5.2)
            string path = Path.Combine(reportFolder, FileName);

            // 3. Aggregate failures into ordered rows
            var errorRows = ErrorReportBuilder.Build(failures, knownEndpoints);

            // 4. Project rows to the shape expected by CsvWriter
            var rows = errorRows
                .Select(r => (IReadOnlyList<string?>)new string?[] { r.Scenario, r.Endpoint, r.ErrorMessage, r.Count.ToString() })
                .ToList();

            // 5. Serialize and write with UTF-8 without BOM, overwriting any existing file (Req 5.4)
            string content = CsvWriter.Write(ErrorReportBuilder.Header, rows);
            File.WriteAllText(path, content, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            return true;
        }
        catch (Exception ex)
        {
            // Req 5.5, 6.1: emit console warning, do not propagate, do not touch other artifacts
            Console.WriteLine($"[Warning] Failed to write error report '{FileName}': {ex.Message}");
            return false;
        }
    }
}
