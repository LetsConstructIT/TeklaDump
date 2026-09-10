using System;
using System.Collections.Generic;
using Tekla.Structures.Model;
using TeklaDump.Json;
using TeklaDump.Sinks;
using TeklaDump.Values;

namespace TeklaDump.Extractors;

/// <summary>
/// A beam: two points and everything a part has.
/// </summary>
/// <remarks>
/// <c>Beam.Type</c> is READ-ONLY after construction — a beam, a column and a pad footing are the
/// same class distinguished by a constructor argument — which is precisely why the create block
/// leads with a <c>$ctor</c> hint instead of listing <c>Type</c> as an assignable property. A
/// generated script that assigns <c>Type</c> does not compile, and that is the class of bug the
/// create/derived split exists to prevent.
/// </remarks>
internal sealed class BeamExtractor : ExtractorBase
{
    public override Type ObjectType => typeof(Beam);

    protected override ExtractorBase? Base => Part;

    protected override IReadOnlyList<DumpKey> OwnCreate => new[]
    {
        new DumpKey("$ctor", DumpKeyType.String,
            "Constructor hint. An opaque string for a human or an LLM to paste — no tool should parse it."),
        new DumpKey("StartPoint", DumpKeyType.Point, "Start of the reference line, in the current work plane."),
        new DumpKey("EndPoint", DumpKeyType.Point, "End of the reference line, in the current work plane."),
        new DumpKey("StartPointOffset", DumpKeyType.Point, "Dx/Dy/Dz offset applied at the start point."),
        new DumpKey("EndPointOffset", DumpKeyType.Point, "Dx/Dy/Dz offset applied at the end point."),
    };

    protected override IReadOnlyList<DumpKey> OwnDerived => new[]
    {
        new DumpKey("Type", DumpKeyType.String,
            "BeamTypeEnum name. DERIVED because it is read-only after construction — it is set by the constructor in $ctor."),
    };

    protected internal override void WriteCreate(ModelObject source, IDumpSink sink, DumpContext context)
    {
        var beam = (Beam)source;

        var type = context.TryValue(() => beam.Type);
        sink.Write("$ctor", JsonValue.String(
            type.HasValue
                ? "new Beam(Beam.BeamTypeEnum." + type.Value + ")"
                : "new Beam()"));

        sink.Write("StartPoint", ValueCoercion.Point(context.Try(() => beam.StartPoint)));
        sink.Write("EndPoint", ValueCoercion.Point(context.Try(() => beam.EndPoint)));
        sink.Write("StartPointOffset", OffsetValue(context.Try(() => beam.StartPointOffset)));
        sink.Write("EndPointOffset", OffsetValue(context.Try(() => beam.EndPointOffset)));

        Base?.WriteCreate(source, sink, context);
    }

    protected internal override void WriteDerived(ModelObject source, IDumpSink sink, DumpContext context)
    {
        var beam = (Beam)source;

        var type = context.TryValue(() => beam.Type);
        if (type.HasValue) sink.Write("Type", ValueCoercion.Enum(type.Value));

        Base?.WriteDerived(source, sink, context);
    }

    /// <summary>
    /// An offset, omitted when it is zero. A zero offset is the default; emitting it would put
    /// three lines of noise on every beam in the model and invite a script to assign it.
    /// </summary>
    internal static JsonValue? OffsetValue(Offset? offset)
    {
        if (offset is null) return null;
        if (offset.Dx == 0 && offset.Dy == 0 && offset.Dz == 0) return null;
        return ValueCoercion.Point3(offset.Dx, offset.Dy, offset.Dz);
    }

    private static readonly PartExtractor Part = new PartExtractor();
}
