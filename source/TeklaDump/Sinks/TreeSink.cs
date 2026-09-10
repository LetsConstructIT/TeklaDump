using System;
using TeklaDump.Json;

namespace TeklaDump.Sinks;

/// <summary>
/// Builds a <see cref="JsonArray"/> of records in memory. Inspect mode, where the document is
/// small by definition and the caller wants a value back rather than a stream.
/// </summary>
internal sealed class TreeSink : IDumpSink
{
    private readonly JsonArray _records = new JsonArray();

    private JsonObject? _record;
    private JsonObject? _section;
    private string? _sectionName;

    /// <summary>The records written so far. Safe to read only between records.</summary>
    public JsonArray Records => _records;

    public void BeginRecord()
    {
        if (_record is not null) throw new InvalidOperationException("A record is already open.");
        _record = new JsonObject();
    }

    public void EndRecord()
    {
        if (_record is null) return;
        EndSection();
        _records.Add(_record);
        _record = null;
    }

    /// <summary>
    /// Discards the open record. Used when an extractor throws part-way: a half-written record is
    /// worse than a skip, because it looks complete.
    /// </summary>
    public void AbortRecord()
    {
        _section = null;
        _sectionName = null;
        _record = null;
    }

    public void BeginSection(string name)
    {
        EndSection();
        _sectionName = name;
        _section = null; // created lazily on the first value
    }

    public void EndSection()
    {
        if (_section is not null && _sectionName is not null && _record is not null)
            _record.Add(_sectionName, _section);

        _section = null;
        _sectionName = null;
    }

    public void Write(string key, JsonValue? value)
    {
        if (value is null) return;
        if (_record is null) throw new InvalidOperationException("No record is open.");

        if (_sectionName is null)
        {
            _record.Add(key, value);
            return;
        }

        _section ??= new JsonObject();
        _section.Add(key, value);
    }
}
