using System;
using System.Collections;
using System.Collections.Generic;
using Tekla.Structures.Model;

namespace TeklaDump.Attributes;

/// <summary>
/// Reads an object's user-defined attributes (UDAs).
/// </summary>
/// <remarks>
/// <c>GetAllUserProperties</c> needs no <c>objects.inp</c> declaration and returns everything
/// actually set on the object, in one interop call — which is why UDAs are on by default while
/// template attributes are not.
/// <para>
/// The classic trap, documented in the README so nobody blames the dumper: a UDA WRITTEN with a
/// mismatched type reads back wrong. Tekla stores the value under the type it was written with,
/// and a string written into an integer field comes back as garbage or not at all. What appears
/// here is what Tekla returned.
/// </para>
/// </remarks>
internal static class UserPropertyReader
{
    /// <summary>
    /// Name to value, sorted ordinally by name — determinism, so two runs diff clean (the
    /// <c>Hashtable</c>'s own order is not stable across processes).
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, object?>> Read(ModelObject target, out string? error)
    {
        error = null;
        var table = new Hashtable();
        try
        {
            target.GetAllUserProperties(ref table);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return Empty;
        }

        if (table is null || table.Count == 0) return Empty;

        var names = new List<string>(table.Count);
        foreach (DictionaryEntry entry in table)
        {
            var name = entry.Key?.ToString();
            if (!string.IsNullOrEmpty(name)) names.Add(name!);
        }

        names.Sort(StringComparer.Ordinal);

        var results = new List<KeyValuePair<string, object?>>(names.Count);
        foreach (var name in names)
            results.Add(new KeyValuePair<string, object?>(name, table[name]));

        return results;
    }

    private static readonly KeyValuePair<string, object?>[] Empty = new KeyValuePair<string, object?>[0];
}
