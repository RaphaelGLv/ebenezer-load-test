// Feature: loadtest-csv-error-report — Independent RFC 4180 parser for test verification.
// This parser does NOT reuse CsvWriter and exists solely to verify round-trip correctness.
namespace EbenezerBackend.LoadTests.Tests;

/// <summary>
/// Minimal, self-contained RFC 4180 CSV parser used exclusively in property-based tests
/// to verify the round-trip correctness of <c>CsvWriter</c> without sharing its implementation.
/// </summary>
/// <remarks>
/// Parsing rules (RFC 4180 §2):
/// <list type="bullet">
///   <item>Records are separated by CRLF (<c>\r\n</c>). A final record MAY omit the trailing CRLF.</item>
///   <item>A field enclosed in double-quotes may contain commas, double-quotes (escaped as <c>""</c>),
///         CR, LF, and CRLF.</item>
///   <item>A field that is not enclosed in double-quotes is taken verbatim up to the next comma or CRLF.</item>
/// </list>
/// </remarks>
public static class MinimalCsvParser
{
    /// <summary>
    /// Parses a CSV document and returns each record as a list of field strings.
    /// Trailing CRLF after the last record is consumed but does not produce an extra empty record.
    /// </summary>
    public static List<List<string>> Parse(string csv)
    {
        var records = new List<List<string>>();
        int pos = 0;
        int len = csv.Length;

        while (pos <= len)
        {
            // At or past end — stop if we already consumed everything
            if (pos == len)
                break;

            var record = ParseRecord(csv, ref pos, len);
            records.Add(record);

            // Consume the CRLF record terminator if present
            if (pos < len && csv[pos] == '\r' && pos + 1 < len && csv[pos + 1] == '\n')
            {
                pos += 2; // consume CRLF
                // If we just consumed the final CRLF and are now at end, stop — no ghost record
                if (pos == len)
                    break;
            }
            else if (pos < len && csv[pos] == '\n')
            {
                // Tolerate bare LF as record terminator (defensive)
                pos += 1;
                if (pos == len)
                    break;
            }
            else
            {
                // No record terminator — end of input after last record
                break;
            }
        }

        return records;
    }

    /// <summary>
    /// Parses one record (up to the next CRLF that is outside a quoted field, or end of input).
    /// Updates <paramref name="pos"/> to point just after the last character of the record
    /// (i.e., before the CRLF terminator, if any).
    /// </summary>
    private static List<string> ParseRecord(string csv, ref int pos, int len)
    {
        var fields = new List<string>();

        while (true)
        {
            fields.Add(ParseField(csv, ref pos, len));

            if (pos < len && csv[pos] == ',')
            {
                pos++; // consume field separator, continue to next field
            }
            else
            {
                // End of record: either CRLF, bare LF, or end of input
                break;
            }
        }

        return fields;
    }

    /// <summary>
    /// Parses one field value, advancing <paramref name="pos"/> past the field content.
    /// Handles both quoted and unquoted fields per RFC 4180.
    /// </summary>
    private static string ParseField(string csv, ref int pos, int len)
    {
        if (pos >= len)
            return string.Empty;

        if (csv[pos] == '"')
        {
            // Quoted field
            pos++; // skip opening quote
            var sb = new System.Text.StringBuilder();

            while (pos < len)
            {
                char ch = csv[pos];

                if (ch == '"')
                {
                    // Either escaped quote ("") or closing quote
                    if (pos + 1 < len && csv[pos + 1] == '"')
                    {
                        // Escaped double-quote: "" → "
                        sb.Append('"');
                        pos += 2;
                    }
                    else
                    {
                        // Closing quote
                        pos++;
                        break;
                    }
                }
                else
                {
                    sb.Append(ch);
                    pos++;
                }
            }

            return sb.ToString();
        }
        else
        {
            // Unquoted field: read until comma or CRLF/LF/end
            int start = pos;
            while (pos < len)
            {
                char ch = csv[pos];
                if (ch == ',' || ch == '\r' || ch == '\n')
                    break;
                pos++;
            }
            return csv.Substring(start, pos - start);
        }
    }
}
