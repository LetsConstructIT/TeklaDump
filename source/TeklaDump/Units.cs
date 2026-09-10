using System;
using System.Collections.Generic;

namespace TeklaDump;

/// <summary>
/// The kinds of quantity the dump knows how to normalize. Anything not on this list is either
/// unitless (class, counts, enum ordinals) or carries a unit we cannot convert, and is handled
/// accordingly by <see cref="Units"/>.
/// </summary>
public enum UnitKind
{
    /// <summary>Unitless, or unknown — emitted as a bare number, untouched.</summary>
    None,
    Length,
    Area,
    Volume,
    Mass,
    Angle,

    /// <summary>
    /// Known to carry a unit, but not one of the five normalizable kinds (density, cost, time).
    /// Emitted as <c>{ "value": ..., "unit": "..." }</c> so it cannot be mistaken for a normalized
    /// number. See the schema README, "Unit policy".
    /// </summary>
    Other,
}

/// <summary>
/// The one place a conversion factor lives, with the source of each factor stated.
/// </summary>
/// <remarks>
/// <para>
/// The Open API's own geometry is already in the model's base units, which are millimetres for
/// every stock environment: <c>Point.X</c>, <c>Beam.StartPoint</c> and every offset are mm, and
/// <c>Tekla.Structures.Datatype.Distance</c> exposes <c>Millimeters</c> outright. So normalizing
/// API-side lengths is the identity, and the factors below exist for REPORT (template) values,
/// which are not uniformly scaled — <c>AREA</c> comes back in m2 while <c>VOLUME</c> comes back in
/// mm3, in the same environment, in the same call.
/// </para>
/// <para>
/// <b>Known limitation, stated rather than papered over.</b> Each attribute's unit ought to come
/// from the runtime <c>.lst</c> parse. It cannot: <c>contentattributes*.lst</c>
/// declares a DATATYPE (CHARACTER / FLOAT / INTEGER) and no unit. So the kind is inferred from the
/// attribute name against <see cref="KindFor"/>'s table, an inference that is right for the stock
/// catalog and can be wrong for a hand-authored attribute whose name resembles a stock one. Two
/// consequences, both deliberate: an unrecognised name normalizes nothing (bare number, no claim
/// made), and <c>--units native</c> switches the whole mechanism off for anyone who would rather
/// convert downstream.
/// </para>
/// </remarks>
internal static class Units
{
    /// <summary>Header <c>units</c> block for <see cref="UnitPolicy.Normalized"/>.</summary>
    public static readonly KeyValuePair<string, string>[] NormalizedUnits =
    {
        new KeyValuePair<string, string>("length", "mm"),
        new KeyValuePair<string, string>("area", "mm2"),
        new KeyValuePair<string, string>("volume", "mm3"),
        new KeyValuePair<string, string>("mass", "kg"),
        new KeyValuePair<string, string>("angle", "deg"),
    };

    /// <summary>Report AREA values come back in square metres; 1 m2 = 1e6 mm2.</summary>
    private const double SquareMetresToSquareMillimetres = 1000000d;

    /// <summary>
    /// Normalizes a REPORT value of the given kind into the header's declared units. Report volume
    /// and mass already arrive as mm3 and kg in stock environments, so those are the identity and
    /// are listed here to make that a decision rather than an omission.
    /// </summary>
    public static double NormalizeReportValue(double value, UnitKind kind)
    {
        switch (kind)
        {
            case UnitKind.Area: return value * SquareMetresToSquareMillimetres;
            case UnitKind.Length: return value;   // mm
            case UnitKind.Volume: return value;   // mm3
            case UnitKind.Mass: return value;     // kg
            default: return value;
        }
    }

    /// <summary>The unit label for the object-form stamp used by <see cref="UnitKind.Other"/>.</summary>
    public static string? LabelFor(string attributeName)
    {
        var leaf = LeafOf(attributeName);
        return OtherUnits.TryGetValue(leaf, out var label) ? label : null;
    }

    /// <summary>
    /// The quantity kind of a template attribute, inferred from its name's last dotted segment
    /// (<c>ASSEMBLY.WEIGHT</c> is a mass, the same as <c>WEIGHT</c>). Unknown names get
    /// <see cref="UnitKind.None"/> and pass through untouched — the safe direction, since a wrong
    /// conversion is worse than no conversion.
    /// </summary>
    public static UnitKind KindFor(string attributeName)
    {
        var leaf = LeafOf(attributeName);
        if (Kinds.TryGetValue(leaf, out var kind)) return kind;
        if (OtherUnits.ContainsKey(leaf)) return UnitKind.Other;

        // Suffix rules for the families the stock catalog generates by composition
        // (WEIGHT_NET, AREA_GROSS, LENGTH_MAX, ...). Checked after the exact table so an exact
        // entry always wins.
        if (EndsWithSegment(leaf, "WEIGHT")) return UnitKind.Mass;
        if (EndsWithSegment(leaf, "AREA")) return UnitKind.Area;
        if (EndsWithSegment(leaf, "VOLUME")) return UnitKind.Volume;
        if (EndsWithSegment(leaf, "LENGTH")) return UnitKind.Length;

        return UnitKind.None;
    }

    private static readonly Dictionary<string, UnitKind> Kinds =
        new Dictionary<string, UnitKind>(StringComparer.OrdinalIgnoreCase)
        {
            // Mass
            { "WEIGHT", UnitKind.Mass },
            { "WEIGHT_NET", UnitKind.Mass },
            { "WEIGHT_GROSS", UnitKind.Mass },
            { "WEIGHT_TOTAL", UnitKind.Mass },

            // Length
            { "LENGTH", UnitKind.Length },
            { "LENGTH_GROSS", UnitKind.Length },
            { "HEIGHT", UnitKind.Length },
            { "WIDTH", UnitKind.Length },
            { "THICKNESS", UnitKind.Length },
            { "DIAMETER", UnitKind.Length },
            { "RADIUS", UnitKind.Length },
            { "SPACING", UnitKind.Length },
            { "COVER_THICKNESS", UnitKind.Length },
            { "BOLT_SIZE", UnitKind.Length },
            { "SIZE", UnitKind.Length },

            // Area
            { "AREA", UnitKind.Area },
            { "AREA_NET", UnitKind.Area },
            { "AREA_GROSS", UnitKind.Area },
            { "PAINTING_AREA", UnitKind.Area },
            { "TOP_AREA", UnitKind.Area },
            { "BOTTOM_AREA", UnitKind.Area },
            { "SIDE_AREA", UnitKind.Area },

            // Volume
            { "VOLUME", UnitKind.Volume },
            { "VOLUME_NET", UnitKind.Volume },
            { "VOLUME_GROSS", UnitKind.Volume },

            // Angle
            { "ANGLE", UnitKind.Angle },
            { "ROTATION", UnitKind.Angle },
            { "SKEW", UnitKind.Angle },
        };

    /// <summary>
    /// Attributes whose unit is known but is not one of the five normalizable kinds. These get the
    /// per-value object stamp so nobody reads them as normalized numbers.
    /// </summary>
    private static readonly Dictionary<string, string> OtherUnits =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "DENSITY", "kg/m3" },
            { "COST", "currency" },
            { "PRICE", "currency" },
        };

    private static string LeafOf(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot >= 0 && dot < name.Length - 1 ? name.Substring(dot + 1) : name;
    }

    /// <summary>
    /// True when <paramref name="name"/> ends with <paramref name="segment"/> on an underscore
    /// boundary. Prevents a compound like <c>MESH_AREA</c> from being confused with a different
    /// word that merely ends in the same letters.
    /// </summary>
    private static bool EndsWithSegment(string name, string segment)
    {
        if (name.Length <= segment.Length) return false;
        if (!name.EndsWith(segment, StringComparison.OrdinalIgnoreCase)) return false;
        return name[name.Length - segment.Length - 1] == '_';
    }
}
