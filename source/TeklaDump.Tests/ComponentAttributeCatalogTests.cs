using System.Linq;
using TeklaDump.Attributes;
using Xunit;

namespace TeklaDump.Tests;

/// <summary>
/// The saved-attribute-file parser, driven against real files from a Tekla 2026 installation.
/// </summary>
/// <remarks>
/// This is the discovery mechanism behind component attributes, and it is the largest open risk in
/// the component path — there is no API that lists a component's attribute names, so if this parse
/// is wrong the create block of a connection is silently empty. Hence real fixtures.
/// </remarks>
public class ComponentAttributeCatalogTests
{
    [Fact]
    public void Reads_a_numbered_components_saved_attributes()
    {
        var names = ComponentAttributeCatalog.ParseFile(
            Fixtures.Path("component-attrs", "standard.j14000104"));

        Assert.NotEmpty(names);

        // The queryable name is what follows "joint_attributes." — the prefix names the file's
        // owner and is not part of any name GetAttribute accepts.
        Assert.Contains(names, name => name.Name == "zang1");
        Assert.DoesNotContain(names, name => name.Name.Contains("joint_attributes"));
    }

    [Fact]
    public void Guesses_the_overload_to_try_first_from_the_saved_literal()
    {
        var names = ComponentAttributeCatalog
            .ParseFile(Fixtures.Path("component-attrs", "standard.j14000104"))
            .ToDictionary(name => name.Name, name => name.ValueType);

        Assert.Equal(ComponentValueType.Integer, names["cut1"]);      // 0
        Assert.Equal(ComponentValueType.Double, names["zang1"]);      // 0.000000
        Assert.Equal(ComponentValueType.String, names["prof"]);       // ""
    }

    [Fact]
    public void Skips_the_bookkeeping_tekla_writes_into_every_file()
    {
        var names = ComponentAttributeCatalog.ParseFile(
            Fixtures.Path("component-attrs", "standard.j14000104"));

        Assert.DoesNotContain(names, name => name.Name == "saveas_file");
        Assert.DoesNotContain(names, name => name.Name == "get_menu");
    }

    [Fact]
    public void Reads_a_plugins_saved_attributes_the_same_way()
    {
        var names = ComponentAttributeCatalog.ParseFile(
            Fixtures.Path("component-attrs", "standard.p_GenericFormworkBeam"));

        Assert.Contains(names, name => name.Name == "BeamClass");
        Assert.DoesNotContain(names, name => name.Name.Contains("_attributes"));
    }

    [Theory]
    [InlineData("joint_attributes.zang1", "zang1")]
    [InlineData("GenericFormworkBeam_attributes.BeamClass", "BeamClass")]
    [InlineData("part_number_prefix", "part_number_prefix")]
    [InlineData("SOME.DOTTED.NAME", "SOME.DOTTED.NAME")]
    public void Strips_only_an_attributes_prefix(string token, string expected)
    {
        // A dot that is not an "<owner>_attributes." prefix is part of the name. Stripping it
        // would quietly rename every attribute that legitimately contains a dot.
        Assert.Equal(expected, ComponentAttributeCatalog.StripPrefix(token));
    }

    // ComponentValueType is internal, so the expectation travels as nameof() — see UnitsTests.
    [Theory]
    [InlineData("0", nameof(ComponentValueType.Integer))]
    [InlineData("-2147483648", nameof(ComponentValueType.Integer))]
    [InlineData("0.000000", nameof(ComponentValueType.Double))]
    [InlineData("\"standard\"", nameof(ComponentValueType.String))]
    [InlineData("", nameof(ComponentValueType.String))]
    public void Classifies_saved_literals(string literal, string expected)
    {
        Assert.Equal(expected, ComponentAttributeCatalog.GuessType(literal).ToString());
    }
}
