using System;
using System.Collections;
using System.Globalization;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;
using TeklaDump.Json;
using TSAngle = Tekla.Structures.Datatype.Angle;
using TSDistance = Tekla.Structures.Datatype.Distance;
using TSIdentifier = Tekla.Structures.Identifier;

namespace TeklaDump.Values;

/// <summary>
/// Turns Tekla and CLR values into JSON nodes, under a <see cref="UnitPolicy"/>.
/// </summary>
/// <remarks>
/// Merged from TeklaLookup's <c>ValueFormatting</c> and <c>TeklaValueFormatter</c>, with the
/// display-formatting concerns dropped: JSON is not a UI, so nothing here produces a
/// human-readable summary string. A value either has a faithful JSON form or is omitted.
/// <para>
/// Returning <c>null</c> means "omit this key". Empty strings, empty profiles and empty materials
/// are omitted rather than emitted as <c>""</c>, because a create block is read as a list of
/// things to assign and assigning an empty profile is not a thing anyone wants generated.
/// </para>
/// </remarks>
internal static class ValueCoercion
{
    /// <summary>A point as <c>{x, y, z}</c>. Coordinates are in the CURRENT work plane.</summary>
    public static JsonValue Point3(double x, double y, double z)
    {
        var obj = new JsonObject();
        obj.Add("x", JsonValue.Number(x));
        obj.Add("y", JsonValue.Number(y));
        obj.Add("z", JsonValue.Number(z));
        return obj;
    }

    public static JsonValue? Point(Point? point) =>
        point is null ? null : Point3(point.X, point.Y, point.Z);

    public static JsonValue? Vector(Vector? vector) =>
        vector is null ? null : Point3(vector.X, vector.Y, vector.Z);

    public static JsonValue? Text(string? value) =>
        string.IsNullOrEmpty(value) ? null : JsonValue.String(value!);

    public static JsonValue? Profile(Profile? profile) =>
        profile is null ? null : Text(profile.ProfileString);

    public static JsonValue? Material(Material? material) =>
        material is null ? null : Text(material.MaterialString);

    /// <summary>A coordinate system as origin plus its two axes.</summary>
    public static JsonValue? CoordinateSystem(CoordinateSystem? system)
    {
        if (system is null) return null;
        var obj = new JsonObject();
        obj.AddIfPresent("origin", Point(system.Origin));
        obj.AddIfPresent("axisX", Vector(system.AxisX));
        obj.AddIfPresent("axisY", Vector(system.AxisY));
        return obj.Count == 0 ? null : obj;
    }

    /// <summary>An <c>ArrayList</c>/array of points, as it comes out of the polygon-shaped APIs.</summary>
    public static JsonValue? Points(IEnumerable? points)
    {
        if (points is null) return null;
        var array = new JsonArray();
        foreach (var item in points)
        {
            // ContourPoint derives from Point, so it MUST be matched first — the other order
            // silently drops every chamfer in the model.
            if (item is ContourPoint contour) array.Add(ContourPointValue(contour));
            else if (item is Point point) array.Add(Point3(point.X, point.Y, point.Z));
        }
        return array.Count == 0 ? null : array;
    }

    /// <summary>
    /// A contour point: the position plus its chamfer, which is the part people forget and then
    /// wonder why the regenerated plate has square corners.
    /// </summary>
    public static JsonValue ContourPointValue(ContourPoint point)
    {
        var obj = new JsonObject();
        obj.Add("x", JsonValue.Number(point.X));
        obj.Add("y", JsonValue.Number(point.Y));
        obj.Add("z", JsonValue.Number(point.Z));

        var chamfer = point.Chamfer;
        if (chamfer is not null && (chamfer.X != 0 || chamfer.Y != 0 || chamfer.Type != Chamfer.ChamferTypeEnum.CHAMFER_NONE))
        {
            var chamferObject = new JsonObject();
            chamferObject.Add("Type", JsonValue.String(chamfer.Type.ToString()));
            chamferObject.Add("X", JsonValue.Number(chamfer.X));
            chamferObject.Add("Y", JsonValue.Number(chamfer.Y));
            chamferObject.Add("DZ1", JsonValue.Number(chamfer.DZ1));
            chamferObject.Add("DZ2", JsonValue.Number(chamfer.DZ2));
            obj.Add("Chamfer", chamferObject);
        }

        return obj;
    }

    /// <summary>
    /// A UDA value, whose CLR type is whatever Tekla stored. A UDA written with a mismatched type
    /// reads back wrong — that is a Tekla trap, not ours, and the README says so — but the value
    /// still round-trips faithfully as whatever type it actually came back as.
    /// </summary>
    public static JsonValue? UserPropertyValue(object? raw)
    {
        switch (raw)
        {
            case null: return null;
            case string s: return Text(s);
            case double d: return JsonValue.Number(d);
            case float f: return JsonValue.Number(f);
            case int i: return JsonValue.Number(i);
            case long l: return JsonValue.Number(l);
            case short sh: return JsonValue.Number(sh);
            case bool b: return JsonValue.Bool(b);
            case DateTime dt: return JsonValue.String(dt.ToString("o", CultureInfo.InvariantCulture));
            default: return Text(raw.ToString());
        }
    }

    /// <summary>
    /// A template (report) value under the given unit policy. <paramref name="attributeName"/>
    /// decides the quantity kind; see <see cref="Units"/> for why that is an inference and what
    /// happens when it fails.
    /// </summary>
    public static JsonValue? TemplateValue(string attributeName, object? raw, UnitPolicy policy)
    {
        if (raw is null) return null;

        if (raw is string s) return Text(s);
        if (raw is int i) return JsonValue.Number(i);
        if (raw is long l) return JsonValue.Number(l);

        if (raw is double d)
        {
            if (policy == UnitPolicy.Native) return JsonValue.Number(d);

            var kind = Units.KindFor(attributeName);
            if (kind == UnitKind.Other)
            {
                var label = Units.LabelFor(attributeName);
                if (label is not null)
                {
                    // The ONE place a per-value unit stamp appears: a known unit we cannot convert
                    // must not be readable as one of the five the header declares.
                    var stamped = new JsonObject();
                    stamped.Add("value", JsonValue.Number(d));
                    stamped.Add("unit", JsonValue.String(label));
                    return stamped;
                }
            }

            return JsonValue.Number(Units.NormalizeReportValue(d, kind));
        }

        return Text(raw.ToString());
    }

    /// <summary>
    /// A <c>Tekla.Structures.Datatype.Distance</c>, which wraps a value plus the unit it was
    /// created with. <c>Millimeters</c> is the normalized form; native mode reports what the
    /// object itself says.
    /// </summary>
    public static JsonValue Distance(TSDistance distance, UnitPolicy policy) =>
        policy == UnitPolicy.Normalized
            ? JsonValue.Number(distance.Millimeters)
            : JsonValue.Number(distance.Value);

    /// <summary>
    /// An angle. Degrees in the normalized header's units; in native mode, the value in whatever
    /// unit the session is currently configured for.
    /// </summary>
    public static JsonValue Angle(TSAngle angle, UnitPolicy policy) =>
        policy == UnitPolicy.Normalized
            ? JsonValue.Number(angle.Degrees)
            : JsonValue.Number(angle.CurrentUnitValue);

    /// <summary>An enum as its name. Ordinals are meaningless in a document a human reads.</summary>
    public static JsonValue Enum(System.Enum value) => JsonValue.String(value.ToString());

    /// <summary>
    /// A GUID string, or null when the identifier carries none. <c>Identifier.GUID</c> is
    /// <c>Guid.Empty</c> for ID-based identifiers; resolving those is
    /// <c>SessionInfo.ResolveGuid</c>'s job, since it needs a <c>Model</c>.
    /// </summary>
    public static string? GuidOf(TSIdentifier? identifier)
    {
        if (identifier is null) return null;
        var guid = identifier.GUID;
        return guid == System.Guid.Empty ? null : guid.ToString();
    }
}
