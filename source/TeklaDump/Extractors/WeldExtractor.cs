using System;
using System.Collections.Generic;
using Tekla.Structures.Model;
using TeklaDump.Json;
using TeklaDump.Sinks;
using TeklaDump.Values;

namespace TeklaDump.Extractors;

/// <summary>
/// A weld: the two objects it joins, and the shop-drawing values that describe it.
/// </summary>
/// <remarks>
/// A weld binds to its parents by reference, which is the plainest example of why this format is
/// not a round-trip format: recreating a weld requires the main and secondary objects to exist
/// FIRST, and their GUIDs in this record name objects in the source model, not in whatever model
/// the generated code runs against.
/// <para>
/// The <c>Above</c>/<c>Below</c> pairs are the two sides of the weld symbol and are kept in full —
/// they are the actual content of a weld, and a "simplified" weld with only the above-line values
/// is a different weld.
/// </para>
/// </remarks>
internal class WeldExtractor : ExtractorBase
{
    public override Type ObjectType => typeof(BaseWeld);

    protected override ExtractorBase? Base => ModelObject;

    protected override IReadOnlyList<DumpKey> OwnCreate => new[]
    {
        new DumpKey("$ctor", DumpKeyType.String, "Constructor hint; an opaque string."),
        new DumpKey("MainObject", DumpKeyType.Reference, "The object welded to."),
        new DumpKey("SecondaryObject", DumpKeyType.Reference, "The object being welded."),
        new DumpKey("ShopWeld", DumpKeyType.Boolean, "True for a workshop weld, false for a site weld."),
        new DumpKey("ConnectAssemblies", DumpKeyType.Boolean, "Whether the weld joins the parts into one assembly."),
        new DumpKey("AroundWeld", DumpKeyType.Boolean, "Weld-all-around flag."),
        new DumpKey("TypeAbove", DumpKeyType.String, "WeldTypeEnum name, above the line."),
        new DumpKey("TypeBelow", DumpKeyType.String, "WeldTypeEnum name, below the line."),
        new DumpKey("SizeAbove", DumpKeyType.Number, "Weld size above the line."),
        new DumpKey("SizeBelow", DumpKeyType.Number, "Weld size below the line."),
        new DumpKey("LengthAbove", DumpKeyType.Number, "Weld length above the line."),
        new DumpKey("LengthBelow", DumpKeyType.Number, "Weld length below the line."),
        new DumpKey("PitchAbove", DumpKeyType.Number, "Pitch above the line, for an intermittent weld."),
        new DumpKey("PitchBelow", DumpKeyType.Number, "Pitch below the line."),
        new DumpKey("AngleAbove", DumpKeyType.Number, "Preparation angle above the line."),
        new DumpKey("AngleBelow", DumpKeyType.Number, "Preparation angle below the line."),
        new DumpKey("ContourAbove", DumpKeyType.String, "WeldContourEnum name, above the line."),
        new DumpKey("ContourBelow", DumpKeyType.String, "WeldContourEnum name, below the line."),
        new DumpKey("FinishAbove", DumpKeyType.String, "WeldFinishEnum name, above the line."),
        new DumpKey("FinishBelow", DumpKeyType.String, "WeldFinishEnum name, below the line."),
        new DumpKey("IntermittentType", DumpKeyType.String, "WeldIntermittentTypeEnum name."),
        new DumpKey("Placement", DumpKeyType.String, "WeldPlacementTypeEnum name."),
        new DumpKey("Preparation", DumpKeyType.String, "WeldPreparationTypeEnum name."),
        new DumpKey("ReferenceText", DumpKeyType.String, "Free text shown on the weld symbol."),
    };

    protected override IReadOnlyList<DumpKey> OwnDerived => new[]
    {
        new DumpKey("WeldNumber", DumpKeyType.Integer, "Assigned by numbering; empty until numbering has run."),
    };

    protected internal override void WriteCreate(ModelObject source, IDumpSink sink, DumpContext context)
    {
        var weld = (BaseWeld)source;

        sink.Write("$ctor", JsonValue.String("new " + source.GetType().Name + "()"));

        sink.Write("MainObject", context.Reference(context.Try(() => weld.MainObject)));
        sink.Write("SecondaryObject", context.Reference(context.Try(() => weld.SecondaryObject)));

        WriteBool(sink, "ShopWeld", context.TryValue(() => weld.ShopWeld));
        WriteBool(sink, "ConnectAssemblies", context.TryValue(() => weld.ConnectAssemblies));
        WriteBool(sink, "AroundWeld", context.TryValue(() => weld.AroundWeld));

        WriteEnum(sink, "TypeAbove", context.TryValue(() => weld.TypeAbove));
        WriteEnum(sink, "TypeBelow", context.TryValue(() => weld.TypeBelow));
        WriteNumber(sink, "SizeAbove", context.TryValue(() => weld.SizeAbove));
        WriteNumber(sink, "SizeBelow", context.TryValue(() => weld.SizeBelow));
        WriteNumber(sink, "LengthAbove", context.TryValue(() => weld.LengthAbove));
        WriteNumber(sink, "LengthBelow", context.TryValue(() => weld.LengthBelow));
        WriteNumber(sink, "PitchAbove", context.TryValue(() => weld.PitchAbove));
        WriteNumber(sink, "PitchBelow", context.TryValue(() => weld.PitchBelow));
        WriteNumber(sink, "AngleAbove", context.TryValue(() => weld.AngleAbove));
        WriteNumber(sink, "AngleBelow", context.TryValue(() => weld.AngleBelow));
        WriteEnum(sink, "ContourAbove", context.TryValue(() => weld.ContourAbove));
        WriteEnum(sink, "ContourBelow", context.TryValue(() => weld.ContourBelow));
        WriteEnum(sink, "FinishAbove", context.TryValue(() => weld.FinishAbove));
        WriteEnum(sink, "FinishBelow", context.TryValue(() => weld.FinishBelow));
        WriteEnum(sink, "IntermittentType", context.TryValue(() => weld.IntermittentType));
        WriteEnum(sink, "Placement", context.TryValue(() => weld.Placement));
        WriteEnum(sink, "Preparation", context.TryValue(() => weld.Preparation));
        sink.Write("ReferenceText", ValueCoercion.Text(context.Try(() => weld.ReferenceText)));

        Base?.WriteCreate(source, sink, context);
    }

    protected internal override void WriteDerived(ModelObject source, IDumpSink sink, DumpContext context)
    {
        var weld = (BaseWeld)source;

        var number = context.TryValue(() => weld.WeldNumber);
        if (number.HasValue) sink.Write("WeldNumber", JsonValue.Number(number.Value));

        Base?.WriteDerived(source, sink, context);
    }

    private protected static void WriteBool(IDumpSink sink, string key, bool? value)
    {
        if (value.HasValue) sink.Write(key, JsonValue.Bool(value.Value));
    }

    private protected static void WriteNumber(IDumpSink sink, string key, double? value)
    {
        if (value.HasValue) sink.Write(key, JsonValue.Number(value.Value));
    }

    private protected static void WriteEnum<T>(IDumpSink sink, string key, T? value) where T : struct, Enum
    {
        if (value.HasValue) sink.Write(key, JsonValue.String(value.Value.ToString()));
    }

    private static readonly ModelObjectExtractor ModelObject = new ModelObjectExtractor();
}

/// <summary>A polygon weld — a weld laid along a polyline rather than between two solids.</summary>
internal sealed class PolygonWeldExtractor : ExtractorBase
{
    public override Type ObjectType => typeof(PolygonWeld);

    protected override ExtractorBase? Base => Weld;

    protected override IReadOnlyList<DumpKey> OwnCreate => new[]
    {
        new DumpKey("Polygon", DumpKeyType.PointArray, "The polyline the weld follows."),
    };

    protected override IReadOnlyList<DumpKey> OwnDerived => new DumpKey[0];

    protected internal override void WriteCreate(ModelObject source, IDumpSink sink, DumpContext context)
    {
        var weld = (PolygonWeld)source;

        var polygon = context.Try(() => weld.Polygon);
        sink.Write("Polygon", polygon is null ? null : ValueCoercion.Points(polygon.Points));

        Base?.WriteCreate(source, sink, context);
    }

    protected internal override void WriteDerived(ModelObject source, IDumpSink sink, DumpContext context)
    {
        Base?.WriteDerived(source, sink, context);
    }

    private static readonly WeldExtractor Weld = new WeldExtractor();
}
