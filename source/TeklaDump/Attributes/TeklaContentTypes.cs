using System;
using System.Collections.Generic;
using Tekla.Structures.Model;
using TeklaTask = Tekla.Structures.Model.Task;

namespace TeklaDump.Attributes;

/// <summary>
/// Maps a CLR object onto the content type name Tekla uses in <c>contentattributes*.lst</c>
/// (<c>PART</c>, <c>BOLT</c>, ...) — and, in the T2 report path, onto the <c>contenttype</c> of the
/// generated row.
/// </summary>
/// <remarks>
/// Lifted from TeklaLookup, minus the drawing types (v1 is model objects only, and a drawing dump
/// would be a separate document with its own domain).
/// </remarks>
internal static class TeklaContentTypes
{
    /// <summary>
    /// Most-derived first — the first assignable entry wins, so <c>SingleRebar</c> resolves to
    /// <c>SINGLE_REBAR</c> rather than falling through to its <c>Reinforcement</c> base.
    /// </summary>
    private static readonly KeyValuePair<Type, string>[] Map =
    {
        new KeyValuePair<Type, string>(typeof(SingleRebar), "SINGLE_REBAR"),
        new KeyValuePair<Type, string>(typeof(RebarStrand), "STRAND"),
        new KeyValuePair<Type, string>(typeof(RebarMesh), "MESH"),
        new KeyValuePair<Type, string>(typeof(RebarGroup), "REBAR"),
        new KeyValuePair<Type, string>(typeof(BaseRebarGroup), "REBAR"),
        new KeyValuePair<Type, string>(typeof(Reinforcement), "REBAR"),

        new KeyValuePair<Type, string>(typeof(BoltGroup), "BOLT"),
        new KeyValuePair<Type, string>(typeof(BaseWeld), "WELD"),
        new KeyValuePair<Type, string>(typeof(BaseComponent), "CONNECTION"),

        new KeyValuePair<Type, string>(typeof(PourObject), "POUR_OBJECT"),
        new KeyValuePair<Type, string>(typeof(PourBreak), "POUR_BREAK"),

        new KeyValuePair<Type, string>(typeof(Assembly), "ASSEMBLY"),
        new KeyValuePair<Type, string>(typeof(Part), "PART"),

        new KeyValuePair<Type, string>(typeof(ReferenceModelObject), "REFERENCE_OBJECT"),
        new KeyValuePair<Type, string>(typeof(ReferenceModel), "REFERENCE_MODEL"),
        new KeyValuePair<Type, string>(typeof(ProjectInfo), "PROJECT"),
        new KeyValuePair<Type, string>(typeof(TeklaTask), "TASK"),
    };

    /// <summary>
    /// The Tekla content type for <paramref name="target"/>, or null when the object has no
    /// template attributes at all.
    /// </summary>
    public static string? For(object? target)
    {
        if (target is null) return null;

        var type = target.GetType();
        foreach (var pair in Map)
        {
            if (pair.Key.IsAssignableFrom(type))
                return pair.Value;
        }

        return null;
    }
}
