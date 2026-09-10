using System;
using System.Collections.Generic;
using Tekla.Structures.Model;
using TeklaDump.Json;
using TeklaDump.Sinks;

namespace TeklaDump.Extractors;

/// <summary>
/// The identity floor. Everything the registry can resolve ends up here, so an object of a type
/// nobody has written an extractor for still produces a correct — if thin — record rather than
/// nothing.
/// </summary>
/// <remarks>
/// There is nothing settable at this level: <c>ModelObject</c> has no constructor worth naming and
/// no assignable property that is meaningful across every subtype. So <c>create</c> is empty here
/// and the whole contribution is <c>derived</c>: the phase and the father component, both one
/// interop call, both emitted as references or scalars and never followed.
/// </remarks>
internal class ModelObjectExtractor : ExtractorBase
{
    public override Type ObjectType => typeof(ModelObject);

    protected override IReadOnlyList<DumpKey> OwnCreate => NoKeys;

    protected override IReadOnlyList<DumpKey> OwnDerived => new[]
    {
        new DumpKey("Phase", DumpKeyType.Integer, "Phase number the object belongs to."),
        new DumpKey("FatherComponent", DumpKeyType.Reference,
            "The component that created this object, when one did. Its presence is the warning that editing this object by hand will be undone the next time the component regenerates."),
    };

    protected internal override void WriteDerived(ModelObject source, IDumpSink sink, DumpContext context)
    {
        var phase = context.Try(() =>
        {
            Phase? value = null;
            return source.GetPhase(out value) ? value : null;
        });
        if (phase is not null) sink.Write("Phase", JsonValue.Number(phase.PhaseNumber));

        var father = context.Try(() => source.GetFatherComponent());
        sink.Write("FatherComponent", context.Reference(father));
    }

    protected static readonly DumpKey[] NoKeys = new DumpKey[0];
}
