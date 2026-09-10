using System;
using System.Collections.Generic;
using Tekla.Structures.Model;
using TeklaDump.Json;
using TeklaDump.Sinks;
using TeklaDump.Values;

namespace TeklaDump.Extractors;

/// <summary>
/// A polybeam: a chamfered contour used as a centre line, plus everything a part has.
/// </summary>
internal sealed class PolyBeamExtractor : ExtractorBase
{
    public override Type ObjectType => typeof(PolyBeam);

    protected override ExtractorBase? Base => Part;

    protected override IReadOnlyList<DumpKey> OwnCreate => new[]
    {
        new DumpKey("$ctor", DumpKeyType.String, "Constructor hint; an opaque string."),
        new DumpKey("Contour", DumpKeyType.PointArray,
            "Centre-line contour points in order. A chamfer here is a bend, not a corner cut."),
    };

    protected override IReadOnlyList<DumpKey> OwnDerived => new[]
    {
        new DumpKey("Type", DumpKeyType.String, "BeamTypeEnum name; read-only after construction."),
    };

    protected internal override void WriteCreate(ModelObject source, IDumpSink sink, DumpContext context)
    {
        var beam = (PolyBeam)source;

        sink.Write("$ctor", JsonValue.String("new PolyBeam()"));

        var contour = context.Try(() => beam.Contour);
        sink.Write("Contour", contour is null ? null : ValueCoercion.Points(contour.ContourPoints));

        Base?.WriteCreate(source, sink, context);
    }

    protected internal override void WriteDerived(ModelObject source, IDumpSink sink, DumpContext context)
    {
        var beam = (PolyBeam)source;

        var type = context.TryValue(() => beam.Type);
        if (type.HasValue) sink.Write("Type", ValueCoercion.Enum(type.Value));

        Base?.WriteDerived(source, sink, context);
    }

    private static readonly PartExtractor Part = new PartExtractor();
}
