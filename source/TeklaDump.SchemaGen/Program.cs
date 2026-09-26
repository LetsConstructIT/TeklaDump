using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TeklaDump;
using TeklaDump.Extractors;
using TeklaDump.Json;

namespace TeklaDump.SchemaGen;

/// <summary>
/// Generates <c>schema/v1/*.schema.json</c> from the extractors' declared keys.
/// </summary>
/// <remarks>
/// The schema is GENERATED, committed, and checked for staleness in CI. Hand-editing it is not a
/// thing; editing an extractor is. That is what keeps the published schema a description of what
/// the code emits rather than of what someone believed it emitted.
/// <para>
/// Public, and the build methods with it, so the test suite can regenerate in-process and compare
/// against the committed files. A staleness check that shelled out to the exe would be a different
/// check on a different build.
/// </para>
/// </remarks>
public static class SchemaGenerator
{
    private const string SchemaDialect = "https://json-schema.org/draft/2020-12/schema";
    private const string BaseId = "https://raw.githubusercontent.com/LetsConstructIT/TeklaDump/main/schema/v1/";

    public static int Main(string[] args)
    {
        try
        {
            var outputDirectory = args.Length > 0
                ? args[0]
                : Path.Combine(FindRepositoryRoot(), "schema", "v1");

            Directory.CreateDirectory(outputDirectory);

            var extractors = ExtractorRegistry.DefaultExtractors();

            Write(Path.Combine(outputDirectory, "inspect.schema.json"), BuildInspectSchema(extractors));
            Write(Path.Combine(outputDirectory, "bulk.schema.json"), BuildBulkSchema(extractors));

            Console.WriteLine("Wrote inspect.schema.json and bulk.schema.json to " + outputDirectory);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Schema generation failed: " + ex.Message);
            return 1;
        }
    }

    public static void Write(string path, JsonValue schema)
    {
        // UTF-8 without BOM and "\n" endings: the committed file has to be byte-identical to what
        // CI regenerates, on a machine with any git autocrlf setting.
        var text = schema.ToIndentedString().Replace("\r\n", "\n") + "\n";
        File.WriteAllBytes(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text));
    }

    internal static JsonValue BuildInspectSchema(IReadOnlyList<IObjectExtractor> extractors)
    {
        var root = new JsonObject();
        root.Add("$schema", JsonValue.String(SchemaDialect));
        root.Add("$id", JsonValue.String(BaseId + "inspect.schema.json"));
        root.Add("title", JsonValue.String("TeklaDump inspect document " + SchemaVersion.Current));
        root.Add("description", JsonValue.String(
            "A curated view of a handful of Tekla model objects. Generated from the extractors' " +
            "declared keys; do not hand-edit."));
        root.Add("type", JsonValue.String("object"));
        root.Add("required", Strings("header", "objects"));

        var properties = new JsonObject();
        properties.Add("header", Ref("header"));

        var objects = new JsonObject();
        objects.Add("type", JsonValue.String("array"));
        objects.Add("items", Ref("record"));
        properties.Add("objects", objects);

        var warnings = new JsonObject();
        warnings.Add("type", JsonValue.String("array"));
        warnings.Add("items", Ref("warning"));
        properties.Add("warnings", warnings);

        root.Add("properties", properties);
        root.Add("additionalProperties", JsonValue.Bool(false));
        root.Add("$defs", BuildDefs(extractors, includeWarning: true));
        return root;
    }

    internal static JsonValue BuildBulkSchema(IReadOnlyList<IObjectExtractor> extractors)
    {
        var root = new JsonObject();
        root.Add("$schema", JsonValue.String(SchemaDialect));
        root.Add("$id", JsonValue.String(BaseId + "bulk.schema.json"));
        root.Add("title", JsonValue.String("TeklaDump bulk NDJSON " + SchemaVersion.Current));
        root.Add("description", JsonValue.String(
            "NDJSON, not a JSON document: line 1 validates against $defs/header and every " +
            "subsequent line against $defs/record. UTF-8 without BOM, \\n line endings. " +
            "Generated from the extractors' declared keys; do not hand-edit."));
        // A one-of at the top level is the closest a JSON Schema gets to describing a line-oriented
        // file: a validator is run per line, not over the whole file.
        var oneOf = new JsonArray();
        oneOf.Add(Ref("header"));
        oneOf.Add(Ref("record"));
        root.Add("oneOf", oneOf);
        root.Add("$defs", BuildDefs(extractors, includeWarning: false));
        return root;
    }

    private static JsonValue BuildDefs(IReadOnlyList<IObjectExtractor> extractors, bool includeWarning)
    {
        var defs = new JsonObject();
        defs.Add("point", PointDef());
        defs.Add("reference", ReferenceDef());
        defs.Add("templateValue", TemplateValueDef());
        defs.Add("header", HeaderDef());
        defs.Add("record", RecordDef(extractors));
        if (includeWarning) defs.Add("warning", WarningDef());
        return defs;
    }

    private static JsonValue PointDef()
    {
        var def = new JsonObject();
        def.Add("type", JsonValue.String("object"));
        def.Add("description", JsonValue.String(
            "A point in the CURRENT work plane of the session the dump was taken in. See the " +
            "header's workPlane field."));
        def.Add("required", Strings("x", "y", "z"));

        var properties = new JsonObject();
        properties.Add("x", Typed("number"));
        properties.Add("y", Typed("number"));
        properties.Add("z", Typed("number"));
        properties.Add("Chamfer", Typed("object"));
        def.Add("properties", properties);
        return def;
    }

    private static JsonValue ReferenceDef()
    {
        var def = new JsonObject();
        def.Add("type", JsonValue.String("object"));
        def.Add("description", JsonValue.String(
            "A related object, named and never followed. The GUID identifies an object in the " +
            "SOURCE model; it means nothing in a model the generated code runs against."));
        def.Add("required", Strings("objectType"));

        var properties = new JsonObject();
        properties.Add("objectType", Typed("string"));
        properties.Add("guid", Typed("string"));
        def.Add("properties", properties);
        return def;
    }

    private static JsonValue TemplateValueDef()
    {
        var def = new JsonObject();
        def.Add("description", JsonValue.String(
            "A template (report) value. A bare number is in the units the header declares. The " +
            "object form appears only for an attribute whose unit is known but not normalizable."));

        var oneOf = new JsonArray();
        oneOf.Add(Typed("string"));
        oneOf.Add(Typed("number"));

        var stamped = new JsonObject();
        stamped.Add("type", JsonValue.String("object"));
        stamped.Add("required", Strings("value", "unit"));
        var stampedProperties = new JsonObject();
        stampedProperties.Add("value", Typed("number"));
        stampedProperties.Add("unit", Typed("string"));
        stamped.Add("properties", stampedProperties);
        oneOf.Add(stamped);

        def.Add("oneOf", oneOf);
        return def;
    }

    private static JsonValue HeaderDef()
    {
        var def = new JsonObject();
        def.Add("type", JsonValue.String("object"));
        def.Add("description", JsonValue.String("Self-describing preamble; line 1 of a bulk file."));
        def.Add("required", Strings("schemaVersion", "domain", "tool", "generatedAt", "unitPolicy", "attributeTier"));

        var properties = new JsonObject();
        properties.Add("schemaVersion", Typed("string"));
        properties.Add("domain", Enum("model"));
        properties.Add("tool", Typed("string"));
        properties.Add("generatedAt", Typed("string"));
        properties.Add("teklaVersion", Typed("string"));
        properties.Add("buildNumber", Typed("string"));
        properties.Add("environment", Typed("string"));
        properties.Add("role", Typed("string"));
        properties.Add("modelName", Typed("string"));
        properties.Add("modelPath", Described(Typed("string"),
            "Only when IncludeSessionDetails is on. Off by default: an inspect file is meant to be pasted into a chat."));
        properties.Add("user", Described(Typed("string"), "The Tekla user, not the Windows account. Session-details gated."));
        properties.Add("currentPhase", Typed("integer"));
        properties.Add("sharedModel", Typed("boolean"));
        properties.Add("workPlane", Enum("global", "custom", "unknown"));
        properties.Add("workPlaneOrigin", Ref("point"));

        var axes = new JsonObject();
        axes.Add("type", JsonValue.String("array"));
        axes.Add("items", Ref("point"));
        properties.Add("workPlaneAxes", axes);

        properties.Add("numberingUpToDate", Described(Typed("boolean"),
            "False means position and mark attributes are empty or stale. There is no API to run numbering."));
        properties.Add("unitPolicy", Enum("normalized", "native"));
        properties.Add("units", Typed("object"));
        properties.Add("attributeTier", Enum("T0", "T1", "T2"));

        var attributeSet = new JsonObject();
        attributeSet.Add("type", JsonValue.String("array"));
        attributeSet.Add("items", Typed("string"));
        properties.Add("attributeSet", attributeSet);

        properties.Add("objectCount", Described(Typed("integer"),
            "Present only when the count was known before writing started."));

        def.Add("properties", properties);
        return def;
    }

    private static JsonValue WarningDef()
    {
        var def = new JsonObject();
        def.Add("type", JsonValue.String("object"));
        def.Add("required", Strings("code", "message"));
        var properties = new JsonObject();
        properties.Add("code", Typed("string"));
        properties.Add("message", Typed("string"));
        properties.Add("objectType", Typed("string"));
        properties.Add("guid", Typed("string"));
        def.Add("properties", properties);
        return def;
    }

    /// <summary>
    /// One record. The per-type create/derived shapes are attached as conditional subschemas keyed
    /// on <c>objectType</c>, so a validator checks a Beam's create block against the Beam keys and
    /// says nothing about types it has never heard of.
    /// </summary>
    private static JsonValue RecordDef(IReadOnlyList<IObjectExtractor> extractors)
    {
        var def = new JsonObject();
        def.Add("type", JsonValue.String("object"));
        def.Add("required", Strings("objectType"));

        var properties = new JsonObject();
        properties.Add("objectType", Described(Typed("string"), "The CLR simple name of the Tekla type."));
        properties.Add("guid", Described(Typed("string"),
            "Stable identity. Absent for an uninserted object, which has none."));
        properties.Add("create", Described(Typed("object"),
            "Properties that can be assigned when recreating the object. A starting point for code you write, not an importer payload."));
        properties.Add("derived", Described(Typed("object"),
            "Strictly informational. Assigning any of these is either impossible or a mistake."));
        properties.Add("userProperties", Described(Typed("object"), "UDAs, sorted by name."));

        var templateAttributes = new JsonObject();
        templateAttributes.Add("type", JsonValue.String("object"));
        templateAttributes.Add("additionalProperties", Ref("templateValue"));
        templateAttributes.Add("description", JsonValue.String("Template (report) attributes, verbatim names, sorted."));
        properties.Add("templateAttributes", templateAttributes);

        properties.Add("componentAttributes", Described(Typed("object"),
            "Component attributes the user actually SET. An attribute the user never set is omitted, because the component is using its own default."));

        def.Add("properties", properties);

        var allOf = new JsonArray();
        foreach (var extractor in extractors)
            allOf.Add(TypeCase(extractor));
        def.Add("allOf", allOf);

        return def;
    }

    /// <summary>
    /// <c>if objectType == X then create/derived have these keys</c>. Only the DECLARED keys are
    /// listed and nothing is forbidden: a subtype with no extractor of its own resolves to its
    /// base and legitimately carries the base's keys under its own objectType.
    /// </summary>
    private static JsonValue TypeCase(IObjectExtractor extractor)
    {
        var typeName = extractor.ObjectType.Name;

        var condition = new JsonObject();
        var conditionProperties = new JsonObject();
        conditionProperties.Add("objectType", Enum(typeName));
        condition.Add("properties", conditionProperties);
        condition.Add("required", Strings("objectType"));

        var then = new JsonObject();
        var thenProperties = new JsonObject();
        thenProperties.Add("create", KeyBlock(extractor.Create));
        thenProperties.Add("derived", KeyBlock(extractor.Derived));
        then.Add("properties", thenProperties);

        var result = new JsonObject();
        result.Add("if", condition);
        result.Add("then", then);
        return result;
    }

    private static JsonValue KeyBlock(IReadOnlyList<DumpKey> keys)
    {
        var block = new JsonObject();
        block.Add("type", JsonValue.String("object"));

        var properties = new JsonObject();
        foreach (var key in keys)
        {
            var schema = SchemaFor(key.Type);
            var description = key.Description;
            if (key.Since is not null)
            {
                description = (description is null ? string.Empty : description + " ") +
                              "Present on Tekla " + key.Since + " and newer.";
            }
            properties.Add(key.Name, description is null ? schema : Described(schema, description));
        }

        block.Add("properties", properties);
        return block;
    }

    private static JsonValue SchemaFor(DumpKeyType type)
    {
        switch (type)
        {
            case DumpKeyType.String: return Typed("string");
            case DumpKeyType.Number: return Typed("number");
            case DumpKeyType.Integer: return Typed("integer");
            case DumpKeyType.Boolean: return Typed("boolean");
            case DumpKeyType.Point: return Ref("point");
            case DumpKeyType.PointArray: return ArrayOf(Ref("point"));
            case DumpKeyType.Reference: return Ref("reference");
            case DumpKeyType.ReferenceArray: return ArrayOf(Ref("reference"));
            case DumpKeyType.Object: return Typed("object");
            default: return Typed("array");
        }
    }

    private static JsonValue ArrayOf(JsonValue items)
    {
        var value = new JsonObject();
        value.Add("type", JsonValue.String("array"));
        value.Add("items", items);
        return value;
    }

    private static JsonValue Typed(string type)
    {
        var value = new JsonObject();
        value.Add("type", JsonValue.String(type));
        return value;
    }

    private static JsonValue Described(JsonValue schema, string description)
    {
        if (schema is not JsonObject obj) return schema;
        obj.Add("description", JsonValue.String(description));
        return obj;
    }

    private static JsonValue Enum(params string[] values)
    {
        var value = new JsonObject();
        value.Add("type", JsonValue.String("string"));
        value.Add("enum", Strings(values));
        return value;
    }

    private static JsonValue Ref(string name)
    {
        var value = new JsonObject();
        value.Add("$ref", JsonValue.String("#/$defs/" + name));
        return value;
    }

    private static JsonValue Strings(params string[] values)
    {
        var array = new JsonArray();
        foreach (var value in values) array.Add(JsonValue.String(value));
        return array;
    }

    /// <summary>Walks up from the running assembly to the folder holding the .slnx.</summary>
    public static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TeklaDump.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Could not find the repository root (no TeklaDump.slnx above " +
            AppDomain.CurrentDomain.BaseDirectory + "). Pass the output directory as an argument.");
    }
}
