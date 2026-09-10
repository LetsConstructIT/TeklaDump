using System;
using System.Collections.Generic;
using TeklaDump.Json;

namespace TeklaDump.Sinks;

/// <summary>
/// Wraps a sink and records which keys were written in which section, so a test can assert that
/// everything an extractor emits was declared in its <c>CreateKeys</c> / <c>DerivedKeys</c>.
/// </summary>
/// <remarks>
/// This is the mechanism behind the schema promise. The declared key lists drive the generated
/// JSON Schema and the schema README table; if an extractor could quietly emit a key it never
/// declared, the schema would be a description of the code's intentions rather than of its output.
/// Test-only by intent, in the library by necessity — the extractors it validates are internal.
/// </remarks>
internal sealed class ValidatingSink : IDumpSink
{
    private readonly IDumpSink _inner;
    private readonly Dictionary<string, List<string>> _keysBySection =
        new Dictionary<string, List<string>>(StringComparer.Ordinal);

    private string _section = RootSection;

    /// <summary>Section name used for keys written outside any section (<c>objectType</c>, <c>guid</c>).</summary>
    public const string RootSection = "";

    public ValidatingSink(IDumpSink inner)
    {
        _inner = inner;
    }

    /// <summary>Keys written into <paramref name="section"/>, in write order.</summary>
    public IReadOnlyList<string> KeysIn(string section) =>
        _keysBySection.TryGetValue(section, out var keys) ? keys : new List<string>();

    public IReadOnlyCollection<string> Sections => _keysBySection.Keys;

    public void BeginRecord() => _inner.BeginRecord();

    public void EndRecord()
    {
        _section = RootSection;
        _inner.EndRecord();
    }

    public void BeginSection(string name)
    {
        _section = name;
        _inner.BeginSection(name);
    }

    public void EndSection()
    {
        _section = RootSection;
        _inner.EndSection();
    }

    public void Write(string key, JsonValue? value)
    {
        // Only keys that actually reach the output are recorded: a null value is an omission, and
        // asserting against keys that were never emitted would make the test fail on absence.
        if (value is not null)
        {
            if (!_keysBySection.TryGetValue(_section, out var keys))
                _keysBySection[_section] = keys = new List<string>();
            keys.Add(key);
        }

        _inner.Write(key, value);
    }
}
