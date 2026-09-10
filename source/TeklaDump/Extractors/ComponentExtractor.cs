using System;
using System.Collections;
using System.Collections.Generic;
using Tekla.Structures;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;
using TeklaDump.Attributes;
using TeklaDump.Json;
using TeklaDump.Sinks;
using TeklaDump.Values;

namespace TeklaDump.Extractors;

/// <summary>
/// A component — the record the course demo is built on, and the one that is different in kind
/// from every other.
/// </summary>
/// <remarks>
/// A component's create block is not a list of properties. It is: which component
/// (<c>Number</c> / <c>Name</c>), what it was applied to (the INPUT), and which attributes the
/// user actually SET.
/// <para>
/// The last part is the subtle one. <c>GetAttribute</c> returns <c>false</c> for an attribute the
/// user never set, meaning the component is using its own internal default. Those are omitted on
/// purpose: they are exactly the set a regenerating script should not assign, because assigning
/// them pins today's default into tomorrow's model. The names come from
/// <see cref="ComponentAttributeCatalog"/>, since no API lists them.
/// </para>
/// </remarks>
internal class ComponentExtractor : ExtractorBase
{
    public override Type ObjectType => typeof(BaseComponent);

    protected override ExtractorBase? Base => ModelObject;

    protected override IReadOnlyList<DumpKey> OwnCreate => new[]
    {
        new DumpKey("$ctor", DumpKeyType.String, "Constructor hint; an opaque string."),
        new DumpKey("Number", DumpKeyType.Integer,
            "System component number, e.g. 141. -100000 (PLUGIN_OBJECT_NUMBER) means a plugin, identified by Name instead."),
        new DumpKey("Name", DumpKeyType.String, "Component or plugin name."),
        new DumpKey("Input", DumpKeyType.Array,
            "Resolved component input: one entry per input item, each with its inputType and either objects (references) or points."),
    };

    protected override IReadOnlyList<DumpKey> OwnDerived => NoKeysStatic;

    protected internal override void WriteCreate(ModelObject source, IDumpSink sink, DumpContext context)
    {
        var component = (BaseComponent)source;

        sink.Write("$ctor", JsonValue.String("new " + source.GetType().Name + "()"));

        var number = context.TryValue(() => component.Number);
        if (number.HasValue) sink.Write("Number", JsonValue.Number(number.Value));
        sink.Write("Name", ValueCoercion.Text(context.Try(() => component.Name)));

        if (component is Component plugin)
            sink.Write("Input", InputValue(context.Try(() => plugin.GetComponentInput()), context));

        Base?.WriteCreate(source, sink, context);
    }

    protected internal override void WriteDerived(ModelObject source, IDumpSink sink, DumpContext context)
    {
        Base?.WriteDerived(source, sink, context);
    }

    /// <summary>
    /// Writes the <c>componentAttributes</c> section: every discovered name whose
    /// <c>GetAttribute</c> answers true.
    /// </summary>
    /// <remarks>
    /// Called by <see cref="DumpWriter"/> rather than from <see cref="WriteCreate"/> so it lands in
    /// its own section, next to <c>userProperties</c> and <c>templateAttributes</c> — three
    /// name/value maps that a reader treats the same way.
    /// </remarks>
    public static void WriteComponentAttributes(BaseComponent component, IDumpSink sink, DumpContext context)
    {
        var mode = context.Options.ComponentAttributes;
        if (mode == ComponentAttributeMode.None) return;

        var names = ResolveNames(component, context, mode);
        if (names.Count == 0) return;

        var opened = false;
        foreach (var attribute in names)
        {
            var value = ReadAttribute(component, attribute);
            if (value is null) continue;   // not set: the component is using its own default

            if (!opened)
            {
                sink.BeginSection("componentAttributes");
                opened = true;
            }

            sink.Write(attribute.Name, value);
        }

        if (opened) sink.EndSection();
    }

    private static IReadOnlyList<ComponentAttributeName> ResolveNames(
        BaseComponent component, DumpContext context, ComponentAttributeMode mode)
    {
        if (mode == ComponentAttributeMode.Explicit)
        {
            var explicitNames = context.Options.ComponentAttributeNames;
            if (explicitNames is null || explicitNames.Count == 0) return EmptyNames;

            var resolved = new List<ComponentAttributeName>(explicitNames.Count);
            foreach (var name in explicitNames)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                // No file to guess a type from, so all three overloads are tried in turn.
                resolved.Add(new ComponentAttributeName(name.Trim(), ComponentValueType.Integer));
            }

            return resolved;
        }

        var number = context.TryValue(() => component.Number);
        var componentName = context.Try(() => component.Name);

        // A plugin has no meaningful number — it is PLUGIN_OBJECT_NUMBER for all of them — so its
        // attribute files are named after the plugin instead.
        var isPlugin = !number.HasValue || number.Value == BaseComponent.PLUGIN_OBJECT_NUMBER;

        var names = isPlugin
            ? (string.IsNullOrWhiteSpace(componentName)
                ? EmptyNames
                : context.ComponentCatalog.ForPlugin(componentName!))
            : context.ComponentCatalog.ForNumber(number!.Value);

        if (names.Count == 0)
        {
            var label = isPlugin ? "plugin '" + componentName + "'" : "component " + number;
            context.WarnOnce(
                "component-attrs-" + label,
                "component-attributes-not-discovered",
                "No saved attribute file was found for " + label + ", so its attribute names are unknown. " +
                "Pass the names explicitly (--component-attrs) if you need them.");
        }

        return names;
    }

    /// <summary>
    /// Reads one attribute, trying the typed overloads in the order the saved literal suggests and
    /// falling through on <c>false</c>. Null means the attribute is not set on this component.
    /// </summary>
    private static JsonValue? ReadAttribute(BaseComponent component, ComponentAttributeName attribute)
    {
        foreach (var type in OrderFor(attribute.ValueType))
        {
            try
            {
                switch (type)
                {
                    case ComponentValueType.Integer:
                        var intValue = 0;
                        if (component.GetAttribute(attribute.Name, ref intValue))
                            return JsonValue.Number(intValue);
                        break;

                    case ComponentValueType.Double:
                        var doubleValue = 0d;
                        if (component.GetAttribute(attribute.Name, ref doubleValue))
                            return JsonValue.Number(doubleValue);
                        break;

                    default:
                        var stringValue = string.Empty;
                        if (component.GetAttribute(attribute.Name, ref stringValue))
                            return ValueCoercion.Text(stringValue);
                        break;
                }
            }
            catch (Exception)
            {
                // A wrong-typed read can throw rather than return false on some versions; treat it
                // the same as a miss and try the next overload.
            }
        }

        return null;
    }

    /// <summary>
    /// Up to three interop calls per name, so the guessed type goes first and the other two follow.
    /// This is the cost that makes component attributes an inspect-mode default and a bulk-mode
    /// opt-in.
    /// </summary>
    private static IEnumerable<ComponentValueType> OrderFor(ComponentValueType first)
    {
        yield return first;
        if (first != ComponentValueType.Double) yield return ComponentValueType.Double;
        if (first != ComponentValueType.String) yield return ComponentValueType.String;
        if (first != ComponentValueType.Integer) yield return ComponentValueType.Integer;
    }

    /// <summary>
    /// The component input, resolved through <c>InputItem.GetData()</c>. Object inputs become
    /// references; point inputs stay points.
    /// </summary>
    private static JsonValue? InputValue(ComponentInput? input, DumpContext context)
    {
        if (input is null) return null;

        var items = new JsonArray();
        foreach (var element in input)
        {
            if (element is not InputItem item) continue;

            var entry = new JsonObject();
            var inputType = context.TryValue(() => item.GetInputType());
            if (inputType.HasValue) entry.Add("inputType", JsonValue.String(inputType.Value.ToString()));

            var data = context.Try(() => item.GetData());
            AddData(entry, data, context);

            if (entry.Count > 0) items.Add(entry);
        }

        return items.Count == 0 ? null : items;
    }

    private static void AddData(JsonObject entry, object? data, DumpContext context)
    {
        switch (data)
        {
            case null:
                return;

            case ModelObject modelObject:
                entry.AddIfPresent("objects", Single(context.Reference(modelObject)));
                return;

            case Point point:
                entry.AddIfPresent("points", Single(ValueCoercion.Point(point)));
                return;

            case Identifier identifier:
                entry.AddIfPresent("objects", Single(ReferenceTo(identifier, context)));
                return;

            case IEnumerable list:
                var objects = new JsonArray();
                var points = new JsonArray();
                foreach (var element in list)
                {
                    switch (element)
                    {
                        case ModelObject modelObject:
                            var reference = context.Reference(modelObject);
                            if (reference is not null) objects.Add(reference);
                            break;
                        case ContourPoint contour:
                            points.Add(ValueCoercion.ContourPointValue(contour));
                            break;
                        case Point point:
                            points.Add(ValueCoercion.Point3(point.X, point.Y, point.Z));
                            break;
                        case Identifier identifier:
                            var resolved = ReferenceTo(identifier, context);
                            if (resolved is not null) objects.Add(resolved);
                            break;
                    }
                }

                if (objects.Count > 0) entry.Add("objects", objects);
                if (points.Count > 0) entry.Add("points", points);
                return;
        }
    }

    /// <summary>
    /// A reference built from a bare identifier. Some versions hand back <c>Identifier</c>s rather
    /// than objects; the type is then unknown, and resolving one to a live object would mean
    /// selecting from the model, which the library does not do.
    /// </summary>
    private static JsonValue? ReferenceTo(Identifier identifier, DumpContext context)
    {
        var guid = ValueCoercion.GuidOf(identifier) ?? context.Session?.ResolveGuid(identifier);
        if (guid is null) return null;

        var reference = new JsonObject();
        reference.Add("objectType", JsonValue.String("ModelObject"));
        reference.Add("guid", JsonValue.String(guid));
        return reference;
    }

    private static JsonValue? Single(JsonValue? value)
    {
        if (value is null) return null;
        var array = new JsonArray();
        array.Add(value);
        return array;
    }

    protected static readonly ModelObjectExtractor ModelObject = new ModelObjectExtractor();
    private static readonly DumpKey[] NoKeysStatic = new DumpKey[0];
    private static readonly ComponentAttributeName[] EmptyNames = new ComponentAttributeName[0];
}

/// <summary>A connection: a component applied to a primary object plus secondaries.</summary>
internal sealed class ConnectionExtractor : ExtractorBase
{
    public override Type ObjectType => typeof(Connection);

    protected override ExtractorBase? Base => Component;

    protected override IReadOnlyList<DumpKey> OwnCreate => new[]
    {
        new DumpKey("PrimaryObject", DumpKeyType.Reference, "The part the connection was applied to first."),
        new DumpKey("SecondaryObjects", DumpKeyType.ReferenceArray, "The parts connected to the primary."),
        new DumpKey("UpVector", DumpKeyType.Point, "Up direction, which decides the connection's orientation."),
        new DumpKey("PositionType", DumpKeyType.String, "PositionTypeEnum name."),
        new DumpKey("AutoDirectionType", DumpKeyType.String, "AutoDirectionTypeEnum name."),
        new DumpKey("Class", DumpKeyType.Integer, "Connection class."),
        new DumpKey("Code", DumpKeyType.String, "Connection code."),
    };

    protected override IReadOnlyList<DumpKey> OwnDerived => new[]
    {
        new DumpKey("Status", DumpKeyType.String,
            "StatusEnum name: whether the connection last built cleanly. Derived — it is Tekla's verdict, not an input."),
    };

    protected internal override void WriteCreate(ModelObject source, IDumpSink sink, DumpContext context)
    {
        var connection = (Connection)source;

        sink.Write("PrimaryObject", context.Reference(context.Try(() => connection.GetPrimaryObject())));
        sink.Write("SecondaryObjects", context.References(context.Try(() => connection.GetSecondaryObjects())));
        sink.Write("UpVector", ValueCoercion.Vector(context.Try(() => connection.UpVector)));
        ComponentShared.WriteEnum(sink, "PositionType", context.TryValue(() => connection.PositionType));
        ComponentShared.WriteEnum(sink, "AutoDirectionType", context.TryValue(() => connection.AutoDirectionType));

        var connectionClass = context.TryValue(() => connection.Class);
        if (connectionClass.HasValue) sink.Write("Class", JsonValue.Number(connectionClass.Value));
        sink.Write("Code", ValueCoercion.Text(context.Try(() => connection.Code)));

        Base?.WriteCreate(source, sink, context);
    }

    protected internal override void WriteDerived(ModelObject source, IDumpSink sink, DumpContext context)
    {
        var connection = (Connection)source;
        ComponentShared.WriteEnum(sink, "Status", context.TryValue(() => connection.Status));
        Base?.WriteDerived(source, sink, context);
    }

    private static readonly ComponentExtractor Component = new ComponentExtractor();
}

/// <summary>A detail: a component applied to one primary object at a point.</summary>
internal sealed class DetailExtractor : ExtractorBase
{
    public override Type ObjectType => typeof(Detail);

    protected override ExtractorBase? Base => Component;

    protected override IReadOnlyList<DumpKey> OwnCreate => new[]
    {
        new DumpKey("PrimaryObject", DumpKeyType.Reference, "The part the detail was applied to."),
        new DumpKey("ReferencePoint", DumpKeyType.Point, "Where on the part the detail sits."),
        new DumpKey("DetailType", DumpKeyType.String, "DetailTypeEnum name: which end or side."),
        new DumpKey("UpVector", DumpKeyType.Point, "Up direction."),
        new DumpKey("PositionType", DumpKeyType.String, "PositionTypeEnum name."),
        new DumpKey("AutoDirectionType", DumpKeyType.String, "AutoDirectionTypeEnum name."),
        new DumpKey("Class", DumpKeyType.Integer, "Detail class."),
        new DumpKey("Code", DumpKeyType.String, "Detail code."),
    };

    protected override IReadOnlyList<DumpKey> OwnDerived => new[]
    {
        new DumpKey("Status", DumpKeyType.String, "StatusEnum name; Tekla's verdict on the last build."),
    };

    protected internal override void WriteCreate(ModelObject source, IDumpSink sink, DumpContext context)
    {
        var detail = (Detail)source;

        sink.Write("PrimaryObject", context.Reference(context.Try(() => detail.GetPrimaryObject())));
        sink.Write("ReferencePoint", ValueCoercion.Point(context.Try(() => detail.GetReferencePoint())));
        ComponentShared.WriteEnum(sink, "DetailType", context.TryValue(() => detail.DetailType));
        sink.Write("UpVector", ValueCoercion.Vector(context.Try(() => detail.UpVector)));
        ComponentShared.WriteEnum(sink, "PositionType", context.TryValue(() => detail.PositionType));
        ComponentShared.WriteEnum(sink, "AutoDirectionType", context.TryValue(() => detail.AutoDirectionType));

        var detailClass = context.TryValue(() => detail.Class);
        if (detailClass.HasValue) sink.Write("Class", JsonValue.Number(detailClass.Value));
        sink.Write("Code", ValueCoercion.Text(context.Try(() => detail.Code)));

        Base?.WriteCreate(source, sink, context);
    }

    protected internal override void WriteDerived(ModelObject source, IDumpSink sink, DumpContext context)
    {
        var detail = (Detail)source;
        ComponentShared.WriteEnum(sink, "Status", context.TryValue(() => detail.Status));
        Base?.WriteDerived(source, sink, context);
    }

    private static readonly ComponentExtractor Component = new ComponentExtractor();
}

/// <summary>A seam: a component applied along a line or a polygon between two parts.</summary>
internal sealed class SeamExtractor : ExtractorBase
{
    public override Type ObjectType => typeof(Seam);

    protected override ExtractorBase? Base => Component;

    protected override IReadOnlyList<DumpKey> OwnCreate => new[]
    {
        new DumpKey("PrimaryObject", DumpKeyType.Reference, "The part the seam was applied to first."),
        new DumpKey("SecondaryObjects", DumpKeyType.ReferenceArray, "The parts seamed to the primary."),
        new DumpKey("StartPosition", DumpKeyType.Point, "Start of the seam line."),
        new DumpKey("EndPosition", DumpKeyType.Point, "End of the seam line."),
        new DumpKey("InputPolygon", DumpKeyType.PointArray, "The polygon input, for a polygon seam."),
        new DumpKey("UpVector", DumpKeyType.Point, "Up direction."),
        new DumpKey("AutoPosition", DumpKeyType.Boolean, "Whether Tekla positions the seam itself."),
        new DumpKey("AutoDirectionType", DumpKeyType.String, "AutoDirectionTypeEnum name."),
        new DumpKey("Class", DumpKeyType.Integer, "Seam class."),
        new DumpKey("Code", DumpKeyType.String, "Seam code."),
    };

    protected override IReadOnlyList<DumpKey> OwnDerived => new[]
    {
        new DumpKey("Status", DumpKeyType.String, "StatusEnum name; Tekla's verdict on the last build."),
    };

    protected internal override void WriteCreate(ModelObject source, IDumpSink sink, DumpContext context)
    {
        var seam = (Seam)source;

        sink.Write("PrimaryObject", context.Reference(context.Try(() => seam.GetPrimaryObject())));
        sink.Write("SecondaryObjects", context.References(context.Try(() => seam.GetSecondaryObjects())));

        var positions = context.Try(() =>
        {
            var start = new Point();
            var end = new Point();
            return seam.GetStartAndEndPositions(ref start, ref end) ? new[] { start, end } : null;
        });
        if (positions is not null)
        {
            sink.Write("StartPosition", ValueCoercion.Point(positions[0]));
            sink.Write("EndPosition", ValueCoercion.Point(positions[1]));
        }

        var polygon = context.Try(() => seam.GetInputPolygon());
        sink.Write("InputPolygon", polygon is null ? null : ValueCoercion.Points(polygon.Points));

        sink.Write("UpVector", ValueCoercion.Vector(context.Try(() => seam.UpVector)));

        var autoPosition = context.TryValue(() => seam.AutoPosition);
        if (autoPosition.HasValue) sink.Write("AutoPosition", JsonValue.Bool(autoPosition.Value));

        ComponentShared.WriteEnum(sink, "AutoDirectionType", context.TryValue(() => seam.AutoDirectionType));

        var seamClass = context.TryValue(() => seam.Class);
        if (seamClass.HasValue) sink.Write("Class", JsonValue.Number(seamClass.Value));
        sink.Write("Code", ValueCoercion.Text(context.Try(() => seam.Code)));

        Base?.WriteCreate(source, sink, context);
    }

    protected internal override void WriteDerived(ModelObject source, IDumpSink sink, DumpContext context)
    {
        var seam = (Seam)source;
        ComponentShared.WriteEnum(sink, "Status", context.TryValue(() => seam.Status));
        Base?.WriteDerived(source, sink, context);
    }

    private static readonly ComponentExtractor Component = new ComponentExtractor();
}

/// <summary>Small helpers the three component-shaped extractors share.</summary>
internal static class ComponentShared
{
    public static void WriteEnum<T>(IDumpSink sink, string key, T? value) where T : struct, Enum
    {
        if (value.HasValue) sink.Write(key, JsonValue.String(value.Value.ToString()));
    }
}
