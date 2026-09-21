using System;
using System.Collections.Generic;
using System.Threading;
using TeklaDump.Attributes;

namespace TeklaDump;

/// <summary>How much of an object's user-defined attributes (UDAs) to read.</summary>
public enum UserPropertyMode
{
    /// <summary>Read none. One fewer interop call per object.</summary>
    None,

    /// <summary>
    /// Everything <c>GetAllUserProperties</c> reports. Cheap (a single call) and the UDAs are
    /// usually the reason someone dumps an object at all.
    /// </summary>
    All,
}

/// <summary>Where the names for <c>BaseComponent.GetAttribute</c> come from.</summary>
/// <remarks>
/// There is no API that enumerates a component's attributes, so the name universe has to be
/// discovered from the environment's saved attribute files. See
/// <see cref="ComponentAttributeCatalog"/>.
/// </remarks>
public enum ComponentAttributeMode
{
    /// <summary>Read no component attributes.</summary>
    None,

    /// <summary>Discover names from the environment's <c>*.j&lt;N&gt;</c> / plugin attribute files.</summary>
    Discovered,

    /// <summary>Read exactly <see cref="DumpOptions.ComponentAttributeNames"/> and nothing else.</summary>
    Explicit,
}

/// <summary>How much geometry an extractor emits.</summary>
public enum GeometryDetail
{
    /// <summary>No geometry beyond what a create block needs.</summary>
    None,

    /// <summary>Defining points — start/end, contour, polygon. The default.</summary>
    Points,

    /// <summary>
    /// Also <c>GetSolid()</c> face geometry. Orders of magnitude more expensive than it looks:
    /// the solid is COMPUTED, not read, and this alone turns minutes into never on 100k parts.
    /// </summary>
    Solids,
}

/// <summary>Whether values are converted to one declared unit system or passed through untouched.</summary>
public enum UnitPolicy
{
    /// <summary>
    /// mm, mm2, mm3, kg, degrees — declared ONCE in the header's <c>units</c> block, with bare
    /// numbers everywhere else. Tekla's report values are not uniformly scaled (AREA comes back in
    /// m2 while VOLUME comes back in mm3), so "raw" is not a coherent unit system.
    /// </summary>
    Normalized,

    /// <summary>Untouched Tekla values, no <c>units</c> block, no stamps. The user asked for raw.</summary>
    Native,
}

/// <summary>
/// How much of the environment's template (report) attribute list to read for one object.
/// </summary>
/// <remarks>
/// The tiers exist because cost is wildly uneven. A group that names another full model object
/// (<c>MAIN_PART</c>, <c>CAST_UNIT</c>, <c>ASSEMBLY</c>) re-exposes that object's entire attribute
/// set — hundreds of names, each needing Tekla to resolve and evaluate a different object. A group
/// that names a constituent (<c>NUT</c>, <c>WASHER</c>, <c>PROFILE</c>) contributes a couple of
/// dozen. Reading the second kind costs about what reading the object itself costs; reading the
/// first is what turns a dump into a hang.
/// </remarks>
public enum TemplateAttributeScope
{
    /// <summary>Read none. The default for a dump — attributes are opt-in.</summary>
    None,

    /// <summary>
    /// The object's own attributes plus its constituent groups — a bolt's nut, washer and hole; a
    /// part's profile and material.
    /// </summary>
    Associated,

    /// <summary>
    /// Everything, including groups that traverse to other model objects. Complete, and slow
    /// enough to be an explicit choice — see <see cref="DumpOptions.MaxTemplateAttributes"/>.
    /// </summary>
    Full,
}

/// <summary>
/// Knobs for a single <see cref="DumpWriter"/> run.
/// </summary>
public sealed class DumpOptions
{
    /// <summary>
    /// Fresh defaults. Deliberately a new instance each time rather than a shared singleton — the
    /// object is mutable, and a shared one would let any caller change every other caller's run.
    /// </summary>
    public static DumpOptions Default => new DumpOptions();

    public UserPropertyMode UserProperties { get; set; } = UserPropertyMode.All;

    /// <summary>
    /// How much of the environment's template (report) attribute list to read. Off by default:
    /// it is the one knob that can turn a fast dump into a slow one.
    /// </summary>
    public TemplateAttributeScope TemplateAttributes { get; set; } = TemplateAttributeScope.None;

    /// <summary>Explicit attribute names. Wins over <see cref="TemplateAttributes"/> when set.</summary>
    public IReadOnlyList<string>? TemplateAttributeNames { get; set; }

    public ComponentAttributeMode ComponentAttributes { get; set; } = ComponentAttributeMode.Discovered;

    /// <summary>
    /// Attribute names for <see cref="ComponentAttributeMode.Explicit"/> — the escape hatch for a
    /// plugin whose attribute files are nowhere on the search path.
    /// </summary>
    public IReadOnlyList<string>? ComponentAttributeNames { get; set; }

    public GeometryDetail Geometry { get; set; } = GeometryDetail.Points;

    public UnitPolicy Units { get; set; } = UnitPolicy.Normalized;

    /// <summary>False emits the <c>create</c> block only — the smallest useful document.</summary>
    public bool IncludeDerived { get; set; } = true;

    /// <summary>
    /// Adds <c>modelPath</c> and the Tekla user name to the header. Off by default because an
    /// inspect file is meant to be pasted into a chat, and a customer's folder path and a user
    /// name do not belong in one. The CLI turns it on for <c>bulk</c>.
    /// </summary>
    public bool IncludeSessionDetails { get; set; } = false;

    /// <summary>Recursion ceiling for the inspect-mode reflection fallback.</summary>
    public int MaxDepth { get; set; } = 8;

    /// <summary>Collection-length ceiling for the inspect-mode reflection fallback.</summary>
    public int MaxItems { get; set; } = 2000;

    /// <summary>
    /// Ceiling on template attributes read for one object. Guards the pathological case under
    /// <see cref="TemplateAttributeScope.Full"/>: a connection declares over 13,000 attributes once
    /// related objects are in scope, fetched in ONE blocking interop call that cancellation cannot
    /// interrupt.
    /// </summary>
    public int MaxTemplateAttributes { get; set; } = 2000;

    /// <summary>
    /// Object count above which template attributes are read through a whole-model report join
    /// (T2) instead of per object (T1). See the schema README, "Attribute tiers".
    /// </summary>
    public int ReportJoinThreshold { get; set; } = 5000;

    /// <summary>Optional progress reports, raised between objects.</summary>
    public IProgress<DumpProgress>? Progress { get; set; }

    /// <summary>
    /// Honoured between objects and between batches. It cannot interrupt a single interop call —
    /// neither a <see cref="TemplateAttributeScope.Full"/> read nor a report generation — and the
    /// docs say so rather than pretending otherwise.
    /// </summary>
    public CancellationToken Cancellation { get; set; }

    /// <summary>Shallow copy, so a caller's instance is never mutated by a run.</summary>
    internal DumpOptions Clone() => (DumpOptions)MemberwiseClone();
}
