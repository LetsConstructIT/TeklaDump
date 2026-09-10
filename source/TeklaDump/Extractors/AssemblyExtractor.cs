using System;
using System.Collections.Generic;
using Tekla.Structures.Model;
using TeklaDump.Sinks;
using TeklaDump.Values;
using TSAssembly = Tekla.Structures.Model.Assembly;

namespace TeklaDump.Extractors;

/// <summary>
/// An assembly: a main part, its secondaries, and its own numbering.
/// </summary>
/// <remarks>
/// Almost everything here is derived, and that is the honest answer rather than a thin extractor.
/// An assembly is not created by assigning properties — it comes into being when parts are welded
/// or bolted together with <c>ConnectAssemblies</c> set, and its membership is the RESULT of that.
/// The one genuinely settable pair is the numbering series and the name.
/// </remarks>
internal sealed class AssemblyExtractor : ExtractorBase
{
    public override Type ObjectType => typeof(TSAssembly);

    protected override ExtractorBase? Base => ModelObject;

    protected override IReadOnlyList<DumpKey> OwnCreate => new[]
    {
        new DumpKey("Name", DumpKeyType.String, "Assembly name."),
        new DumpKey("AssemblyNumber", DumpKeyType.Object, "Numbering series: Prefix and StartNumber."),
    };

    protected override IReadOnlyList<DumpKey> OwnDerived => new[]
    {
        new DumpKey("MainPart", DumpKeyType.Reference, "The part the assembly is named after."),
        new DumpKey("Secondaries", DumpKeyType.ReferenceArray, "Parts welded or bolted to the main part."),
        new DumpKey("SubAssemblies", DumpKeyType.ReferenceArray, "Nested assemblies."),
    };

    protected internal override void WriteCreate(ModelObject source, IDumpSink sink, DumpContext context)
    {
        var assembly = (TSAssembly)source;

        sink.Write("Name", ValueCoercion.Text(context.Try(() => assembly.Name)));
        sink.Write("AssemblyNumber", PartExtractor.NumberingSeriesValue(context.Try(() => assembly.AssemblyNumber)));

        Base?.WriteCreate(source, sink, context);
    }

    protected internal override void WriteDerived(ModelObject source, IDumpSink sink, DumpContext context)
    {
        var assembly = (TSAssembly)source;

        sink.Write("MainPart", context.Reference(context.Try(() => assembly.GetMainPart())));
        sink.Write("Secondaries", context.References(context.Try(() => assembly.GetSecondaries())));
        sink.Write("SubAssemblies", context.References(context.Try(() => assembly.GetSubAssemblies())));

        Base?.WriteDerived(source, sink, context);
    }

    private static readonly ModelObjectExtractor ModelObject = new ModelObjectExtractor();
}
