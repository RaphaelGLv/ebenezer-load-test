using EbenezerBackend.LoadTests.Reporting;
using Xunit;

namespace EbenezerBackend.LoadTests.Tests;

/// <summary>
/// Example tests for the header structure of <c>resource-usage.csv</c> as
/// produced by <see cref="ResourceSampler"/> via <see cref="CsvWriter.WriteRecord"/>.
///
/// <para>
/// <see cref="ResourceSampler"/> requires a live Docker daemon and cannot be
/// instantiated in a unit test. Instead, these tests exercise <see cref="CsvWriter.WriteRecord"/>
/// directly with the same arguments that <see cref="ResourceSampler.Start"/> uses,
/// verifying the header and data-line structure without any Docker dependency.
/// </para>
/// </summary>
public sealed class ResourceSamplerTests
{
    // -------------------------------------------------------------------------
    // Test 1 – First line is the expected header (Req 1.1, 1.2)
    // -------------------------------------------------------------------------

    /// <summary>
    /// The first physical line written by <see cref="ResourceSampler.Start"/>
    /// is the CSV header <c>timestamp,container,cpu_percent,memory_mb</c>.
    /// <para>
    /// Req 1.1: header is the first (and only header) line.
    /// Req 1.2: header has exactly 4 distinct, non-empty column names in the
    /// expected order.
    /// </para>
    /// </summary>
    [Fact]
    public void ResourceSamplerHeader_FirstLineIsExpectedHeader()
    {
        // ResourceSampler.Start() calls:
        //   CsvWriter.WriteRecord(new[] { "timestamp", "container", "cpu_percent", "memory_mb" })
        // and then replaces \r\n with \n for LF compatibility.
        string headerLine = CsvWriter.WriteRecord(new[] { "timestamp", "container", "cpu_percent", "memory_mb" })
            .Replace("\r\n", "\n");

        // Req 1.1: header starts with the first column name (verifies it is the header line)
        Assert.StartsWith("timestamp,", headerLine);

        // Req 1.2: header has exactly 4 distinct non-empty column names in the correct order
        string[] columns = headerLine.TrimEnd('\n').Split(',');
        Assert.Equal(4, columns.Length);
        Assert.Equal("timestamp",   columns[0]);
        Assert.Equal("container",   columns[1]);
        Assert.Equal("cpu_percent", columns[2]);
        Assert.Equal("memory_mb",   columns[3]);
    }

    // -------------------------------------------------------------------------
    // Test 2 – Data line has the same field count as the header (Req 1.2, 1.3)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Each data line produced by <see cref="ResourceSampler"/> (via
    /// <see cref="CsvWriter.WriteRecord"/>) has the same number of fields as
    /// the header line, satisfying the consistent-field-count requirement.
    /// <para>
    /// Req 1.2: data columns align with the declared header columns.
    /// Req 1.3: the same field separator is used throughout the file.
    /// </para>
    /// </summary>
    [Fact]
    public void ResourceSamplerDataLine_HasSameFieldCountAsHeader()
    {
        // Simulate a data line exactly as ResourceSampler produces it
        string dataLine = CsvWriter.WriteRecord(new string?[]
        {
            DateTimeOffset.UtcNow.ToString("O"),
            "my-container",
            "42.50",
            "256.00"
        }).Replace("\r\n", "\n");

        string headerLine = CsvWriter.WriteRecord(new[] { "timestamp", "container", "cpu_percent", "memory_mb" })
            .Replace("\r\n", "\n");

        // Both header and data line must have the same number of comma-separated fields
        int headerFieldCount = headerLine.TrimEnd('\n').Split(',').Length;
        int dataFieldCount   = dataLine.TrimEnd('\n').Split(',').Length;

        Assert.Equal(headerFieldCount, dataFieldCount);
    }
}
