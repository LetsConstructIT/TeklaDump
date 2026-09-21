using TeklaDump.Json;
using TeklaDump.Values;
using Xunit;

namespace TeklaDump.Tests;

/// <summary>
/// The unit policy: what gets converted, what does not, and the one place a per-value unit stamp
/// is allowed to appear.
/// </summary>
public class UnitsTests
{
    // UnitKind is internal — it is not part of the published surface — so the expectation travels
    // as nameof(). That still fails to compile if a member is renamed, which is the point of it.
    [Theory]
    [InlineData("WEIGHT", nameof(UnitKind.Mass))]
    [InlineData("ASSEMBLY.WEIGHT", nameof(UnitKind.Mass))]      // the kind comes from the LAST segment
    [InlineData("WEIGHT_NET", nameof(UnitKind.Mass))]
    [InlineData("AREA", nameof(UnitKind.Area))]
    [InlineData("PAINTING_AREA", nameof(UnitKind.Area))]
    [InlineData("VOLUME", nameof(UnitKind.Volume))]
    [InlineData("LENGTH", nameof(UnitKind.Length))]
    [InlineData("DENSITY", nameof(UnitKind.Other))]
    public void Classifies_known_attribute_names(string name, string expected)
    {
        Assert.Equal(expected, Units.KindFor(name).ToString());
    }

    [Theory]
    [InlineData("PART_POS")]
    [InlineData("CLASS")]
    [InlineData("SOME_FIRM_ATTRIBUTE")]
    public void Leaves_an_unrecognised_name_alone(string name)
    {
        // The safe direction. A wrong conversion is worse than no conversion, so a name the table
        // has never heard of normalizes nothing and the document makes no claim about its unit.
        Assert.Equal(UnitKind.None, Units.KindFor(name));
    }

    [Fact]
    public void Converts_report_area_from_square_metres()
    {
        // The asymmetry this whole mechanism exists for: AREA comes back in m2 while VOLUME comes
        // back in mm3, in the same environment, in the same call.
        Assert.Equal(1_000_000d, Units.NormalizeReportValue(1d, UnitKind.Area));
        Assert.Equal(1d, Units.NormalizeReportValue(1d, UnitKind.Volume));
        Assert.Equal(1d, Units.NormalizeReportValue(1d, UnitKind.Mass));
    }

    [Fact]
    public void Normalized_mode_emits_a_bare_number()
    {
        var value = ValueCoercion.TemplateValue("WEIGHT", 412.3d, UnitPolicy.Normalized);
        Assert.Equal("412.3", value!.ToCompactString());
    }

    [Fact]
    public void Native_mode_touches_nothing()
    {
        // "Raw" is not a coherent unit system, but the user asked for raw and gets raw — including
        // the m2 area that normalized mode would have converted.
        var value = ValueCoercion.TemplateValue("AREA", 2.5d, UnitPolicy.Native);
        Assert.Equal("2.5", value!.ToCompactString());
    }

    [Fact]
    public void Stamps_a_unit_only_where_it_cannot_normalize()
    {
        var stamped = ValueCoercion.TemplateValue("DENSITY", 7850d, UnitPolicy.Normalized);
        Assert.Equal("{\"value\":7850,\"unit\":\"kg/m3\"}", stamped!.ToCompactString());

        // ... and nowhere else. Repeating a unit on every value costs about a third of the bytes
        // of a point-heavy file whose consumer has a context budget.
        var bare = ValueCoercion.TemplateValue("LENGTH", 6000d, UnitPolicy.Normalized);
        Assert.Equal("6000", bare!.ToCompactString());
    }

    [Fact]
    public void Rounds_to_six_decimals_so_two_runs_diff_clean()
    {
        Assert.Equal("0.3", JsonValue.Number(0.1 + 0.2).ToCompactString());
        Assert.Equal("1.000001", JsonValue.Number(1.0000006).ToCompactString());
    }

    [Fact]
    public void Keeps_the_document_valid_when_a_value_is_not_a_number()
    {
        // JSON has no NaN or Infinity literal, and a document that cannot be parsed is worse than
        // one that says "NaN" in a string.
        Assert.Equal("\"NaN\"", JsonValue.Number(double.NaN).ToCompactString());
        Assert.Equal("\"Infinity\"", JsonValue.Number(double.PositiveInfinity).ToCompactString());
    }
}
