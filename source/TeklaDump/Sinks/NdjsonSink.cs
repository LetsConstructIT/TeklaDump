using System;
using System.IO;
using System.Text;
using TeklaDump.Json;

namespace TeklaDump.Sinks;

/// <summary>
/// Writes records straight to a stream, one compact JSON object per line.
/// </summary>
/// <remarks>
/// Nothing is accumulated: a record is written as its keys arrive and forgotten, so a 200k-object
/// dump costs the same memory as a 25-object one. That is the whole reason bulk mode exists, and
/// the reason it uses explicit extractors rather than the reflection walker — a walker cannot know
/// which members are cheap.
/// <para>
/// Framing: UTF-8 with NO byte order mark, "\n" line endings, one JSON value per line. Line 1 is
/// the header. A run interrupted with Ctrl+C leaves a valid truncated NDJSON file, because every
/// completed line is independently parseable.
/// </para>
/// </remarks>
internal sealed class NdjsonSink : IDumpSink, IDisposable
{
    private readonly StreamWriter _writer;
    private readonly StringBuilder _scratch = new StringBuilder();

    private bool _recordOpen;
    private bool _recordHasContent;
    private string? _pendingSection;
    private bool _sectionOpen;
    private bool _sectionHasContent;

    public NdjsonSink(Stream output)
    {
        // encoderShouldEmitUTF8Identifier: false — a BOM breaks `jq`, breaks a naive
        // `open(...).readline()`, and is not part of the framing the schema documents.
        _writer = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 64 * 1024, leaveOpen: true)
        {
            NewLine = "\n",
            AutoFlush = false,
        };
    }

    /// <summary>Writes a complete document on its own line. Used for the header line.</summary>
    public void WriteLine(JsonValue value)
    {
        _writer.Write(value.ToCompactString());
        _writer.Write('\n');
    }

    public void BeginRecord()
    {
        if (_recordOpen) throw new InvalidOperationException("A record is already open.");
        _recordOpen = true;
        _recordHasContent = false;
        _writer.Write('{');
    }

    public void EndRecord()
    {
        if (!_recordOpen) return;
        CloseSection();
        _writer.Write('}');
        _writer.Write('\n');
        _recordOpen = false;
    }

    public void BeginSection(string name)
    {
        CloseSection();
        _pendingSection = name;
    }

    public void EndSection() => CloseSection();

    public void Write(string key, JsonValue? value)
    {
        if (value is null) return;
        if (!_recordOpen) throw new InvalidOperationException("No record is open.");

        if (_pendingSection is not null)
        {
            // The section header is written on its FIRST value, so a section that turns out empty
            // never appears. There is no way to take it back once it is on the stream.
            WriteSeparator(_recordHasContent);
            WriteKey(_pendingSection);
            _writer.Write('{');
            _recordHasContent = true;
            _sectionOpen = true;
            _sectionHasContent = false;
            _pendingSection = null;
        }

        if (_sectionOpen)
        {
            WriteSeparator(_sectionHasContent);
            WriteKey(key);
            _writer.Write(value.ToCompactString());
            _sectionHasContent = true;
            return;
        }

        WriteSeparator(_recordHasContent);
        WriteKey(key);
        _writer.Write(value.ToCompactString());
        _recordHasContent = true;
    }

    public void Flush() => _writer.Flush();

    public void Dispose()
    {
        // A half-written record on the way out would produce an unparseable last line, which is
        // exactly what the "Ctrl+C leaves a valid file" promise rules out.
        if (_recordOpen) EndRecord();
        _writer.Flush();
        _writer.Dispose();
    }

    private void CloseSection()
    {
        if (_sectionOpen)
        {
            _writer.Write('}');
            _sectionOpen = false;
            _sectionHasContent = false;
        }

        _pendingSection = null;
    }

    private void WriteSeparator(bool needed)
    {
        if (needed) _writer.Write(',');
    }

    private void WriteKey(string key)
    {
        _scratch.Clear();
        JsonValue.String(key).Write(_scratch, 0, indented: false);
        _writer.Write(_scratch.ToString());
        _writer.Write(':');
    }
}
