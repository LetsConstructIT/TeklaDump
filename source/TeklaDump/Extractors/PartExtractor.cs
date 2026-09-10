using System;
using System.Collections.Generic;
using Tekla.Structures.Model;
using TeklaDump.Json;
using TeklaDump.Sinks;
using TeklaDump.Values;

namespace TeklaDump.Extractors;

/// <summary>
/// The properties every part has, whatever its shape: what it is made of, what it is called, and
/// how it is positioned against its own reference line.
/// </summary>
/// <remarks>
/// This allowlist is CUT from TeklaLookup's full member list rather than grown from nothing. The
/// members that are absent are absent on purpose: <c>GetSolid</c>, <c>GetBolts</c>,
/// <c>GetWelds</c>, <c>GetReinforcements</c>, <c>GetBooleans</c> and <c>GetComponents</c> either
/// compute geometry or walk the object graph, and the danger list in the schema README says why
/// none of them runs by default.
/// </remarks>
internal class PartExtractor : ExtractorBase
{
    public override Type ObjectType => typeof(Part);

    protected override ExtractorBase? Base => ModelObject;

    protected override IReadOnlyList<DumpKey> OwnCreate => new[]
    {
        new DumpKey("Profile", DumpKeyType.String, "Profile catalog string, e.g. HEA300."),
        new DumpKey("Material", DumpKeyType.String, "Material catalog string, e.g. S355J2."),
        new DumpKey("Class", DumpKeyType.String, "Class. A string in the API even though it looks like a number."),
        new DumpKey("Name", DumpKeyType.String, "Part name."),
        new DumpKey("Finish", DumpKeyType.String, "Finish, e.g. a paint or galvanising code."),
        new DumpKey("CastUnitType", DumpKeyType.String, "PartCastUnitTypeEnum name."),
        new DumpKey("Position", DumpKeyType.Object,
            "Depth / Plane / Rotation enum names and their three offsets. The part of a part that is hardest to reproduce from a screenshot."),
        new DumpKey("PartNumber", DumpKeyType.Object, "Numbering series: Prefix and StartNumber."),
        new DumpKey("AssemblyNumber", DumpKeyType.Object, "Assembly numbering series: Prefix and StartNumber."),
    };

    protected override IReadOnlyList<DumpKey> OwnDerived => new[]
    {
        new DumpKey("Assembly", DumpKeyType.Reference, "The assembly this part belongs to."),
        new DumpKey("Solid", DumpKeyType.Object,
            "Bounding box of the computed solid. Only when Geometry = Solids — computing it is orders of magnitude more expensive than every other field combined."),
    };

    protected internal override void WriteCreate(ModelObject source, IDumpSink sink, DumpContext context)
    {
        var part = (Part)source;

        sink.Write("Profile", ValueCoercion.Profile(context.Try(() => part.Profile)));
        sink.Write("Material", ValueCoercion.Material(context.Try(() => part.Material)));
        sink.Write("Class", ValueCoercion.Text(context.Try(() => part.Class)));
        sink.Write("Name", ValueCoercion.Text(context.Try(() => part.Name)));
        sink.Write("Finish", ValueCoercion.Text(context.Try(() => part.Finish)));

        var castUnitType = context.TryValue(() => part.CastUnitType);
        if (castUnitType.HasValue) sink.Write("CastUnitType", ValueCoercion.Enum(castUnitType.Value));

        sink.Write("Position", PositionValue(context.Try(() => part.Position)));
        sink.Write("PartNumber", NumberingSeriesValue(context.Try(() => part.PartNumber)));
        sink.Write("AssemblyNumber", NumberingSeriesValue(context.Try(() => part.AssemblyNumber)));

        Base?.WriteCreate(source, sink, context);
    }

    protected internal override void WriteDerived(ModelObject source, IDumpSink sink, DumpContext context)
    {
        var part = (Part)source;

        sink.Write("Assembly", context.Reference(context.Try(() => part.GetAssembly())));

        if (context.Options.Geometry == GeometryDetail.Solids)
            sink.Write("Solid", SolidValue(context.Try(() => part.GetSolid())));

        Base?.WriteDerived(source, sink, context);
    }

    /// <summary>
    /// The position block. All six members are emitted together even when they are at their
    /// defaults: position is the one thing a reader cannot infer from the geometry, and a partial
    /// block would read as "the rest is default" when it means "the rest was not read".
    /// </summary>
    internal static JsonValue? PositionValue(Position? position)
    {
        if (position is null) return null;

        var value = new JsonObject();
        value.Add("Depth", JsonValue.String(position.Depth.ToString()));
        value.Add("Plane", JsonValue.String(position.Plane.ToString()));
        value.Add("Rotation", JsonValue.String(position.Rotation.ToString()));
        value.Add("DepthOffset", JsonValue.Number(position.DepthOffset));
        value.Add("PlaneOffset", JsonValue.Number(position.PlaneOffset));
        value.Add("RotationOffset", JsonValue.Number(position.RotationOffset));
        return value;
    }

    internal static JsonValue? NumberingSeriesValue(NumberingSeries? series)
    {
        if (series is null) return null;

        var value = new JsonObject();
        value.AddIfPresent("Prefix", ValueCoercion.Text(series.Prefix));
        value.Add("StartNumber", JsonValue.Number(series.StartNumber));
        return value;
    }

    /// <summary>
    /// The solid as its bounding box. Not the faces: a tessellated solid is megabytes per part and
    /// is not something a create block can use, while the box answers "how big is it" — which is
    /// what a reader actually asks.
    /// </summary>
    internal static JsonValue? SolidValue(Solid? solid)
    {
        if (solid is null) return null;

        var value = new JsonObject();
        value.AddIfPresent("MinimumPoint", ValueCoercion.Point(solid.MinimumPoint));
        value.AddIfPresent("MaximumPoint", ValueCoercion.Point(solid.MaximumPoint));
        return value.Count == 0 ? null : value;
    }

    /// <summary>Shared instance: the extractors form a chain, not a tree of copies.</summary>
    protected static readonly ModelObjectExtractor ModelObject = new ModelObjectExtractor();
}
