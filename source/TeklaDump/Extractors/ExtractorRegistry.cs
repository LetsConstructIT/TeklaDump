using System;
using System.Collections.Generic;
using System.Linq;

namespace TeklaDump.Extractors;

/// <summary>
/// Maps a CLR type onto the extractor that handles it, walking the inheritance chain.
/// </summary>
/// <remarks>
/// The walk is what keeps the tool correct as Tekla grows. Its hierarchy is deep and Trimble adds
/// types: an unregistered <c>Beam</c> subclass resolves to <see cref="PartExtractor"/> and emits a
/// correct part record, rather than falling off the end and emitting nothing. Only when nothing in
/// the chain is registered does inspect mode reach for the reflection fallback — and bulk mode
/// never does.
/// </remarks>
internal sealed class ExtractorRegistry
{
    private readonly Dictionary<Type, IObjectExtractor> _byType = new Dictionary<Type, IObjectExtractor>();
    private readonly Dictionary<Type, IObjectExtractor?> _resolved = new Dictionary<Type, IObjectExtractor?>();

    /// <summary>
    /// The registry the library ships with. A fresh instance per call: the resolve cache is
    /// per-run state and a shared one would outlive the process's usefulness.
    /// </summary>
    public static ExtractorRegistry CreateDefault()
    {
        var registry = new ExtractorRegistry();
        foreach (var extractor in DefaultExtractors()) registry.Register(extractor);
        return registry;
    }

    /// <summary>
    /// Every extractor the library defines, most-general first. The schema generator enumerates
    /// this, so the order here is the order of the schema's type sections.
    /// </summary>
    public static IReadOnlyList<IObjectExtractor> DefaultExtractors() => new IObjectExtractor[]
    {
        new ModelObjectExtractor(),
        new PartExtractor(),
        new BeamExtractor(),
        new ContourPlateExtractor(),
        new PolyBeamExtractor(),
        new BoltExtractor(),
        new WeldExtractor(),
        new PolygonWeldExtractor(),
        new RebarGroupExtractor(),
        new SingleRebarExtractor(),
        new RebarMeshExtractor(),
        new ComponentExtractor(),
        new ConnectionExtractor(),
        new DetailExtractor(),
        new SeamExtractor(),
        new AssemblyExtractor(),
    };

    public void Register(IObjectExtractor extractor)
    {
        _byType[extractor.ObjectType] = extractor;
        _resolved.Clear();
    }

    /// <summary>
    /// The most derived registered extractor for <paramref name="type"/>, or null when nothing in
    /// its chain is registered.
    /// </summary>
    public IObjectExtractor? Resolve(Type type)
    {
        if (_resolved.TryGetValue(type, out var cached)) return cached;

        IObjectExtractor? found = null;
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (_byType.TryGetValue(current, out var extractor))
            {
                found = extractor;
                break;
            }
        }

        _resolved[type] = found;
        return found;
    }

    /// <summary>Registered extractors, in registration order. For the schema generator and tests.</summary>
    public IReadOnlyList<IObjectExtractor> All => _byType.Values.ToArray();
}
