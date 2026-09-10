using System.Linq;
using TeklaDump.Attributes;
using Xunit;

namespace TeklaDump.Tests;

/// <summary>
/// The <c>.lst</c> parser, driven against both the real files in <c>fixtures/lst/</c> and
/// hand-built ones for the shapes that are hard to find in a stock installation.
/// </summary>
public class TemplateAttributeCatalogTests
{
    [Fact]
    public void Parses_the_real_environment_excerpt()
    {
        var catalog = TemplateAttributeCatalog.Load(new[]
        {
            Fixtures.Path("lst", "contentattributes_global_excerpt.lst"),
        });

        Assert.False(catalog.IsEmpty);

        var part = catalog.ForContentType("PART");
        Assert.NotEmpty(part);
        Assert.Contains(part, definition => definition.Name == "PROFILE");
        Assert.Contains(part, definition => definition.Name == "WEIGHT");
    }

    [Fact]
    public void Reads_the_datatype_column_so_values_can_be_batched()
    {
        var catalog = TemplateAttributeCatalog.Load(new[]
        {
            Fixtures.Path("lst", "contentattributes_global_excerpt.lst"),
        });

        var part = catalog.ForContentType("PART");
        var weight = part.Single(definition => definition.Name == "WEIGHT");
        var profile = part.Single(definition => definition.Name == "PROFILE");

        // This is the whole reason the catalog exists: GetAllReportProperties needs the names
        // pre-sorted into string / double / integer buckets, and nothing else supplies that.
        Assert.Equal(TemplateValueType.Float, weight.ValueType);
        Assert.Equal(TemplateValueType.Character, profile.ValueType);
    }

    [Fact]
    public void Groups_a_dotted_name_by_its_leading_segment()
    {
        var catalog = TemplateAttributeCatalog.Load(new[]
        {
            Fixtures.Path("lst", "contentattributes_global_excerpt.lst"),
        });

        var bolt = catalog.ForContentType("BOLT");
        var nutAttributes = bolt.Where(definition => definition.Group == "NUT").ToList();

        Assert.NotEmpty(nutAttributes);
        Assert.All(nutAttributes, definition => Assert.False(definition.IsDirect));
    }

    [Fact]
    public void Treats_a_constituent_group_as_cheap_and_an_object_traversal_as_expensive()
    {
        var catalog = TemplateAttributeCatalog.Load(new[]
        {
            Fixtures.Path("lst", "contentattributes_global_excerpt.lst"),
        });

        var bolt = catalog.ForContentType("BOLT");

        // A bolt's nut is a constituent — reading it costs about what reading the bolt costs.
        // ASSEMBLY re-exposes a whole other object's attribute set, which is what makes Full slow.
        Assert.All(
            bolt.Where(definition => definition.Group == "NUT"),
            definition => Assert.True(definition.IsAssociated));
        Assert.All(
            bolt.Where(definition => definition.Group == "ASSEMBLY"),
            definition => Assert.True(definition.IsRelatedObject));
    }

    [Fact]
    public void Follows_include_directives_because_stock_environments_ship_container_files()
    {
        using var tree = new TempTree();

        // Several stock environments ship contentattributes.lst as a pure container: a handful of
        // [INCLUDE] lines and an EMPTY [BINDINGS] section. Ignoring the directive yields nothing.
        tree.Write("settings/contentattributes.lst",
            "[INCLUDE contentattributes_included.lst]\n[BINDINGS]\n");
        tree.Write("settings/contentattributes_included.lst",
            "WEIGHT FLOAT LEFT TRUE 10\n[BINDINGS]\nPART = WEIGHT\n");

        var catalog = TemplateAttributeCatalog.Load(new[]
        {
            System.IO.Path.Combine(tree.Root, "settings", "contentattributes.lst"),
        });

        Assert.Single(catalog.ForContentType("PART"));
        Assert.Equal("WEIGHT", catalog.ForContentType("PART")[0].Name);
    }

    [Fact]
    public void Strips_bracketed_nodes_and_version_suffixes_that_never_reach_the_property_api()
    {
        Assert.Equal("USERDEFINED.SPACE_B", TemplateAttributeCatalog.NormalizeName("USERDEFINED.[Base plate].SPACE_B"));
        Assert.Equal("WEIGHT", TemplateAttributeCatalog.NormalizeName("WEIGHT#2"));
        Assert.Equal("NUT", TemplateAttributeCatalog.GroupOf("NUT.WEIGHT"));
        Assert.Equal(string.Empty, TemplateAttributeCatalog.GroupOf("WEIGHT"));
    }

    [Fact]
    public void Drops_a_binding_no_file_declared_a_datatype_for()
    {
        using var tree = new TempTree();
        tree.Write("contentattributes.lst",
            "WEIGHT FLOAT LEFT TRUE 10\n[BINDINGS]\nPART = WEIGHT\nPART = MYSTERY\n");

        var catalog = TemplateAttributeCatalog.Load(new[]
        {
            System.IO.Path.Combine(tree.Root, "contentattributes.lst"),
        });

        // An attribute with no datatype cannot go in any bucket, so it is unqueryable rather than
        // merely unknown — dropping it is the only honest option, and the count says it happened.
        Assert.Single(catalog.ForContentType("PART"));
        Assert.Equal(1, catalog.UntypedCount);
    }
}
