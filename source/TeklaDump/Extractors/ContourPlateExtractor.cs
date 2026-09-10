using System;
using System.Collections.Generic;
using Tekla.Structures.Model;
using TeklaDump.Json;
using TeklaDump.Sinks;
using TeklaDump.Values;

namespace TeklaDump.Extractors;

/// <summary>
/// A contour plate: a chamfered polygon and everything a part has.
/// </summary>
/// <remarks>
/// The contour points carry their chamfers, which is the detail a screenshot loses and a
/// regenerated plate needs. What the record does NOT carry is the plate's cuts — a plate with a
/// <c>Fitting</c> or a <c>BooleanPart</c> cannot be fully regenerated from v1 output, and the
/// README says so rather than letting someone discover it. Cuts and booleans are v1.1.
/// </remarks>
internal sealed class ContourPlateExtractor : ExtractorBase
{
    public override Type ObjectType => typeof(ContourPlate);

    protected override ExtractorBase? Base => Part;

    protected override IReadOnlyList<DumpKey> OwnCreate => new[]
    {
        new DumpKey("$ctor", DumpKeyType.String, "Constructor hint; an opaque string."),
        new DumpKey("Contour", DumpKeyType.PointArray,
            "Contour points in order, each with its Chamfer when it has one."),
    };

    protected override IReadOnlyList<DumpKey> OwnDerived => new[]
    {
        new DumpKey("Type", DumpKeyType.String,
            "ContourPlateTypeEnum name. DERIVED: it is decided by the material (concrete gives a slab, anything else a plate), not assigned."),
    };

    protected internal override void WriteCreate(ModelObject source, IDumpSink sink, DumpContext context)
    {
        var plate = (ContourPlate)source;

        sink.Write("$ctor", JsonValue.String("new ContourPlate()"));

        var contour = context.Try(() => plate.Contour);
        sink.Write("Contour", contour is null ? null : ValueCoercion.Points(contour.ContourPoints));

        Base?.WriteCreate(source, sink, context);
    }

    protected internal override void WriteDerived(ModelObject source, IDumpSink sink, DumpContext context)
    {
        var plate = (ContourPlate)source;

        var type = context.TryValue(() => plate.Type);
        if (type.HasValue) sink.Write("Type", ValueCoercion.Enum(type.Value));

        Base?.WriteDerived(source, sink, context);
    }

    private static readonly PartExtractor Part = new PartExtractor();
}
