using TeklaDump.Json;

namespace TeklaDump.Sinks;

/// <summary>
/// Where an extractor writes. The abstraction that lets ONE set of extractors serve both output
/// modes: <see cref="TreeSink"/> builds a document (inspect), <see cref="NdjsonSink"/> writes
/// straight to a stream (bulk). An extractor cannot tell which it has, which is the point — the
/// two modes cannot drift apart in what they emit.
/// </summary>
/// <remarks>
/// Sections are lazy: a section that receives no values is never opened, so an object with no
/// user properties has no <c>userProperties</c> key rather than an empty object. That is a
/// requirement rather than a nicety, because <see cref="NdjsonSink"/> cannot un-write a key it has
/// already pushed to the stream.
/// </remarks>
internal interface IDumpSink
{
    /// <summary>Starts one record.</summary>
    void BeginRecord();

    /// <summary>Ends the record started by <see cref="BeginRecord"/>.</summary>
    void EndRecord();

    /// <summary>
    /// Opens a named section (<c>create</c>, <c>derived</c>, <c>userProperties</c>, ...). Sections
    /// do not nest. Calling this while a section is open closes the previous one.
    /// </summary>
    void BeginSection(string name);

    /// <summary>Closes the current section, if any.</summary>
    void EndSection();

    /// <summary>
    /// Writes one key. A null <paramref name="value"/> is ignored — omission is how the schema
    /// expresses "no value", so every extractor can write unconditionally and let the coercion
    /// layer decide.
    /// </summary>
    void Write(string key, JsonValue? value);
}
