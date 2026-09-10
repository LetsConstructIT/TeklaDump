using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using Tekla.Structures.Model;
using TeklaDump.Interop;
using TeklaDump.Json;
using TeklaDump.Sinks;
using TeklaDump.Values;

namespace TeklaDump.Extractors;

/// <summary>
/// What every reinforcement has: the object it is placed in, the bar itself, and the offsets that
/// decide where inside the concrete it sits.
/// </summary>
/// <remarks>
/// <c>Father</c> is a create key here, which is the exception to the "references are derived"
/// habit and a deliberate one: a rebar cannot be inserted without a father, so a create block
/// without it is not a create block. It is still emitted as a reference and still never followed.
/// </remarks>
internal abstract class ReinforcementExtractorBase : ExtractorBase
{
    protected override ExtractorBase? Base => ModelObject;

    protected override IReadOnlyList<DumpKey> OwnDerived => new[]
    {
        new DumpKey("NumberOfRebars", DumpKeyType.Integer, "How many bars the group actually produced."),
        new DumpKey("Assembly", DumpKeyType.Reference, "The rebar assembly, on Tekla 2022 and newer.", since: "2022"),
    };

    /// <summary>The <c>Reinforcement</c>-level create keys, shared by every rebar extractor.</summary>
    protected static IReadOnlyList<DumpKey> ReinforcementCreateKeys => new[]
    {
        new DumpKey("Father", DumpKeyType.Reference, "The part or pour the reinforcement is placed in. Required to insert."),
        new DumpKey("Name", DumpKeyType.String, "Reinforcement name."),
        new DumpKey("Grade", DumpKeyType.String, "Steel grade, e.g. B500B."),
        new DumpKey("Class", DumpKeyType.Integer, "Class. An int here, unlike a part's string Class."),
        new DumpKey("NumberingSeries", DumpKeyType.Object, "Numbering series: Prefix and StartNumber."),
        new DumpKey("FromPlaneOffset", DumpKeyType.Number, "Offset from the placing plane."),
        new DumpKey("StartPointOffsetType", DumpKeyType.String, "RebarOffsetTypeEnum name."),
        new DumpKey("StartPointOffsetValue", DumpKeyType.Number, "Offset applied at the start point."),
        new DumpKey("EndPointOffsetType", DumpKeyType.String, "RebarOffsetTypeEnum name."),
        new DumpKey("EndPointOffsetValue", DumpKeyType.Number, "Offset applied at the end point."),
        new DumpKey("OnPlaneOffsets", DumpKeyType.Array, "Offsets in the placing plane, one per leg."),
        new DumpKey("RadiusValues", DumpKeyType.Array, "Bending radii, one per bend."),
    };

    protected static void WriteReinforcementCreate(Reinforcement rebar, IDumpSink sink, DumpContext context)
    {
        sink.Write("Father", context.Reference(context.Try(() => rebar.Father)));
        sink.Write("Name", ValueCoercion.Text(context.Try(() => rebar.Name)));
        sink.Write("Grade", ValueCoercion.Text(context.Try(() => rebar.Grade)));

        var rebarClass = context.TryValue(() => rebar.Class);
        if (rebarClass.HasValue) sink.Write("Class", JsonValue.Number(rebarClass.Value));

        sink.Write("NumberingSeries", PartExtractor.NumberingSeriesValue(context.Try(() => rebar.NumberingSeries)));

        WriteNumber(sink, "FromPlaneOffset", context.TryValue(() => rebar.FromPlaneOffset));
        WriteEnum(sink, "StartPointOffsetType", context.TryValue(() => rebar.StartPointOffsetType));
        WriteNumber(sink, "StartPointOffsetValue", context.TryValue(() => rebar.StartPointOffsetValue));
        WriteEnum(sink, "EndPointOffsetType", context.TryValue(() => rebar.EndPointOffsetType));
        WriteNumber(sink, "EndPointOffsetValue", context.TryValue(() => rebar.EndPointOffsetValue));

        sink.Write("OnPlaneOffsets", Numbers(context.Try(() => rebar.OnPlaneOffsets)));
        sink.Write("RadiusValues", Numbers(context.Try(() => rebar.RadiusValues)));
    }

    protected static void WriteReinforcementDerived(Reinforcement rebar, IDumpSink sink, DumpContext context)
    {
        var count = context.TryValue(() => rebar.GetNumberOfRebars());
        if (count.HasValue) sink.Write("NumberOfRebars", JsonValue.Number(count.Value));

        // Reinforcement.GetAssembly arrived in Tekla 2022. Reflected so the 2021-floor binary still
        // emits it on a newer Tekla and simply omits it on 2021.
        var assembly = OptionalTeklaApi.Invoke(GetAssemblyMethod, rebar) as ModelObject;
        sink.Write("Assembly", context.Reference(assembly));
    }

    /// <summary>A hook as its four settable values; omitted entirely when there is no hook.</summary>
    protected static JsonValue? HookValue(RebarHookData? hook)
    {
        if (hook is null) return null;

        var value = new JsonObject();
        value.Add("Shape", JsonValue.String(hook.Shape.ToString()));
        value.Add("Angle", JsonValue.Number(hook.Angle));
        value.Add("Radius", JsonValue.Number(hook.Radius));
        value.Add("Length", JsonValue.Number(hook.Length));
        return value;
    }

    /// <summary>An <c>ArrayList</c> of numbers, as the spacing and offset APIs return them.</summary>
    protected static JsonValue? Numbers(IEnumerable? values)
    {
        if (values is null) return null;

        var array = new JsonArray();
        foreach (var item in values)
        {
            if (item is null) continue;
            try { array.Add(JsonValue.Number(Convert.ToDouble(item, CultureInfo.InvariantCulture))); }
            catch (Exception) { /* not a number after all — skip it rather than guess */ }
        }

        return array.Count == 0 ? null : array;
    }

    protected static void WriteNumber(IDumpSink sink, string key, double? value)
    {
        if (value.HasValue) sink.Write(key, JsonValue.Number(value.Value));
    }

    protected static void WriteEnum<T>(IDumpSink sink, string key, T? value) where T : struct, Enum
    {
        if (value.HasValue) sink.Write(key, JsonValue.String(value.Value.ToString()));
    }

    protected static readonly ModelObjectExtractor ModelObject = new ModelObjectExtractor();

    private static readonly System.Reflection.MethodInfo? GetAssemblyMethod =
        OptionalTeklaApi.Method(typeof(Reinforcement), "GetAssembly");
}

/// <summary>A rebar group: bars laid between two points at a spacing.</summary>
internal sealed class RebarGroupExtractor : ReinforcementExtractorBase
{
    public override Type ObjectType => typeof(RebarGroup);

    protected override IReadOnlyList<DumpKey> OwnCreate
    {
        get
        {
            var keys = new List<DumpKey>
            {
                new DumpKey("$ctor", DumpKeyType.String, "Constructor hint; an opaque string."),
                new DumpKey("StartPoint", DumpKeyType.Point, "Start of the distribution line."),
                new DumpKey("EndPoint", DumpKeyType.Point, "End of the distribution line."),
                new DumpKey("Polygons", DumpKeyType.Array, "The bar shapes, each an array of points."),
                new DumpKey("Size", DumpKeyType.String, "Bar size, a catalog string such as 12."),
                new DumpKey("Spacings", DumpKeyType.Array, "Spacing values; one value means an even spacing."),
                new DumpKey("SpacingType", DumpKeyType.String, "RebarGroupSpacingTypeEnum name."),
                new DumpKey("ExcludeType", DumpKeyType.String, "ExcludeTypeEnum name: which end bars are left out."),
                new DumpKey("StirrupType", DumpKeyType.String, "StirrupTypeEnum name."),
                new DumpKey("StartHook", DumpKeyType.Object, "Shape, Angle, Radius, Length."),
                new DumpKey("EndHook", DumpKeyType.Object, "Shape, Angle, Radius, Length."),
                new DumpKey("StartFromPlaneOffset", DumpKeyType.Number, "Plane offset at the start of the group."),
                new DumpKey("EndFromPlaneOffset", DumpKeyType.Number, "Plane offset at the end of the group."),
            };
            keys.AddRange(ReinforcementCreateKeys);
            return keys;
        }
    }

    protected internal override void WriteCreate(ModelObject source, IDumpSink sink, DumpContext context)
    {
        var group = (RebarGroup)source;

        sink.Write("$ctor", JsonValue.String("new RebarGroup()"));
        sink.Write("StartPoint", ValueCoercion.Point(context.Try(() => group.StartPoint)));
        sink.Write("EndPoint", ValueCoercion.Point(context.Try(() => group.EndPoint)));
        sink.Write("Polygons", PolygonsValue(context.Try(() => group.Polygons)));
        sink.Write("Size", ValueCoercion.Text(context.Try(() => group.Size)));
        sink.Write("Spacings", Numbers(context.Try(() => group.Spacings)));
        WriteEnum(sink, "SpacingType", context.TryValue(() => group.SpacingType));
        WriteEnum(sink, "ExcludeType", context.TryValue(() => group.ExcludeType));
        WriteEnum(sink, "StirrupType", context.TryValue(() => group.StirrupType));
        sink.Write("StartHook", HookValue(context.Try(() => group.StartHook)));
        sink.Write("EndHook", HookValue(context.Try(() => group.EndHook)));
        WriteNumber(sink, "StartFromPlaneOffset", context.TryValue(() => group.StartFromPlaneOffset));
        WriteNumber(sink, "EndFromPlaneOffset", context.TryValue(() => group.EndFromPlaneOffset));

        WriteReinforcementCreate(group, sink, context);
        Base?.WriteCreate(source, sink, context);
    }

    protected internal override void WriteDerived(ModelObject source, IDumpSink sink, DumpContext context)
    {
        WriteReinforcementDerived((RebarGroup)source, sink, context);
        Base?.WriteDerived(source, sink, context);
    }

    /// <summary>An <c>ArrayList</c> of <c>Polygon</c>, which is how a group states its bar shapes.</summary>
    private static JsonValue? PolygonsValue(IEnumerable? polygons)
    {
        if (polygons is null) return null;

        var array = new JsonArray();
        foreach (var item in polygons)
        {
            if (item is not Polygon polygon) continue;
            var points = ValueCoercion.Points(polygon.Points);
            if (points is not null) array.Add(points);
        }

        return array.Count == 0 ? null : array;
    }
}

/// <summary>A single bar.</summary>
internal sealed class SingleRebarExtractor : ReinforcementExtractorBase
{
    public override Type ObjectType => typeof(SingleRebar);

    protected override IReadOnlyList<DumpKey> OwnCreate
    {
        get
        {
            var keys = new List<DumpKey>
            {
                new DumpKey("$ctor", DumpKeyType.String, "Constructor hint; an opaque string."),
                new DumpKey("Polygon", DumpKeyType.PointArray, "The bar's shape, point by point."),
                new DumpKey("Size", DumpKeyType.String, "Bar size."),
                new DumpKey("StartHook", DumpKeyType.Object, "Shape, Angle, Radius, Length."),
                new DumpKey("EndHook", DumpKeyType.Object, "Shape, Angle, Radius, Length."),
            };
            keys.AddRange(ReinforcementCreateKeys);
            return keys;
        }
    }

    protected internal override void WriteCreate(ModelObject source, IDumpSink sink, DumpContext context)
    {
        var rebar = (SingleRebar)source;

        sink.Write("$ctor", JsonValue.String("new SingleRebar()"));
        var polygon = context.Try(() => rebar.Polygon);
        sink.Write("Polygon", polygon is null ? null : ValueCoercion.Points(polygon.Points));
        sink.Write("Size", ValueCoercion.Text(context.Try(() => rebar.Size)));
        sink.Write("StartHook", HookValue(context.Try(() => rebar.StartHook)));
        sink.Write("EndHook", HookValue(context.Try(() => rebar.EndHook)));

        WriteReinforcementCreate(rebar, sink, context);
        Base?.WriteCreate(source, sink, context);
    }

    protected internal override void WriteDerived(ModelObject source, IDumpSink sink, DumpContext context)
    {
        WriteReinforcementDerived((SingleRebar)source, sink, context);
        Base?.WriteDerived(source, sink, context);
    }
}

/// <summary>A mesh: a catalog item placed as a sheet, not a set of bars.</summary>
internal sealed class RebarMeshExtractor : ReinforcementExtractorBase
{
    public override Type ObjectType => typeof(RebarMesh);

    protected override IReadOnlyList<DumpKey> OwnCreate
    {
        get
        {
            var keys = new List<DumpKey>
            {
                new DumpKey("$ctor", DumpKeyType.String, "Constructor hint; an opaque string."),
                new DumpKey("CatalogName", DumpKeyType.String, "Mesh catalog name. With it, most of the geometry below is implied."),
                new DumpKey("Polygon", DumpKeyType.PointArray, "The sheet's outline."),
                new DumpKey("StartPoint", DumpKeyType.Point, "Start of the placing line."),
                new DumpKey("EndPoint", DumpKeyType.Point, "End of the placing line."),
                new DumpKey("MeshType", DumpKeyType.String, "MeshTypeEnum name."),
                new DumpKey("Length", DumpKeyType.Number, "Sheet length."),
                new DumpKey("Width", DumpKeyType.Number, "Sheet width."),
                new DumpKey("LongitudinalSize", DumpKeyType.String, "Longitudinal bar size."),
                new DumpKey("CrossSize", DumpKeyType.String, "Cross bar size."),
                new DumpKey("StartFromPlaneOffset", DumpKeyType.Number, "Plane offset at the start."),
                new DumpKey("EndFromPlaneOffset", DumpKeyType.Number, "Plane offset at the end."),
            };
            keys.AddRange(ReinforcementCreateKeys);
            return keys;
        }
    }

    protected internal override void WriteCreate(ModelObject source, IDumpSink sink, DumpContext context)
    {
        var mesh = (RebarMesh)source;

        sink.Write("$ctor", JsonValue.String("new RebarMesh()"));
        sink.Write("CatalogName", ValueCoercion.Text(context.Try(() => mesh.CatalogName)));
        var polygon = context.Try(() => mesh.Polygon);
        sink.Write("Polygon", polygon is null ? null : ValueCoercion.Points(polygon.Points));
        sink.Write("StartPoint", ValueCoercion.Point(context.Try(() => mesh.StartPoint)));
        sink.Write("EndPoint", ValueCoercion.Point(context.Try(() => mesh.EndPoint)));
        WriteEnum(sink, "MeshType", context.TryValue(() => mesh.MeshType));
        WriteNumber(sink, "Length", context.TryValue(() => mesh.Length));
        WriteNumber(sink, "Width", context.TryValue(() => mesh.Width));
        sink.Write("LongitudinalSize", ValueCoercion.Text(context.Try(() => mesh.LongitudinalSize)));
        sink.Write("CrossSize", ValueCoercion.Text(context.Try(() => mesh.CrossSize)));
        WriteNumber(sink, "StartFromPlaneOffset", context.TryValue(() => mesh.StartFromPlaneOffset));
        WriteNumber(sink, "EndFromPlaneOffset", context.TryValue(() => mesh.EndFromPlaneOffset));

        WriteReinforcementCreate(mesh, sink, context);
        Base?.WriteCreate(source, sink, context);
    }

    protected internal override void WriteDerived(ModelObject source, IDumpSink sink, DumpContext context)
    {
        WriteReinforcementDerived((RebarMesh)source, sink, context);
        Base?.WriteDerived(source, sink, context);
    }
}
