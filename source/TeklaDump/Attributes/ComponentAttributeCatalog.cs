using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tekla.Structures;

namespace TeklaDump.Attributes;

/// <summary>The type an attribute value should be read back as, guessed from the saved literal.</summary>
internal enum ComponentValueType
{
    Integer,
    Double,
    String,
}

/// <summary>One discovered component attribute name plus the overload to try first.</summary>
internal sealed class ComponentAttributeName
{
    public ComponentAttributeName(string name, ComponentValueType valueType)
    {
        Name = name;
        ValueType = valueType;
    }

    public string Name { get; }

    public ComponentValueType ValueType { get; }
}

/// <summary>
/// Discovers the attribute-name universe of a component, because the Open API has no call that
/// lists one.
/// </summary>
/// <remarks>
/// <para>
/// <c>BaseComponent</c> exposes <c>GetAttribute(name, ref int|double|string)</c> and
/// <c>LoadAttributesFromFile</c>, and nothing that enumerates names. <c>GetAttribute</c> returning
/// <c>false</c> means the user never set that attribute and the component is using its internal
/// default — which is exactly the set a regenerating script should NOT assign, so those are
/// omitted from the create block on purpose.
/// </para>
/// <para>
/// So the names are discovered the same way the template catalog is: from the environment's own
/// files. Tekla saves a component's attributes as <c>&lt;setname&gt;.j&lt;Number&gt;</c> for a
/// numbered system component (<c>standard.j141</c>) and <c>&lt;setname&gt;.&lt;PluginName&gt;</c>
/// for a plugin, both plain text in the whitespace-separated form documented on
/// <see cref="ParseFile"/>. Every such file on the attribute search path is parsed and the union of
/// keys is the universe for that component.
/// </para>
/// <para>
/// <b>The file naming is UNVERIFIED across environments</b> — it has been checked against a stock
/// 2026.0 installation only, and it is the largest open risk in the component path. When discovery finds nothing, the component
/// still dumps (identity, input, name/number); only the attribute block is missing, a warning names
/// the component, and <c>ComponentAttributeNames</c> / <c>--component-attrs</c> is the escape hatch.
/// </para>
/// </remarks>
internal sealed class ComponentAttributeCatalog
{
    /// <summary>
    /// Advanced options whose paths hold environment attribute files, in Tekla's own override
    /// order (most specific first).
    /// </summary>
    private static readonly string[] SearchOptions = { "XS_PROJECT", "XS_FIRM", "XS_SYSTEM" };

    /// <summary>
    /// A component that saved a hundred attributes is normal; one that appears to have thousands
    /// means the parse latched onto the wrong kind of file, and reading them all would be a very
    /// slow way to produce nonsense.
    /// </summary>
    private const int MaxNamesPerComponent = 500;

    private readonly IReadOnlyList<string> _directories;
    private readonly Dictionary<string, IReadOnlyList<ComponentAttributeName>> _cache =
        new Dictionary<string, IReadOnlyList<ComponentAttributeName>>(StringComparer.OrdinalIgnoreCase);

    private ComponentAttributeCatalog(IReadOnlyList<string> directories)
    {
        _directories = directories;
    }

    public static ComponentAttributeCatalog Empty { get; } = new ComponentAttributeCatalog(new string[0]);

    /// <summary>Directories that were probed. Only interesting when nothing was found.</summary>
    public IReadOnlyList<string> SearchedDirectories => _directories;

    /// <summary>
    /// Builds a catalog over the model folder and the environment search path.
    /// </summary>
    public static ComponentAttributeCatalog Create(string? modelPath)
    {
        var directories = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory)) return;
            if (seen.Add(directory!)) directories.Add(directory!);
        }

        if (!string.IsNullOrWhiteSpace(modelPath))
        {
            Add(modelPath);
            // Where the model's own saved component attributes actually live.
            try { Add(Path.Combine(modelPath!, "attributes")); }
            catch (ArgumentException) { }
        }

        foreach (var option in SearchOptions)
            foreach (var path in OptionPaths(option))
                Add(path);

        return new ComponentAttributeCatalog(directories);
    }

    /// <summary>
    /// The attribute names for a numbered system component, e.g. <c>141</c> — the union of the keys
    /// in every <c>*.j141</c> on the search path.
    /// </summary>
    public IReadOnlyList<ComponentAttributeName> ForNumber(int number) =>
        Resolve("j" + number.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>
    /// The attribute names for a plugin component, whose files are named
    /// <c>&lt;setname&gt;.&lt;PluginName&gt;</c>.
    /// </summary>
    /// <remarks>
    /// Stock plugin files on a 2026 installation carry a <c>p_</c> before the plugin name
    /// (<c>standard.p_GenericFormworkBeam</c>), while the name the API reports has no such prefix.
    /// Whether that <c>p_</c> is universal is exactly the kind of thing that varies by
    /// environment, so both spellings are tried and whichever answers wins.
    /// </remarks>
    public IReadOnlyList<ComponentAttributeName> ForPlugin(string pluginName)
    {
        if (string.IsNullOrWhiteSpace(pluginName)) return Empty1;

        var direct = Resolve(pluginName);
        return direct.Count > 0 ? direct : Resolve("p_" + pluginName);
    }

    /// <summary>
    /// Parses one saved attribute file into names with a guessed value type. Public for the test
    /// suite, which drives it against committed fixture files rather than an installation.
    /// </summary>
    /// <remarks>
    /// The real format, checked against a Tekla 2026 installation, is one
    /// <c>&lt;prefix&gt;_attributes.&lt;NAME&gt; &lt;value&gt;</c> per line — whitespace separated,
    /// not <c>name=value</c>, and with the queryable name sitting AFTER a prefix that names the
    /// file's owner (<c>joint_attributes.</c> for a numbered component,
    /// <c>GenericFormworkBeam_attributes.</c> for that plugin). The prefix is stripped: it is the
    /// name after the dot that <c>GetAttribute</c> accepts.
    /// <para>
    /// Values are only read to guess a type. Tekla writes <c>-2147483648</c> (int.MinValue) as its
    /// "not set" sentinel, which is why the values in these files are worthless as data and the
    /// live <c>GetAttribute</c> call is what decides whether an attribute is actually set.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<ComponentAttributeName> ParseFile(string path)
    {
        var results = new List<ComponentAttributeName>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rawLine in ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            if (line[0] == '/' || line[0] == '#' || line[0] == '[') continue;

            var split = line.IndexOfAny(WhitespaceOrEquals);
            if (split <= 0) continue;

            var name = StripPrefix(line.Substring(0, split).Trim());
            if (name.Length == 0 || !IsPlausibleName(name)) continue;
            if (FileMetadata.Contains(name)) continue;
            if (!seen.Add(name)) continue;

            var value = line.Substring(split + 1).Trim().TrimStart('=').Trim();
            results.Add(new ComponentAttributeName(name, GuessType(value)));
        }

        return results;
    }

    /// <summary>
    /// Drops the <c>&lt;something&gt;_attributes.</c> prefix. A name with no such prefix is left
    /// alone — some files are written flat, and a name is never wrong for having no prefix.
    /// </summary>
    internal static string StripPrefix(string token)
    {
        var dot = token.IndexOf('.');
        if (dot <= 0 || dot == token.Length - 1) return token;

        var prefix = token.Substring(0, dot);
        return prefix.EndsWith("_attributes", StringComparison.OrdinalIgnoreCase)
            ? token.Substring(dot + 1)
            : token;
    }

    /// <summary>
    /// Quoted literal to <c>string</c>, integer literal to <c>int</c>, decimal literal to
    /// <c>double</c> — the order the three typed <c>GetAttribute</c> overloads should be tried in.
    /// </summary>
    internal static ComponentValueType GuessType(string value)
    {
        if (value.Length == 0) return ComponentValueType.String;
        if (value[0] == '"') return ComponentValueType.String;

        if (long.TryParse(value, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            return ComponentValueType.Integer;
        }

        if (double.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            return ComponentValueType.Double;
        }

        return ComponentValueType.String;
    }

    /// <summary>
    /// Bookkeeping Tekla writes into every one of these files. They are not component attributes,
    /// and reading them back would put three lines of noise in every component record.
    /// </summary>
    private static readonly HashSet<string> FileMetadata = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "saveas_file", "get_menu", "name_enable", "encoding",
    };

    private static readonly char[] WhitespaceOrEquals = { ' ', '\t', '=' };

    private IReadOnlyList<ComponentAttributeName> Resolve(string extension)
    {
        if (_cache.TryGetValue(extension, out var cached)) return cached;

        var byName = new Dictionary<string, ComponentAttributeName>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var directory in _directories)
        {
            string[] files;
            try
            {
                if (!Directory.Exists(directory)) continue;
                files = Directory.GetFiles(directory, "*." + extension);
            }
            catch (Exception)
            {
                continue; // unreadable share or malformed path — keep searching
            }

            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                IReadOnlyList<ComponentAttributeName> parsed;
                try { parsed = ParseFile(file); }
                catch (Exception) { continue; }

                foreach (var attribute in parsed)
                {
                    if (byName.ContainsKey(attribute.Name)) continue;
                    if (order.Count >= MaxNamesPerComponent) break;
                    byName[attribute.Name] = attribute;
                    order.Add(attribute.Name);
                }
            }
        }

        // Ordinal by name: the union of several files has no meaningful natural order, and a
        // stable one is what makes two dumps of the same component diff clean.
        order.Sort(StringComparer.Ordinal);
        var result = new List<ComponentAttributeName>(order.Count);
        foreach (var name in order) result.Add(byName[name]);

        _cache[extension] = result;
        return result;
    }

    /// <summary>
    /// Keeps the parse from treating a stray line of prose as an attribute. Tekla's attribute names
    /// are identifier-shaped; anything with whitespace or punctuation in it is not one.
    /// </summary>
    private static bool IsPlausibleName(string name)
    {
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == '-') continue;
            return false;
        }
        return true;
    }

    /// <summary>
    /// Same encoding handling as the <c>.lst</c> parser: these files are a mix of ASCII, UTF-8 and
    /// legacy ANSI, and decoding ANSI as UTF-8 corrupts every non-ASCII name it touches.
    /// </summary>
    private static string[] ReadLines(string path)
    {
        var bytes = File.ReadAllBytes(path);

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Split(Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));

        var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        try
        {
            return Split(strict.GetString(bytes));
        }
        catch (DecoderFallbackException)
        {
            return Split(Encoding.Default.GetString(bytes));
        }
    }

    private static string[] Split(string text) =>
        text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

    private static IReadOnlyList<string> OptionPaths(string option)
    {
        try
        {
            // The return value is deliberately ignored: it means "read successfully AND every entry
            // was a valid path", so one stale folder in a long XS_SYSTEM list would discard the
            // whole environment. See the same note in TemplateAttributeCatalogProvider.
            TeklaStructuresSettings.GetAdvancedOptionPaths(
                option, out var paths, (advancedOption, invalidString, exceptionMessage) => { });
            if (paths is not null) return paths;
        }
        catch (Exception)
        {
            // Older or oddly-configured Tekla — treat as "option not set".
        }

        return new string[0];
    }

    private static readonly ComponentAttributeName[] Empty1 = new ComponentAttributeName[0];
}
