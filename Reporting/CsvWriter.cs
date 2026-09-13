namespace EbenezerBackend.LoadTests.Reporting;

/// <summary>
/// Utilitário de serialização CSV em conformidade com RFC 4180.
/// Centraliza o cumprimento dos Requirements 1 e 4.
/// </summary>
public static class CsvWriter
{
    public const string FieldSeparator = ",";
    public const string RecordTerminator = "\r\n"; // CRLF (Req 4.5)

    /// <summary>
    /// Escapa um único campo conforme RFC 4180 (Req 4.1, 4.4).
    /// <list type="bullet">
    ///   <item><c>null</c> ou vazio → string vazia (campo vazio, preserva contagem de campos).</item>
    ///   <item>Contém <c>,</c>, <c>"</c>, CR ou LF → envolve em aspas duplas e duplica aspas internas.</item>
    ///   <item>Caso contrário → valor inalterado.</item>
    /// </list>
    /// </summary>
    public static string EscapeField(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        bool needsQuoting = value.Contains(',')
                         || value.Contains('"')
                         || value.Contains('\r')
                         || value.Contains('\n');

        if (!needsQuoting)
            return value;

        // Wrap in double-quotes and double each internal quote (RFC 4180 §2.7)
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>
    /// Serializes a single record (line) already with CRLF terminator (Req 4.5).
    /// Each field is escaped via <see cref="EscapeField"/> and fields are joined
    /// by <see cref="FieldSeparator"/>.
    /// </summary>
    public static string WriteRecord(IReadOnlyList<string?> fields)
    {
        return string.Join(FieldSeparator, fields.Select(EscapeField)) + RecordTerminator;
    }

    /// <summary>
    /// Serializes a complete CSV document: header + data rows (Req 1.1–1.4, 4.2, 4.5).
    /// </summary>
    /// <param name="header">Column names. Must be non-empty, with no empty/whitespace names and no duplicates.</param>
    /// <param name="rows">Data rows. Each row must have exactly <c>header.Count</c> fields.</param>
    /// <returns>The full CSV text with every record terminated by CRLF.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when:
    /// <list type="bullet">
    ///   <item>header is empty (Req 1.2)</item>
    ///   <item>any header name is empty or whitespace (Req 1.2)</item>
    ///   <item>header contains duplicate names — case-sensitive comparison (Req 1.2)</item>
    ///   <item>any row has a different field count than header (Req 4.2)</item>
    /// </list>
    /// </exception>
    public static string Write(IReadOnlyList<string> header,
                               IReadOnlyList<IReadOnlyList<string?>> rows)
    {
        // Validate header not empty (Req 1.2)
        if (header.Count == 0)
            throw new ArgumentException("Header must contain at least one column name.", nameof(header));

        // Validate no empty/whitespace column names (Req 1.2)
        for (int i = 0; i < header.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(header[i]))
                throw new ArgumentException(
                    $"Header column at index {i} is empty or whitespace.", nameof(header));
        }

        // Validate no duplicate column names — case-sensitive (Req 1.2)
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in header)
        {
            if (!seen.Add(name))
                throw new ArgumentException(
                    $"Header contains duplicate column name: \"{name}\".", nameof(header));
        }

        // Validate each row has exactly header.Count fields (Req 4.2)
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Count != header.Count)
                throw new ArgumentException(
                    $"Row at index {i} has {rows[i].Count} field(s) but header has {header.Count}.",
                    nameof(rows));
        }

        // Build the document: header first, then data rows (Req 1.1, 1.4, 4.5)
        var sb = new System.Text.StringBuilder();
        sb.Append(WriteRecord(header));

        foreach (var row in rows)
            sb.Append(WriteRecord(row));

        return sb.ToString();
    }
}
