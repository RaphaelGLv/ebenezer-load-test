// Feature: loadtest-csv-error-report, Property 1: Round-trip RFC 4180
// Feature: loadtest-csv-error-report, Property 3: Validacao do cabecalho
// Feature: loadtest-csv-error-report, Property 4: Cabeçalho como primeira linha e caso sem dados
using EbenezerBackend.LoadTests.Reporting;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace EbenezerBackend.LoadTests.Tests;

/// <summary>
/// Property-based tests for <see cref="CsvWriter"/>.
/// Uses FsCheck 3.x C# API (FsCheck.Fluent namespace).
/// </summary>
public sealed class CsvWriterPropertyTests
{
    // ---------------------------------------------------------------------------
    // Generators
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Generates a nullable string that may be null, empty, or contain "malicious"
    /// characters (commas, double-quotes, CR, LF, CRLF) as well as arbitrary strings.
    /// </summary>
    private static Gen<string?> MaliciousStringGen()
    {
        // Fixed pool of tricky strings (including null)
        Gen<string?> trickyGen = Gen.Elements<string?>(
            null,
            "",
            ",",
            "\"",
            "\"\"",
            "\r",
            "\n",
            "\r\n",
            "a,b",
            "a\"b",
            "a\rb",
            "a\nb",
            "a\r\nb",
            "hello world",
            "plain text"
        );

        // Arbitrary FsCheck-generated string, cast to string?
        Gen<string?> arbitraryGen = ArbMap.Default.GeneratorFor<string>()
            .Select(s => (string?)s);

        // Weighted mix: 2 parts tricky, 1 part arbitrary
        return Gen.Frequency(
            (2, trickyGen),
            (1, arbitraryGen));
    }

    /// <summary>
    /// Fixed pool of unique column name tokens used to build valid headers.
    /// </summary>
    private static readonly string[] ColumnNamePool =
    {
        "col_a", "col_b", "col_c", "col_d", "col_e", "col_f",
        "endpoint", "error_msg", "count_col", "id_col", "name_col", "val_col"
    };

    /// <summary>
    /// Generates a non-empty list of 1-6 distinct, non-empty, non-whitespace column names
    /// by taking a random sized prefix of a shuffled version of <see cref="ColumnNamePool"/>.
    /// </summary>
    private static Gen<List<string>> ValidHeaderGen()
    {
        return Gen.Choose(1, 6).Select(count =>
            ColumnNamePool
                .OrderBy(_ => System.Guid.NewGuid())
                .Take(count)
                .ToList());
    }

    /// <summary>
    /// Generates a list of 0-10 data rows, each containing exactly
    /// <paramref name="fieldCount"/> nullable string fields.
    /// </summary>
    private static Gen<List<List<string?>>> DataRowsGen(int fieldCount)
    {
        // A single row: a list of `fieldCount` malicious strings
        Gen<List<string?>> rowGen =
            (from _ in Gen.Choose(0, 0) // anchor for LINQ
             select Enumerable.Range(0, fieldCount)
                 .Select(_ => (string?)null)
                 .ToList())
            .SelectMany(dummy =>
                // Generate each field independently
                Enumerable.Range(0, fieldCount)
                    .Select(_ => MaliciousStringGen())
                    .Aggregate(
                        Gen.Constant(new List<string?>()),
                        (accGen, fieldGen) =>
                            from acc in accGen
                            from field in fieldGen
                            select new List<string?>(acc) { field }));

        return Gen.Choose(0, 10).SelectMany(rowCount =>
        {
            if (rowCount == 0)
                return Gen.Constant(new List<List<string?>>());

            // Build up `rowCount` rows, each independently generated
            return Enumerable.Range(0, rowCount)
                .Select(_ => rowGen)
                .Aggregate(
                    Gen.Constant(new List<List<string?>>()),
                    (accGen, rg) =>
                        from acc in accGen
                        from row in rg
                        select new List<List<string?>>(acc) { row });
        });
    }

    /// <summary>
    /// Generates a (header, rows) pair with consistent field counts.
    /// </summary>
    private static Gen<(List<string> header, List<List<string?>> rows)> CsvTableGen()
    {
        return from header in ValidHeaderGen()
               from rows in DataRowsGen(header.Count)
               select (header, rows);
    }

    // ---------------------------------------------------------------------------
    // Property 1: Round-trip RFC 4180
    // Validates: Requirements 2.6, 4.1, 4.3, 4.4
    // ---------------------------------------------------------------------------

    [Property(MaxTest = 100)]
    public Property Write_Property1_RoundTripRfc4180()
    {
        // Feature: loadtest-csv-error-report, Property 1: Round-trip RFC 4180
        //
        // For every valid header and every list of data rows with consistent field count
        // (arbitrary fields including commas, double-quotes, CR, LF, CRLF, empty strings,
        // and nulls), reading with an RFC-4180-compliant reader the document produced by
        // CsvWriter.Write recovers, field by field, exactly the original values
        // (treating null as empty field).

        return Prop.ForAll(
            CsvTableGen().ToArbitrary(),
            tuple =>
            {
                var (header, rows) = tuple;

                // Adapt to the IReadOnlyList types expected by CsvWriter
                IReadOnlyList<string> csvHeader = header.AsReadOnly();
                IReadOnlyList<IReadOnlyList<string?>> csvRows =
                    rows.Select(r => (IReadOnlyList<string?>)r.AsReadOnly())
                        .ToList()
                        .AsReadOnly();

                // 1. Produce the CSV document via CsvWriter
                string csvText = CsvWriter.Write(csvHeader, csvRows);

                // 2. Parse with the independent minimal RFC 4180 parser
                List<List<string>> parsed = MinimalCsvParser.Parse(csvText);

                // --- Check A: record count = 1 header + N data rows ---
                int expectedRecordCount = 1 + rows.Count;
                if (parsed.Count != expectedRecordCount)
                    return false.Label(
                        $"Record count mismatch: expected {expectedRecordCount}, got {parsed.Count}");

                // --- Check B: first parsed record == header values ---
                List<string> parsedHeader = parsed[0];
                if (parsedHeader.Count != header.Count)
                    return false.Label(
                        $"Header field count mismatch: expected {header.Count}, got {parsedHeader.Count}");

                for (int j = 0; j < header.Count; j++)
                {
                    if (parsedHeader[j] != header[j])
                        return false.Label(
                            $"Header field {j} mismatch: expected '{header[j]}', got '{parsedHeader[j]}'");
                }

                // --- Check C: each data row recovers original values (null treated as "") ---
                for (int i = 0; i < rows.Count; i++)
                {
                    List<string> parsedRow = parsed[i + 1];
                    if (parsedRow.Count != header.Count)
                        return false.Label(
                            $"Row {i} field count mismatch: expected {header.Count}, got {parsedRow.Count}");

                    for (int j = 0; j < header.Count; j++)
                    {
                        string expected = rows[i][j] ?? string.Empty;
                        if (parsedRow[j] != expected)
                            return false.Label(
                                $"Row {i}, field {j} mismatch: expected '{EscapeForDisplay(expected)}', got '{EscapeForDisplay(parsedRow[j])}'");
                    }
                }

                return true.ToProperty();
            });
    }

    private static string EscapeForDisplay(string s) =>
        s.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\"", "\\\"");

    // ---------------------------------------------------------------------------
    // Property 5: Escaping de campo conforme RFC 4180
    // Validates: Requirements 4.1, 4.4
    // ---------------------------------------------------------------------------

    /// <summary>
    /// **Validates: Requirements 4.1, 4.4**
    ///
    /// Property 5: For any field value, CsvWriter.EscapeField produces:
    ///   1. empty string when value is null or empty
    ///   2. a double-quote-delimited value with each internal quote doubled
    ///      when value contains ',', '"', CR, or LF
    ///   3. the value unchanged otherwise
    /// </summary>
    [Property(MaxTest = 100, Arbitrary = new[] { typeof(MaliciousStringArbitrary) })]
    public bool EscapeField_Property5_RfcEscaping(string? value)
    {
        // Feature: loadtest-csv-error-report, Property 5: Escaping de campo conforme RFC 4180
        var result = CsvWriter.EscapeField(value);

        // Rule 1: null or empty -> empty string
        if (string.IsNullOrEmpty(value))
            return result == string.Empty;

        bool needsQuoting = value.Contains(',')
                         || value.Contains('"')
                         || value.Contains('\r')
                         || value.Contains('\n');

        if (needsQuoting)
        {
            // Rule 2a: result must be delimited by outer double-quotes
            if (result.Length < 2 || !result.StartsWith("\"") || !result.EndsWith("\""))
                return false;

            // Rule 2b: strip outer quotes and verify every internal '"' is doubled
            var inner = result.Substring(1, result.Length - 2);
            var expectedInner = value.Replace("\"", "\"\"");
            return inner == expectedInner;
        }
        else
        {
            // Rule 3: no special chars -> value unchanged
            return result == value;
        }
    }

    // ---------------------------------------------------------------------------
    // Property 3: Validação do cabeçalho
    // Validates: Requirements 1.2, 4.2
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Generates an INVALID header: empty list, contains an empty/whitespace name,
    /// or contains duplicate names.
    /// </summary>
    private static Gen<List<string>> InvalidHeaderGen()
    {
        // Case A: empty header (zero columns)
        Gen<List<string>> emptyGen = Gen.Constant(new List<string>());

        // Case B: header with at least one empty name at a random position
        Gen<List<string>> emptyNameGen =
            from count in Gen.Choose(1, 4)
            from idx in Gen.Choose(0, count - 1)
            select Enumerable.Range(0, count)
                             .Select(i => i == idx ? "" : $"col{i}")
                             .ToList();

        // Case C: header with at least one whitespace-only name at a random position
        Gen<List<string>> whitespaceNameGen =
            from count in Gen.Choose(1, 4)
            from idx in Gen.Choose(0, count - 1)
            select Enumerable.Range(0, count)
                             .Select(i => i == idx ? "   " : $"col{i}")
                             .ToList();

        // Case D: header with a duplicate name (last element equals first)
        Gen<List<string>> duplicateGen =
            from count in Gen.Choose(2, 5)
            select Enumerable.Range(0, count)
                             .Select(i => i == count - 1 ? "col0" : $"col{i}")
                             .ToList();

        return Gen.Frequency(
            (1, emptyGen),
            (3, emptyNameGen),
            (2, whitespaceNameGen),
            (3, duplicateGen));
    }

    /// <summary>
    /// Generates a valid header paired with exactly one row whose field count
    /// intentionally differs from header.Count.
    /// </summary>
    private static Gen<(List<string> Header, List<IReadOnlyList<string?>> Rows)> ValidHeaderWithBadRowGen()
    {
        return ValidHeaderGen().SelectMany(header =>
        {
            int correct = header.Count;
            // Wrong count: any value in [0 .. correct+3] that is NOT correct
            Gen<int> wrongCountGen = Gen.Choose(0, correct + 3).Where(n => n != correct);
            Gen<string?> fieldGen = Gen.Elements<string?>("val", "x", "1", null, "");

            return wrongCountGen.SelectMany(wrongCount =>
                Gen.ArrayOf(fieldGen, wrongCount)
                   .Select(arr =>
                   {
                       IReadOnlyList<string?> badRow = arr.ToList().AsReadOnly();
                       return (header, new List<IReadOnlyList<string?>> { badRow });
                   }));
        });
    }

    /// <summary>
    /// **Validates: Requirements 1.2, 4.2**
    ///
    /// Property 3 - Case A (invalid inputs -> ArgumentException):
    /// For every header that contains at least one empty name or duplicate names,
    /// OR for every valid header with a data row whose field count differs from
    /// header.Count, CsvWriter.Write must throw ArgumentException.
    /// </summary>
    [Property(MaxTest = 100)]
    public Property Write_Property3_HeaderValidation_InvalidThrows()
    {
        // Sub-case 1: invalid header (empty / empty-name / whitespace-name / duplicate)
        var invalidHeaderProp = Prop.ForAll(
            InvalidHeaderGen().ToArbitrary(),
            invalidHeader =>
            {
                IReadOnlyList<string> hdr = invalidHeader.AsReadOnly();
                IReadOnlyList<IReadOnlyList<string?>> rows =
                    new List<IReadOnlyList<string?>>().AsReadOnly();
                try
                {
                    CsvWriter.Write(hdr, rows);
                    return false.Label("Write should have thrown ArgumentException for invalid header");
                }
                catch (ArgumentException)
                {
                    return true.Label("ArgumentException thrown as expected");
                }
                catch (Exception ex)
                {
                    return false.Label($"Expected ArgumentException but got {ex.GetType().Name}");
                }
            });

        // Sub-case 2: valid header but at least one row has wrong field count
        var wrongRowProp = Prop.ForAll(
            ValidHeaderWithBadRowGen().ToArbitrary(),
            pair =>
            {
                var (header, rows) = pair;
                IReadOnlyList<string> hdr = header.AsReadOnly();
                IReadOnlyList<IReadOnlyList<string?>> typedRows =
                    rows.Select(r => r).ToList().AsReadOnly();
                try
                {
                    CsvWriter.Write(hdr, typedRows);
                    return false.Label("Write should have thrown ArgumentException for wrong row field count");
                }
                catch (ArgumentException)
                {
                    return true.Label("ArgumentException thrown as expected");
                }
                catch (Exception ex)
                {
                    return false.Label($"Expected ArgumentException but got {ex.GetType().Name}");
                }
            });

        return invalidHeaderProp.And(wrongRowProp);
    }

    /// <summary>
    /// **Validates: Requirements 1.2, 4.2**
    ///
    /// Property 3 - Case B (valid inputs -> success):
    /// For every valid header (non-empty, no empty names, no duplicates) with rows
    /// that each have exactly header.Count fields, CsvWriter.Write succeeds without
    /// throwing any exception.
    /// </summary>
    [Property(MaxTest = 100)]
    public Property Write_Property3_HeaderValidation_ValidSucceeds()
    {
        // Reuse ValidHeaderGen + DataRowsGen (consistent field count guaranteed by DataRowsGen)
        return Prop.ForAll(
            CsvTableGen().ToArbitrary(),
            tuple =>
            {
                var (header, rows) = tuple;
                IReadOnlyList<string> hdr = header.AsReadOnly();
                IReadOnlyList<IReadOnlyList<string?>> typedRows =
                    rows.Select(r => (IReadOnlyList<string?>)r.AsReadOnly())
                        .ToList()
                        .AsReadOnly();
                try
                {
                    CsvWriter.Write(hdr, typedRows);
                    return true.Label("Write succeeded as expected");
                }
                catch (Exception ex)
                {
                    return false.Label(
                        $"Unexpected exception: {ex.GetType().Name}: {ex.Message}");
                }
            });
    }

    // ---------------------------------------------------------------------------
    // Property 4: Cabeçalho como primeira linha e caso sem dados
    // Validates: Requirements 1.1, 1.4, 2.2, 2.7
    // ---------------------------------------------------------------------------

    /// <summary>
    /// **Validates: Requirements 1.1, 1.4, 2.2, 2.7**
    ///
    /// Property 4 - Part A: For every valid header and every list of rows, the first
    /// logical record parsed from the CSV document produced by CsvWriter.Write is
    /// exactly the header values.
    ///
    /// Property 4 - Part B: When rows is empty, the produced document equals exactly
    /// CsvWriter.WriteRecord(header) - one CRLF-terminated line, no extra CRLF.
    /// </summary>
    [Property(MaxTest = 100)]
    public Property Write_Property4_HeaderFirstAndEmptyCase()
    {
        // Feature: loadtest-csv-error-report, Property 4: Cabeçalho como primeira linha e caso sem dados
        //
        // Part A: For every valid header and every list of rows, the first logical record
        //         parsed from the CSV document is exactly the header values.
        //
        // Part B: When rows is empty, the document equals exactly WriteRecord(header)
        //         - one CRLF-terminated line, no extra CRLF.

        return Prop.ForAll(
            CsvTableGen().ToArbitrary(),
            tuple =>
            {
                var (header, rows) = tuple;

                IReadOnlyList<string> csvHeader = header.AsReadOnly();
                IReadOnlyList<IReadOnlyList<string?>> csvRows =
                    rows.Select(r => (IReadOnlyList<string?>)r.AsReadOnly())
                        .ToList()
                        .AsReadOnly();

                // --- Part A: header is the first logical record ---
                string text = CsvWriter.Write(csvHeader, csvRows);
                List<List<string>> parsed = MinimalCsvParser.Parse(text);

                // Must have at least one record (the header)
                if (parsed.Count == 0)
                    return false.Label("Part A: no records found in output");

                List<string> firstRecord = parsed[0];

                // First record must have the same field count as header
                if (firstRecord.Count != header.Count)
                    return false.Label(
                        $"Part A: first record has {firstRecord.Count} fields, expected {header.Count}");

                // Each field of the first record must equal the corresponding header value exactly
                for (int i = 0; i < header.Count; i++)
                {
                    if (firstRecord[i] != header[i])
                        return false.Label(
                            $"Part A: first record field[{i}] = \"{firstRecord[i]}\", expected \"{header[i]}\"");
                }

                // --- Part B: empty rows case ---
                string emptyText = CsvWriter.Write(csvHeader, Array.Empty<string[]>());
                string expectedText = CsvWriter.WriteRecord(csvHeader);

                // Document must equal exactly the serialized header (one CRLF-terminated line)
                if (emptyText != expectedText)
                    return false.Label("Part B: empty-rows document does not equal WriteRecord(header)");

                // Must end with exactly one CRLF
                if (!emptyText.EndsWith("\r\n"))
                    return false.Label("Part B: document does not end with CRLF");

                // Must NOT end with a double CRLF
                if (emptyText.EndsWith("\r\n\r\n"))
                    return false.Label("Part B: document ends with double CRLF");

                return true.Label("Property 4 holds");
            });
    }
}

/// <summary>
/// FsCheck Arbitrary provider for nullable "malicious" strings used in Property 5.
/// Produces: null, "", plain strings (no special chars), and strings with ',', '"', '\r', '\n', '\r\n' embedded.
/// Must be a public static class with public static Arbitrary methods for FsCheck to discover it.
/// </summary>
public static class MaliciousStringArbitrary
{
    public static Arbitrary<string?> NullableString()
    {
        // Fixed pool of tricky strings (null, empty, special chars)
        Gen<string?> trickyGen = Gen.Elements<string?>(new string?[]
        {
            null, "", ",", "\"", "\"\"", "\r", "\n", "\r\n",
            "a,b", "a\"b", "a\rb", "a\nb", "a\r\nb",
            "hello,world", "say \"hi\"", "line1\r\nline2"
        });

        // Arbitrary FsCheck-generated string (may or may not contain special chars)
        Gen<string?> arbitraryGen = ArbMap.Default.GeneratorFor<string>()
            .Select(s => (string?)s);

        // Weighted mix: 3 parts tricky (guarantees coverage), 1 part arbitrary
        Gen<string?> fullGen = Gen.Frequency<string?>(new[]
        {
            (3, trickyGen),
            (1, arbitraryGen)
        });

        return Arb.From(fullGen);
    }
}
