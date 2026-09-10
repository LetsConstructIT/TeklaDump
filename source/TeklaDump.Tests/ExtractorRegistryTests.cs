using Tekla.Structures.Model;
using TeklaDump.Extractors;
using Xunit;
using TSAssembly = Tekla.Structures.Model.Assembly;

namespace TeklaDump.Tests;

/// <summary>
/// The hierarchy walk. Tekla's type tree is deep and Trimble adds to it, so what happens to a type
/// nobody registered matters as much as what happens to one that is.
/// </summary>
public class ExtractorRegistryTests
{
    [Theory]
    [InlineData(typeof(Beam), typeof(Beam))]
    [InlineData(typeof(ContourPlate), typeof(ContourPlate))]
    [InlineData(typeof(PolyBeam), typeof(PolyBeam))]
    [InlineData(typeof(Connection), typeof(Connection))]
    [InlineData(typeof(Detail), typeof(Detail))]
    [InlineData(typeof(Seam), typeof(Seam))]
    [InlineData(typeof(TSAssembly), typeof(TSAssembly))]
    public void Resolves_a_registered_type_to_its_own_extractor(System.Type queried, System.Type expected)
    {
        var registry = ExtractorRegistry.CreateDefault();
        Assert.Equal(expected, registry.Resolve(queried)!.ObjectType);
    }

    [Theory]
    // BoltArray, BoltCircle and BoltXYList are all BoltGroup shapes: one extractor, and the
    // concrete type survives in $ctor rather than in a per-shape extractor.
    [InlineData(typeof(BoltArray), typeof(BoltGroup))]
    [InlineData(typeof(BoltCircle), typeof(BoltGroup))]
    [InlineData(typeof(BoltXYList), typeof(BoltGroup))]
    // A weld is a BaseWeld; a polygon weld has its own extractor and is checked above.
    [InlineData(typeof(Weld), typeof(BaseWeld))]
    // Component is a BaseComponent with nothing of its own to add beyond the base's input handling.
    [InlineData(typeof(Component), typeof(BaseComponent))]
    [InlineData(typeof(CustomPart), typeof(BaseComponent))]
    public void Falls_back_to_the_nearest_registered_base(System.Type queried, System.Type expected)
    {
        var registry = ExtractorRegistry.CreateDefault();
        Assert.Equal(expected, registry.Resolve(queried)!.ObjectType);
    }

    [Fact]
    public void An_unregistered_part_subtype_still_produces_a_part_record()
    {
        // The reason the walk exists. A ContourPlate subclass, a new Trimble type, anything that
        // is a Part: it emits a correct Part-level record instead of nothing at all.
        var registry = ExtractorRegistry.CreateDefault();

        Assert.Equal(typeof(Part), registry.Resolve(typeof(BentPlate))?.ObjectType);
        Assert.Equal(typeof(Part), registry.Resolve(typeof(LoftedPlate))?.ObjectType);
    }

    [Fact]
    public void Anything_that_is_a_model_object_resolves_to_something()
    {
        // ModelObjectExtractor is the floor, which is what makes bulk mode's "no reflection" rule
        // safe: there is always an extractor, so a record is never simply missing.
        var registry = ExtractorRegistry.CreateDefault();

        Assert.NotNull(registry.Resolve(typeof(Grid)));
        Assert.NotNull(registry.Resolve(typeof(ReferenceModel)));
        Assert.Equal(typeof(ModelObject), registry.Resolve(typeof(Grid))!.ObjectType);
    }
}
