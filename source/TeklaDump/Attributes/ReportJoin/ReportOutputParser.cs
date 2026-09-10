using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace TeklaDump.Attributes.ReportJoin;

/// <summary>
/// Parses the report Tekla produced back into rows keyed by GUID.
/// </summary>
/// <remarks>
/// The parser is deliberately forgiving in one direction and strict in the other: any line that
/// does not start with the <c>TD;</c> tag is ignored (page headers, blank lines, whatever the
/// environment's report settings add), and a tagged line whose column count does not match the row
/// spec is dropped with a warning rather than silently shifting every value one column left.
/// </remarks>
internal static class ReportOutputParser
{
    /// <summary>
    /// Parses <paramref name="path"/> using the row specs the template was built from.
    /// </summary>
    /// <param name="path">The report file Tekla produced.</param>
    /// <param name="rowsByContentType">The row specs the template was built from, keyed by content type.</param>
    /// <param name="malformedLines">Count of tagged lines that could not be used.</param>
    public static Dictionary<string, IReadOnlyList<KeyValuePair<string, object>>> Parse(
        string path,
        IReadOnlyDictionary<string, ReportRowSpec> rowsByContentType,
        out int malformedLines)
    {
        var result = new Dictionary<string, IReadOnlyList<KeyValuePair<string, object>>>(StringComparer.OrdinalIgnoreCase);
        malformedLines = 0;

        foreach (var line in ReadLines(path))
        {
            if (!line.StartsWith(ReportTemplateWriter.RowTag + ReportTemplateWriter.Separator, StringComparison.Ordinal))
                continue;

            var fields = line.Split(ReportTemplateWriter.Separator);
            if (fields.Length < 3) { malformedLines++; continue; }

            var contentType = fields[1].Trim();
            if (!rowsByContentType.TryGetValue(contentType, out var row)) { malformedLines++; continue; }

            var guid = NormalizeGuid(fields[2]);
            if (guid is null) { malformedLines++; continue; }

            // tag + content type + GUID, then one field per attribute.
            if (fields.Length < 3 + row.Attributes.Count) { malformedLines++; continue; }

            var values = new List<KeyValuePair<string, object>>(row.Attributes.Count);
            for (var i = 0; i < row.Attributes.Count; i++)
            {
                var attribute = row.Attributes[i];
                var raw = fields[3 + i].Trim();
                if (raw.Length == 0) continue;   // the object has no value for this attribute

                var value = Convert(raw, attribute.ValueType);
                if (value is not null) values.Add(new KeyValuePair<string, object>(attribute.Name, value));
            }

            // Last row for a GUID wins. A GUID appearing twice means two rows matched the same
            // object (a part is also a PART inside an ASSEMBLY row's scope in some templates);
            // the values are the same either way.
            result[guid] = values;
        }

        return result;
    }

    /// <summary>
    /// Report GUIDs come back in whatever form the environment writes — braced, upper case, or
    /// with an ID prefix. They are normalized to the same plain form the dump emits, or rejected.
    /// </summary>
    private static string? NormalizeGuid(string raw)
    {
        var text = raw.Trim().Trim('{', '}');
        if (text.Length == 0) return null;
        return Guid.TryParse(text, out var parsed) && parsed != Guid.Empty ? parsed.ToString() : null;
    }

    private static object? Convert(string raw, TemplateValueType valueType)
    {
        switch (valueType)
        {
            case TemplateValueType.Float:
                return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                    ? (object)d
                    : null;

            case TemplateValueType.Integer:
                return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
                    ? (object)i
                    : null;

            default:
                return raw;
        }
    }

    /// <summary>
    /// Reads the report with its encoding detected. Tekla writes reports in the environment's own
    /// code page, and decoding an ANSI report as UTF-8 corrupts every profile name with a
    /// non-ASCII character in it.
    /// </summary>
    private static IEnumerable<string> ReadLines(string path)
    {
        var bytes = File.ReadAllBytes(path);

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Split(Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));

        var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        try
        {
            return Split(strict.GetString(bytes));
        }
        catch (DecoderFallbackException)
        {
            return Split(Encoding.Default.GetString(bytes));
        }
    }

    private static string[] Split(string text) =>
        text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
}
