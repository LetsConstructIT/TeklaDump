using System;
using System.Collections.Generic;
using System.Linq;
using Tekla.Structures.Model;
using TeklaDump.Extractors;
using TeklaDump.Session;
using TeklaDump.Sinks;
using Xunit;
using TSAssembly = Tekla.Structures.Model.Assembly;

namespace TeklaDump.Tests;

/// <summary>
/// The contract that makes the schema mean something: declared keys are exclusive, and nothing an
/// extractor writes is undeclared.
/// </summary>
/// <remarks>
/// These run without a Tekla installation. The Open API assemblies come from NuGet and the objects
/// are UNINSERTED — <c>new Beam()</c> is a plain CLR object until <c>Insert()</c> is called, so
/// every property read here is a field read rather than a database round trip.
/// </remarks>
public class ExtractorContractTests
{
    // IObjectExtractor is internal — the extractor contract is not published API — so the theory
    // travels by the extractor's ObjectType and resolves it here. It also gives each case a test
    // name that reads as the Tekla type under contract.
    public static IEnumerable<object[]> AllExtractors() =>
        ExtractorRegistry.DefaultExtractors().Select(extractor => new object[] { extractor.ObjectType });

    private static IObjectExtractor ExtractorFor(Type objectType) =>
        ExtractorRegistry.DefaultExtractors().Single(extractor => extractor.ObjectType == objectType);

    [Theory]
    [MemberData(nameof(AllExtractors))]
    public void A_key_is_settable_or_derived_but_never_both(Type objectType)
    {
        var extractor = ExtractorFor(objectType);

        // This assertion IS the anti-round-trip-trap mechanism. A key in both blocks would mean the
        // document says "you can assign this" and "this is read-only" about the same property.
        var overlap = extractor.CreateKeys.Intersect(extractor.DerivedKeys, StringComparer.Ordinal).ToList();

        Assert.True(
            overlap.Count == 0,
            extractor.ObjectType.Name + " declares " + string.Join(", ", overlap) + " in both create and derived.");
    }

    [Theory]
    [MemberData(nameof(AllExtractors))]
    public void Declares_each_key_once(Type objectType)
    {
        var extractor = ExtractorFor(objectType);

        Assert.Equal(extractor.CreateKeys.Count, extractor.CreateKeys.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(extractor.DerivedKeys.Count, extractor.DerivedKeys.Distinct(StringComparer.Ordinal).Count());
    }

    public static IEnumerable<object[]> SampleObjects()
    {
        yield return new object[] { new Beam() };
        yield return new object[] { new ContourPlate() };
        yield return new object[] { new PolyBeam() };
        yield return new object[] { new BoltArray() };
        yield return new object[] { new Weld() };
        yield return new object[] { new PolygonWeld() };
        yield return new object[] { new RebarGroup() };
        yield return new object[] { new SingleRebar() };
        yield return new object[] { new RebarMesh() };
        yield return new object[] { new Component() };
        yield return new object[] { new Connection() };
        yield return new object[] { new Detail() };
        yield return new object[] { new Seam() };
        yield return new object[] { new TSAssembly() };
    }

    [Theory]
    [MemberData(nameof(SampleObjects))]
    public void Emits_only_declared_keys(ModelObject source)
    {
        var registry = ExtractorRegistry.CreateDefault();
        var extractor = registry.Resolve(source.GetType());
        Assert.NotNull(extractor);

        var sink = new ValidatingSink(new TreeSink());
        var context = TestContext.Create();

        sink.BeginRecord();
        extractor!.Write(source, sink, context);
        sink.EndRecord();

        AssertSubset(sink.KeysIn("create"), extractor.CreateKeys, source, "create");
        AssertSubset(sink.KeysIn("derived"), extractor.DerivedKeys, source, "derived");
    }

    private static void AssertSubset(
        IReadOnlyList<string> emitted, IReadOnlyList<string> declared, ModelObject source, string section)
    {
        var undeclared = emitted.Except(declared, StringComparer.Ordinal).ToList();

        Assert.True(
            undeclared.Count == 0,
            source.GetType().Name + " emitted undeclared " + section + " key(s): " + string.Join(", ", undeclared));
    }

    [Fact]
    public void Every_extractor_claims_a_tekla_type()
    {
        foreach (var extractor in ExtractorRegistry.DefaultExtractors())
            Assert.True(typeof(ModelObject).IsAssignableFrom(extractor.ObjectType));
    }
}

/// <summary>A context with no Tekla session, which is the state every test here runs in.</summary>
/// <remarks>
/// The session is passed as null rather than through <see cref="SessionInfo.TryCreate"/>, so the
/// suite behaves identically on a CI runner and on a developer machine that happens to have Tekla
/// open. A null session is a supported state: the header carries the tool fields only, and GUID
/// resolution falls back to the object's own identifier.
/// </remarks>
internal static class TestContext
{
    public static DumpContext Create(DumpOptions? options = null) =>
        new DumpContext(options ?? DumpOptions.Default, session: null);
}
