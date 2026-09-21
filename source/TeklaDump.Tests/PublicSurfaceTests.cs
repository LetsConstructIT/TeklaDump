using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Xunit;

namespace TeklaDump.Tests;

/// <summary>
/// The published API surface must equal what is committed in <c>source/TeklaDump/PublicSurface.txt</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every public type in <c>TeklaDump.dll</c> is a compatibility promise the moment 1.0 ships, and
/// the surface is small ON PURPOSE: the extractor contract, the sinks, the dump context and the
/// session reader are implementation, not API. Without a gate, a type drifts to <c>public</c> in a
/// refactor and nobody notices until removing it is a breaking change.
/// </para>
/// <para>
/// When this fails, decide which it is. <b>An addition you meant</b> — regenerate the baseline
/// (set <c>TEKLADUMP_APPROVE_PUBLIC_SURFACE=1</c> and run the suite), commit it, and add a
/// CHANGELOG entry. <b>An accidental leak</b> — mark the type <c>internal</c>. Note the asymmetry
/// that makes the default obvious: opening a type later is a non-breaking change, closing one is
/// not, so when in doubt leave it internal.
/// </para>
/// <para>
/// The rendering covers types, members, signatures and enum values. It deliberately does NOT carry
/// nullable reference annotations: those are worth knowing about but noisy to diff, and every drift
/// this gate exists to catch — a type or member appearing, vanishing or changing shape — shows up
/// without them.
/// </para>
/// </remarks>
public class PublicSurfaceTests
{
    private const string ApproveVariable = "TEKLADUMP_APPROVE_PUBLIC_SURFACE";

    [Fact]
    public void The_published_surface_is_what_is_committed()
    {
        var baselinePath = Path.Combine(Fixtures.Root, "source", "TeklaDump", "PublicSurface.txt");
        var actual = PublicSurface.Render(typeof(DumpWriter).Assembly);

        if (Environment.GetEnvironmentVariable(ApproveVariable) == "1")
            File.WriteAllText(baselinePath, actual, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        Assert.True(File.Exists(baselinePath), baselinePath + " is missing. Regenerate it: set " +
            ApproveVariable + "=1 and run the suite.");

        var committed = File.ReadAllText(baselinePath).Replace("\r\n", "\n");
        if (committed == actual) return;

        var expectedLines = new HashSet<string>(committed.Split('\n'), StringComparer.Ordinal);
        var actualLines = new HashSet<string>(actual.Split('\n'), StringComparer.Ordinal);

        var added = actualLines.Except(expectedLines, StringComparer.Ordinal)
            .OrderBy(line => line, StringComparer.Ordinal).ToList();
        var removed = expectedLines.Except(actualLines, StringComparer.Ordinal)
            .OrderBy(line => line, StringComparer.Ordinal).ToList();

        var message = new StringBuilder();
        message.Append("The published API surface changed.\n\n");
        foreach (var line in added) message.Append("  + ").Append(line).Append('\n');
        foreach (var line in removed) message.Append("  - ").Append(line).Append('\n');
        message.Append(
            "\nIf this was deliberate, regenerate the baseline (set " + ApproveVariable +
            "=1 and run the suite), commit it, and record it in the CHANGELOG. If a type leaked " +
            "out by accident, mark it internal — opening a type later is non-breaking, closing " +
            "one is not.");

        Assert.Fail(message.ToString());
    }
}

/// <summary>
/// Renders an assembly's externally visible surface as sorted, diffable text.
/// </summary>
/// <remarks>
/// Lines are fully qualified and sorted ordinally, which groups every member under its own type
/// without the renderer having to track nesting: <c>"TeklaDump.DumpOptions "</c> sorts before
/// <c>"TeklaDump.DumpOptions."</c> because space precedes the dot. Modifiers go at the END of a
/// line for the same reason — a leading <c>static</c> would scatter a type's members.
/// </remarks>
internal static class PublicSurface
{
    private const BindingFlags DeclaredMembers =
        BindingFlags.Public | BindingFlags.NonPublic |
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    public static string Render(Assembly assembly)
    {
        var lines = new List<string>();

        foreach (var type in assembly.GetExportedTypes())
        {
            lines.Add(DescribeType(type));
            lines.AddRange(DescribeMembers(type));
        }

        lines.Sort(StringComparer.Ordinal);
        return string.Join("\n", lines) + "\n";
    }

    private static string DescribeType(Type type)
    {
        var kind =
            type.IsEnum ? "enum" :
            type.IsInterface ? "interface" :
            type.IsValueType ? "struct" :
            type.IsAbstract && type.IsSealed ? "static class" :
            type.IsAbstract ? "abstract class" :
            type.IsSealed ? "sealed class" : "class";

        var bases = new List<string>();

        if (type.IsEnum)
        {
            bases.Add(Name(Enum.GetUnderlyingType(type)));
        }
        else
        {
            if (type.BaseType is not null && type.BaseType != typeof(object) && type.BaseType != typeof(ValueType))
                bases.Add(Name(type.BaseType));

            bases.AddRange(type.GetInterfaces()
                .Where(contract => contract.IsVisible)
                .Select(Name)
                .OrderBy(name => name, StringComparer.Ordinal));
        }

        return Name(type) + " (" + kind + ")" + (bases.Count == 0 ? "" : " : " + string.Join(", ", bases));
    }

    private static IEnumerable<string> DescribeMembers(Type type)
    {
        foreach (var member in type.GetMembers(DeclaredMembers))
        {
            switch (member)
            {
                case ConstructorInfo constructor when IsVisible(constructor):
                    yield return Name(type) + "..ctor(" + Parameters(constructor) + ")" + Modifiers(constructor);
                    break;

                // Property and event accessors are special-name and are rendered with their
                // property or event instead; operators are special-name too and ARE surface.
                case MethodInfo method when IsVisible(method) &&
                                            (!method.IsSpecialName || method.Name.StartsWith("op_", StringComparison.Ordinal)):
                    yield return Name(type) + "." + method.Name + Generics(method) +
                                 "(" + Parameters(method) + ") : " + Name(method.ReturnType) + Modifiers(method);
                    break;

                case PropertyInfo property when Accessors(property).Any(IsVisible):
                    var accessors = new List<string>();
                    if (property.GetMethod is not null && IsVisible(property.GetMethod)) accessors.Add("get");
                    if (property.SetMethod is not null && IsVisible(property.SetMethod)) accessors.Add("set");
                    var indexer = property.GetIndexParameters();
                    var index = indexer.Length == 0 ? "" : "[" + string.Join(", ", indexer.Select(p => Name(p.ParameterType))) + "]";
                    yield return Name(type) + "." + property.Name + index +
                                 " { " + string.Join("; ", accessors) + "; } : " + Name(property.PropertyType) +
                                 Modifiers(Accessors(property).First(IsVisible));
                    break;

                // An enum's own value__ field is public but is the storage, not a member: it has no
                // constant value and asking for one throws.
                case FieldInfo field when IsVisible(field) && !(type.IsEnum && !field.IsLiteral):
                    // An enum's members are its surface: renumbering one is a breaking change for
                    // anything that persisted the number.
                    yield return type.IsEnum
                        ? Name(type) + "." + field.Name + " = " +
                          Convert.ToInt64(field.GetRawConstantValue(), CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)
                        : Name(type) + "." + field.Name + " : " + Name(field.FieldType) +
                          (field.IsLiteral ? " [const]" : field.IsStatic ? " [static]" : "");
                    break;

                case EventInfo declaredEvent when declaredEvent.AddMethod is not null && IsVisible(declaredEvent.AddMethod):
                    yield return Name(type) + "." + declaredEvent.Name + " (event) : " + Name(declaredEvent.EventHandlerType!);
                    break;
            }
        }
    }

    private static IEnumerable<MethodInfo> Accessors(PropertyInfo property) =>
        property.GetAccessors(nonPublic: true);

    private static bool IsVisible(MethodBase method) =>
        method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly;

    private static bool IsVisible(FieldInfo field) =>
        field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly;

    private static string Modifiers(MethodBase method)
    {
        if (method.IsStatic) return " [static]";
        if (method.IsAbstract) return " [abstract]";
        if (method.IsVirtual && !method.IsFinal) return " [virtual]";
        return "";
    }

    private static string Generics(MethodInfo method) =>
        method.IsGenericMethodDefinition
            ? "<" + string.Join(", ", method.GetGenericArguments().Select(argument => argument.Name)) + ">"
            : "";

    private static string Parameters(MethodBase method) =>
        string.Join(", ", method.GetParameters().Select(parameter =>
            Name(parameter.ParameterType) + (parameter.IsOptional ? " = default" : "")));

    private static string Name(Type type)
    {
        if (type.IsGenericParameter) return type.Name;
        if (type.IsArray) return Name(type.GetElementType()!) + "[]";
        if (type.IsByRef) return Name(type.GetElementType()!) + "&";

        var nullable = Nullable.GetUnderlyingType(type);
        if (nullable is not null) return Name(nullable) + "?";

        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition().FullName ?? type.Name;
            var tick = definition.IndexOf('`');
            if (tick > 0) definition = definition.Substring(0, tick);
            return Clean(definition) + "<" + string.Join(", ", type.GetGenericArguments().Select(Name)) + ">";
        }

        return Alias(Clean(type.FullName ?? type.Name));
    }

    /// <summary>Nested types come back as <c>Outer+Inner</c>; the source spelling is a dot.</summary>
    private static string Clean(string name) => name.Replace('+', '.');

    private static string Alias(string name) => name switch
    {
        "System.String" => "string",
        "System.Int32" => "int",
        "System.Int64" => "long",
        "System.Double" => "double",
        "System.Boolean" => "bool",
        "System.Object" => "object",
        "System.Void" => "void",
        _ => name,
    };
}
