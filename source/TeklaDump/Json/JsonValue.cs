using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TeklaDump.Json;

/// <summary>
/// Minimal, dependency-free JSON document model + writer. net48 has no <c>System.Text.Json</c>,
/// and the dump has an arbitrary, nested shape that
/// <see cref="System.Runtime.Serialization.Json.DataContractJsonSerializer"/> can't express
/// cleanly, so we build a tiny node tree and render it ourselves.
/// </summary>
/// <remarks>
/// Lifted from TeklaLookup, plus a compact renderer: NDJSON needs one record per line, and the
/// header line of a bulk file is the same document type as an indented inspect document.
/// </remarks>
public abstract class JsonValue
{
    public static JsonValue Null { get; } = new JsonScalar("null", isRaw: true);
    public static JsonValue Bool(bool value) => new JsonScalar(value ? "true" : "false", isRaw: true);
    public static JsonValue String(string value) => new JsonScalar(value, isRaw: false);

    /// <summary>
    /// A JSON number. Rounded to 6 decimals — Tekla's own internal precision is coarser than that,
    /// and unrounded doubles are what makes two runs of the same dump differ in the last mantissa
    /// bit and break a golden diff (schema README, "Determinism").
    /// </summary>
    public static JsonValue Number(double value)
    {
        // JSON has no NaN/Infinity literals — fall back to a string so the document stays valid.
        if (double.IsNaN(value) || double.IsInfinity(value))
            return String(value.ToString(CultureInfo.InvariantCulture));
        var rounded = Math.Round(value, 6, MidpointRounding.AwayFromZero);
        // "0.######" and not "R": R round-trips the binary double (0.1 + 0.2 -> 0.30000000000000004),
        // which defeats the rounding above. Six decimals of a rounded value is exact in this format.
        return new JsonScalar(rounded.ToString("0.######", CultureInfo.InvariantCulture), isRaw: true);
    }

    public static JsonValue Number(long value) =>
        new JsonScalar(value.ToString(CultureInfo.InvariantCulture), isRaw: true);

    /// <summary>Two-space indented, multi-line. Used by inspect mode and <c>--pretty</c>.</summary>
    public string ToIndentedString()
    {
        var builder = new StringBuilder();
        Write(builder, 0, indented: true);
        return builder.ToString();
    }

    /// <summary>Single-line, no whitespace. One NDJSON line.</summary>
    public string ToCompactString()
    {
        var builder = new StringBuilder();
        Write(builder, 0, indented: false);
        return builder.ToString();
    }

    internal abstract void Write(StringBuilder builder, int indent, bool indented);

    private protected static void AppendIndent(StringBuilder builder, int indent, bool indented)
    {
        if (indented) builder.Append(' ', indent * 2);
    }

    private protected static void AppendEscaped(StringBuilder builder, string value)
    {
        builder.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (c < ' ')
                        builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        builder.Append(c);
                    break;
            }
        }
        builder.Append('"');
    }
}

internal sealed class JsonScalar : JsonValue
{
    private readonly string _text;
    private readonly bool _isRaw;

    public JsonScalar(string text, bool isRaw)
    {
        _text = text;
        _isRaw = isRaw;
    }

    internal override void Write(StringBuilder builder, int indent, bool indented)
    {
        if (_isRaw)
            builder.Append(_text);
        else
            AppendEscaped(builder, _text);
    }
}

/// <summary>Insertion-ordered JSON object. Order is preserved so the dump stays stable and readable.</summary>
public sealed class JsonObject : JsonValue
{
    private readonly List<KeyValuePair<string, JsonValue>> _members = new();

    public int Count => _members.Count;

    public void Add(string key, JsonValue value) => _members.Add(new KeyValuePair<string, JsonValue>(key, value));

    /// <summary>Adds <paramref name="value"/> unless it is null. Omission is how "no value" is expressed.</summary>
    public void AddIfPresent(string key, JsonValue? value)
    {
        if (value is not null) _members.Add(new KeyValuePair<string, JsonValue>(key, value));
    }

    internal override void Write(StringBuilder builder, int indent, bool indented)
    {
        if (_members.Count == 0)
        {
            builder.Append("{}");
            return;
        }

        builder.Append('{');
        if (indented) builder.Append('\n');
        for (var i = 0; i < _members.Count; i++)
        {
            AppendIndent(builder, indent + 1, indented);
            AppendEscaped(builder, _members[i].Key);
            builder.Append(':');
            if (indented) builder.Append(' ');
            _members[i].Value.Write(builder, indent + 1, indented);
            if (i < _members.Count - 1) builder.Append(',');
            if (indented) builder.Append('\n');
        }
        AppendIndent(builder, indent, indented);
        builder.Append('}');
    }
}

public sealed class JsonArray : JsonValue
{
    private readonly List<JsonValue> _items = new();

    public int Count => _items.Count;

    public void Add(JsonValue value) => _items.Add(value);

    internal override void Write(StringBuilder builder, int indent, bool indented)
    {
        if (_items.Count == 0)
        {
            builder.Append("[]");
            return;
        }

        builder.Append('[');
        if (indented) builder.Append('\n');
        for (var i = 0; i < _items.Count; i++)
        {
            AppendIndent(builder, indent + 1, indented);
            _items[i].Write(builder, indent + 1, indented);
            if (i < _items.Count - 1) builder.Append(',');
            if (indented) builder.Append('\n');
        }
        AppendIndent(builder, indent, indented);
        builder.Append(']');
    }
}
