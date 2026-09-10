using System;
using System.Collections.Generic;
using System.Linq;
using Tekla.Structures.Model;
using TeklaDump.Sinks;

namespace TeklaDump.Extractors;

/// <summary>The JSON shape of one declared key. Drives schema generation.</summary>
public enum DumpKeyType
{
    String,
    Number,
    Integer,
    Boolean,

    /// <summary><c>{x, y, z}</c>.</summary>
    Point,

    /// <summary>An array of <see cref="Point"/> objects.</summary>
    PointArray,

    /// <summary><c>{objectType, guid}</c> — a reference that is never followed.</summary>
    Reference,

    /// <summary>An array of references.</summary>
    ReferenceArray,

    /// <summary>A nested object whose members are documented in the schema README (Position, Chamfer).</summary>
    Object,

    /// <summary>An array of anything else.</summary>
    Array,
}

/// <summary>One key an extractor may emit, with the type annotation the schema generator needs.</summary>
public sealed class DumpKey
{
    public DumpKey(string name, DumpKeyType type, string? description = null, string? since = null)
    {
        Name = name;
        Type = type;
        Description = description;
        Since = since;
    }

    public string Name { get; }

    public DumpKeyType Type { get; }

    /// <summary>One line for the schema README's field table.</summary>
    public string? Description { get; }

    /// <summary>
    /// Tekla version this field first appears on, for members reached through
    /// <c>OptionalTeklaApi</c>. Null means "every supported version".
    /// </summary>
    public string? Since { get; }
}

/// <summary>
/// Turns one kind of model object into records. The contract that makes the schema reviewable in
/// a diff.
/// </summary>
public interface IObjectExtractor
{
    /// <summary>The most-derived CLR type this extractor claims.</summary>
    Type ObjectType { get; }

    /// <summary>
    /// Declared up front, not discovered by running: the keys this extractor may emit under
    /// <c>create</c> and under <c>derived</c>. Drives (a) the exclusivity test, (b) schema
    /// generation, (c) the schema README table. Emitting an undeclared key is a test failure.
    /// </summary>
    IReadOnlyList<string> CreateKeys { get; }

    IReadOnlyList<string> DerivedKeys { get; }

    /// <summary>The same keys with their type annotations. What the schema generator reads.</summary>
    IReadOnlyList<DumpKey> Create { get; }

    IReadOnlyList<DumpKey> Derived { get; }

    /// <summary>
    /// Writes the <c>create</c> and <c>derived</c> sections. The caller has already opened the
    /// record and written the identity keys, and writes the attribute sections afterwards — an
    /// extractor is responsible for the Open API surface of its type and nothing else.
    /// </summary>
    void Write(ModelObject source, IDumpSink sink, DumpContext context);
}

/// <summary>
/// Base for extractors: composes declared keys along the inheritance chain and projects the name
/// lists, so a subclass declares only what it adds.
/// </summary>
internal abstract class ExtractorBase : IObjectExtractor
{
    private IReadOnlyList<DumpKey>? _create;
    private IReadOnlyList<DumpKey>? _derived;
    private IReadOnlyList<string>? _createNames;
    private IReadOnlyList<string>? _derivedNames;

    public abstract Type ObjectType { get; }

    /// <summary>
    /// The extractor whose keys and writing this one extends, or null. Composition rather than CLR
    /// inheritance so a Beam extractor can reuse the Part one without inheriting its
    /// <see cref="ObjectType"/>.
    /// </summary>
    protected virtual ExtractorBase? Base => null;

    /// <summary>Keys this extractor adds to <c>create</c>, in the order they should be written.</summary>
    protected abstract IReadOnlyList<DumpKey> OwnCreate { get; }

    /// <summary>Keys this extractor adds to <c>derived</c>.</summary>
    protected abstract IReadOnlyList<DumpKey> OwnDerived { get; }

    public IReadOnlyList<DumpKey> Create => _create ??= Compose(Base?.Create, OwnCreate);

    public IReadOnlyList<DumpKey> Derived => _derived ??= Compose(Base?.Derived, OwnDerived);

    public IReadOnlyList<string> CreateKeys => _createNames ??= Create.Select(k => k.Name).ToArray();

    public IReadOnlyList<string> DerivedKeys => _derivedNames ??= Derived.Select(k => k.Name).ToArray();

    public void Write(ModelObject source, IDumpSink sink, DumpContext context)
    {
        sink.BeginSection("create");
        WriteCreate(source, sink, context);
        sink.EndSection();

        if (context.Options.IncludeDerived)
        {
            sink.BeginSection("derived");
            WriteDerived(source, sink, context);
            sink.EndSection();
        }
    }

    /// <summary>
    /// Writes this extractor's own create keys and then calls <c>Base?.WriteCreate</c>.
    /// </summary>
    /// <remarks>
    /// Own keys come FIRST, which is why this is not a base-call-first template method. A create
    /// block is ordered as you would actually write the code: the constructor, then the properties
    /// the object cannot exist without (a beam's two points), then the catalog properties any part
    /// has (profile, material, class). The concrete type owns the first two, and the inherited
    /// extractor owns the third.
    /// </remarks>
    protected internal virtual void WriteCreate(ModelObject source, IDumpSink sink, DumpContext context)
    {
    }

    protected internal virtual void WriteDerived(ModelObject source, IDumpSink sink, DumpContext context)
    {
    }

    private static IReadOnlyList<DumpKey> Compose(IReadOnlyList<DumpKey>? inherited, IReadOnlyList<DumpKey> own)
    {
        if (inherited is null || inherited.Count == 0) return own;

        // Own first, then inherited — the declared order IS the emitted order, and the emitted
        // order is what a reader turns back into code.
        var composed = new List<DumpKey>(inherited.Count + own.Count);
        composed.AddRange(own);
        // A subclass may restate a base key to move it or re-describe it; the base declaration is
        // then dropped so the key appears exactly once, where the subclass put it.
        var ownNames = new HashSet<string>(own.Select(k => k.Name), StringComparer.Ordinal);
        foreach (var key in inherited)
            if (!ownNames.Contains(key.Name)) composed.Add(key);
        return composed;
    }
}
