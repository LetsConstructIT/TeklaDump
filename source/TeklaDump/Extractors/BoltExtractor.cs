using System;
using System.Collections.Generic;
using Tekla.Structures.Model;
using TeklaDump.Json;
using TeklaDump.Sinks;
using TeklaDump.Values;

namespace TeklaDump.Extractors;

/// <summary>
/// A bolt group: which parts it joins, where the bolts sit, and what they are.
/// </summary>
/// <remarks>
/// The parts a bolt group connects are emitted as REFERENCES, not as nested part records. A bolt
/// dump that inlined its parts would repeat every part of the model once per bolt through it, and
/// the reader already has those parts by GUID if they were in the same dump.
/// <para>
/// Cut from the full 40-property surface: the five <c>Hole</c> members, the two <c>Nut</c>/three
/// <c>Washer</c> flags and the slot geometry are all real, all settable, and all noise for the
/// question "how do I recreate this bolt group" — they are reachable through the template
/// attributes when someone actually needs them.
/// </para>
/// </remarks>
internal sealed class BoltExtractor : ExtractorBase
{
    public override Type ObjectType => typeof(BoltGroup);

    protected override ExtractorBase? Base => ModelObject;

    protected override IReadOnlyList<DumpKey> OwnCreate => new[]
    {
        new DumpKey("$ctor", DumpKeyType.String, "Constructor hint; an opaque string."),
        new DumpKey("PartToBeBolted", DumpKeyType.Reference, "The part the group is bolted to first."),
        new DumpKey("PartToBoltTo", DumpKeyType.Reference, "The part being bolted onto."),
        new DumpKey("OtherPartsToBolt", DumpKeyType.ReferenceArray, "Any further parts in the grip."),
        new DumpKey("FirstPosition", DumpKeyType.Point, "First positioning point of the group."),
        new DumpKey("SecondPosition", DumpKeyType.Point, "Second positioning point; with the first it sets the group's direction."),
        new DumpKey("BoltPositions", DumpKeyType.PointArray, "Positions of the individual bolts within the group."),
        new DumpKey("BoltSize", DumpKeyType.Number, "Nominal bolt size."),
        new DumpKey("BoltStandard", DumpKeyType.String, "Bolt assembly catalog name, e.g. 8.8X."),
        new DumpKey("BoltType", DumpKeyType.String, "BoltTypeEnum name: site or workshop."),
        new DumpKey("Length", DumpKeyType.Number, "Bolt length."),
        new DumpKey("ExtraLength", DumpKeyType.Number, "Extra length added past the grip."),
        new DumpKey("CutLength", DumpKeyType.Number, "Cut length."),
        new DumpKey("Position", DumpKeyType.Object, "Depth / Plane / Rotation and their offsets, as on a part."),
        new DumpKey("ConnectAssemblies", DumpKeyType.Boolean, "Whether the group connects the parts into one assembly."),
        new DumpKey("Tolerance", DumpKeyType.Number, "Hole tolerance."),
        new DumpKey("HoleType", DumpKeyType.String, "BoltHoleTypeEnum name."),
    };

    protected override IReadOnlyList<DumpKey> OwnDerived => NoKeysStatic;

    protected internal override void WriteCreate(ModelObject source, IDumpSink sink, DumpContext context)
    {
        var bolts = (BoltGroup)source;

        // The concrete subclass matters: BoltArray, BoltCircle and BoltXYList lay the same group
        // out differently, and the shape is the constructor, not a property.
        sink.Write("$ctor", JsonValue.String("new " + source.GetType().Name + "()"));

        sink.Write("PartToBeBolted", context.Reference(context.Try(() => bolts.PartToBeBolted)));
        sink.Write("PartToBoltTo", context.Reference(context.Try(() => bolts.PartToBoltTo)));
        sink.Write("OtherPartsToBolt", context.References(context.Try(() => bolts.OtherPartsToBolt)));

        sink.Write("FirstPosition", ValueCoercion.Point(context.Try(() => bolts.FirstPosition)));
        sink.Write("SecondPosition", ValueCoercion.Point(context.Try(() => bolts.SecondPosition)));
        sink.Write("BoltPositions", ValueCoercion.Points(context.Try(() => bolts.BoltPositions)));

        WriteNumber(sink, "BoltSize", context.TryValue(() => bolts.BoltSize));
        sink.Write("BoltStandard", ValueCoercion.Text(context.Try(() => bolts.BoltStandard)));

        var boltType = context.TryValue(() => bolts.BoltType);
        if (boltType.HasValue) sink.Write("BoltType", ValueCoercion.Enum(boltType.Value));

        WriteNumber(sink, "Length", context.TryValue(() => bolts.Length));
        WriteNumber(sink, "ExtraLength", context.TryValue(() => bolts.ExtraLength));
        WriteNumber(sink, "CutLength", context.TryValue(() => bolts.CutLength));

        sink.Write("Position", PartExtractor.PositionValue(context.Try(() => bolts.Position)));

        var connectAssemblies = context.TryValue(() => bolts.ConnectAssemblies);
        if (connectAssemblies.HasValue) sink.Write("ConnectAssemblies", JsonValue.Bool(connectAssemblies.Value));

        WriteNumber(sink, "Tolerance", context.TryValue(() => bolts.Tolerance));

        var holeType = context.TryValue(() => bolts.HoleType);
        if (holeType.HasValue) sink.Write("HoleType", ValueCoercion.Enum(holeType.Value));

        Base?.WriteCreate(source, sink, context);
    }

    protected internal override void WriteDerived(ModelObject source, IDumpSink sink, DumpContext context)
    {
        Base?.WriteDerived(source, sink, context);
    }

    private static void WriteNumber(IDumpSink sink, string key, double? value)
    {
        if (value.HasValue) sink.Write(key, JsonValue.Number(value.Value));
    }

    private static readonly ModelObjectExtractor ModelObject = new ModelObjectExtractor();
    private static readonly DumpKey[] NoKeysStatic = new DumpKey[0];
}
