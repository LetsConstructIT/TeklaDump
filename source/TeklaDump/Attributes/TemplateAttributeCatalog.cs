using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace TeklaDump.Attributes;

/// <summary>
/// The set of template (report) attributes Tekla knows about, keyed by content type
/// (<c>PART</c>, <c>BOLT</c>, <c>ASSEMBLY</c>, ...).
/// </summary>
/// <remarks>
/// Lifted verbatim from TeklaLookup — only the namespace and the accessibility changed. This
/// parser is the reason the tool is correct in US / DE / custom environments, and re-deriving it
/// would mean re-deriving a pile of environment quirks that were found the hard way.
/// <para>
/// There is no Open API call that enumerates available report properties —
/// <c>GetAllReportProperties</c> is a batch getter that requires the caller to already know the
/// names, pre-sorted into string/double/integer buckets. Tekla itself gets that list from the
/// <c>contentattributes*.lst</c> files in the environment, so this reads the same files. The
/// datatype column in those files is what makes the batch call possible at all.
/// </para>
/// </remarks>
internal sealed class TemplateAttributeCatalog
{
    /// <summary>
    /// Size at which even a recognised constituent group is treated as expensive. A backstop for
    /// customised environments, not the primary rule — no stock constituent comes close.
    /// </summary>
    internal const int RelatedObjectGroupSize = 100;

    private readonly Dictionary<string, IReadOnlyList<TemplateAttributeDefinition>> _byContentType;

    private TemplateAttributeCatalog(
        Dictionary<string, IReadOnlyList<TemplateAttributeDefinition>> byContentType,
        IReadOnlyList<string> sourceFiles,
        IReadOnlyList<string> searchedDirectories,
        string? loadError,
        int untypedCount)
    {
        _byContentType = byContentType;
        SourceFiles = sourceFiles;
        SearchedDirectories = searchedDirectories;
        LoadError = loadError;
        UntypedCount = untypedCount;
    }

    public static TemplateAttributeCatalog Empty { get; } = new(
        new Dictionary<string, IReadOnlyList<TemplateAttributeDefinition>>(StringComparer.OrdinalIgnoreCase),
        new string[0],
        new string[0],
        null,
        0);

    /// <summary>The .lst files this catalog was built from, in the order they were applied.</summary>
    public IReadOnlyList<string> SourceFiles { get; }

    /// <summary>
    /// Directories that were probed during discovery. Only interesting when nothing was found, at
    /// which point it is the difference between a dead end and an actionable message.
    /// </summary>
    public IReadOnlyList<string> SearchedDirectories { get; }

    /// <summary>Non-null when a file could not be read; the catalog is then empty or partial.</summary>
    public string? LoadError { get; }

    public bool IsEmpty => _byContentType.Count == 0;

    public int AttributeCount => _byContentType.Values.Sum(v => v.Count);

    /// <summary>
    /// Bindings dropped because no file declared a datatype for them. Such an attribute cannot be
    /// put in any <c>GetAllReportProperties</c> bucket, so it is unqueryable rather than merely
    /// unknown. Non-zero usually means a hand-edited environment with a binding but no definition.
    /// </summary>
    public int UntypedCount { get; }

    /// <summary>
    /// Attributes bound to <paramref name="contentType"/>, in the order Tekla declares them
    /// (which is the order its own attribute tree shows). Empty when the type is unknown.
    /// </summary>
    public IReadOnlyList<TemplateAttributeDefinition> ForContentType(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType)) return new TemplateAttributeDefinition[0];
        return _byContentType.TryGetValue(contentType!, out var list)
            ? list
            : new TemplateAttributeDefinition[0];
    }

    public static TemplateAttributeCatalog Load(IEnumerable<string> files)
        => Load(files, new string[0]);

    public static TemplateAttributeCatalog Load(
        IEnumerable<string> files,
        IReadOnlyList<string> searchedDirectories)
    {
        var builder = new CatalogBuilder(searchedDirectories);
        var applied = new List<string>();
        string? error = null;

        foreach (var file in files)
        {
            try
            {
                // False when an earlier file's [INCLUDE] already pulled this one in — recording it
                // again would show the same file twice in the diagnostics.
                if (builder.AddFile(file))
                    applied.Add(file);
            }
            catch (Exception ex)
            {
                // One unreadable file shouldn't cost us the rest of the environment.
                error ??= $"{Path.GetFileName(file)}: {ex.Message}";
            }
        }

        applied.AddRange(builder.IncludedFiles);

        return new TemplateAttributeCatalog(
            builder.Build(), applied, searchedDirectories, error, builder.UntypedCount);
    }

    /// <summary>
    /// Two-pass accumulator over any number of <c>contentattributes*.lst</c> files. Definitions
    /// (name to datatype) and bindings (content type to name) are collected across all files
    /// first, because a binding in one file routinely refers to a datatype declared in another.
    /// </summary>
    private sealed class CatalogBuilder
    {
        /// <summary>Depth cap so a malformed environment with a circular include can't hang us.</summary>
        private const int MaxIncludeDepth = 8;

        private readonly Dictionary<string, TemplateValueType> _valueTypes =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, List<RawBinding>> _bindings =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly HashSet<string> _visited = new(StringComparer.OrdinalIgnoreCase);
        private readonly IReadOnlyList<string> _searchDirectories;

        public CatalogBuilder(IReadOnlyList<string> searchDirectories)
        {
            _searchDirectories = searchDirectories;
        }

        public int UntypedCount { get; private set; }

        /// <summary>Files pulled in through <c>[INCLUDE]</c> rather than found by discovery.</summary>
        public List<string> IncludedFiles { get; } = new();

        /// <returns>False when the file was already applied, so callers don't double-report it.</returns>
        public bool AddFile(string path) => AddFile(path, 0);

        private bool AddFile(string path, int depth)
        {
            var full = SafeFullPath(path);
            if (!_visited.Add(full)) return false;

            var inBindings = false;
            foreach (var rawLine in ReadAllLinesDetectingEncoding(path))
            {
                var line = StripComment(rawLine).Trim();
                if (line.Length == 0) continue;

                if (line.StartsWith("[BINDINGS]", StringComparison.OrdinalIgnoreCase))
                {
                    inBindings = true;
                    continue;
                }

                // Several stock environments (Finland, ConstrusoftEuropean) ship
                // contentattributes.lst as a pure container: a handful of [INCLUDE] lines and an
                // EMPTY [BINDINGS] section. Ignoring the directive yields a catalog of nothing.
                if (TryReadInclude(line, out var relative))
                {
                    if (depth < MaxIncludeDepth) Include(relative, path, depth);
                    continue;
                }

                if (inBindings) ParseBinding(line);
                else ParseDefinition(line);
            }

            return true;
        }

        private void Include(string relative, string includingFile, int depth)
        {
            var resolved = ResolveInclude(relative, includingFile);
            if (resolved is null) return;

            try
            {
                if (AddFile(resolved, depth + 1))
                    IncludedFiles.Add(resolved);
            }
            catch (Exception)
            {
                // A missing or unreadable include is normal — environments reference optional
                // integration files (ArchiCAD, MagiCAD, Revit) that are not always installed.
            }
        }

        /// <summary>
        /// Include paths are written relative to the Template Editor's own settings layout, e.g.
        /// <c>.\settings\contentattributes_global.lst</c> from a file that already sits IN a
        /// settings folder. So the parent directory is tried as well as the file's own, and
        /// finally the bare filename across every discovered directory.
        /// </summary>
        private string? ResolveInclude(string relative, string includingFile)
        {
            if (Path.IsPathRooted(relative))
                return File.Exists(relative) ? relative : null;

            var directory = Path.GetDirectoryName(includingFile);
            if (string.IsNullOrEmpty(directory)) return null;

            var parent = Path.GetDirectoryName(directory);
            var fileName = Path.GetFileName(relative);

            foreach (var candidate in EnumerateCandidates(directory!, parent, relative, fileName))
            {
                try
                {
                    if (File.Exists(candidate)) return candidate;
                }
                catch (Exception)
                {
                    // malformed candidate path — try the next one
                }
            }

            return null;
        }

        private IEnumerable<string> EnumerateCandidates(
            string directory, string? parent, string relative, string fileName)
        {
            yield return Path.Combine(directory, relative);
            if (parent is not null) yield return Path.Combine(parent, relative);
            yield return Path.Combine(directory, fileName);

            foreach (var searched in _searchDirectories)
            {
                yield return Path.Combine(searched, relative);
                yield return Path.Combine(searched, fileName);
            }
        }

        private static bool TryReadInclude(string line, out string relative)
        {
            relative = string.Empty;
            if (!line.StartsWith("[INCLUDE", StringComparison.OrdinalIgnoreCase)) return false;

            var close = line.LastIndexOf(']');
            if (close <= "[INCLUDE".Length) return false;

            relative = line.Substring("[INCLUDE".Length, close - "[INCLUDE".Length).Trim();
            return relative.Length > 0;
        }

        private static string SafeFullPath(string path)
        {
            try { return Path.GetFullPath(path); }
            catch (Exception) { return path; }
        }

        /// <summary>
        /// Reads a .lst file with its encoding detected rather than assumed. Stock files are a
        /// mix — ASCII, UTF-8 with BOM, and legacy ANSI (Finland's and the USA environment's
        /// userdefined/external files ship that way, and firm files generated from
        /// <c>objects.inp</c> usually do too). Decoding ANSI bytes as UTF-8 silently replaces
        /// them with U+FFFD, corrupting quoted labels and any attribute name that carries one.
        /// </summary>
        private static string[] ReadAllLinesDetectingEncoding(string path)
        {
            var bytes = File.ReadAllBytes(path);

            Encoding encoding;
            var offset = 0;
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                encoding = Encoding.UTF8;
                offset = 3;
            }
            else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                encoding = Encoding.Unicode;
                offset = 2;
            }
            else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                encoding = Encoding.BigEndianUnicode;
                offset = 2;
            }
            else
            {
                // No BOM: strict UTF-8 if the bytes actually are UTF-8 (which covers plain
                // ASCII), otherwise the system ANSI code page — the encoding the Template Editor
                // itself writes on that machine.
                encoding = new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
                try
                {
                    return SplitLines(encoding.GetString(bytes));
                }
                catch (DecoderFallbackException)
                {
                    encoding = Encoding.Default;
                }
            }

            return SplitLines(encoding.GetString(bytes, offset, bytes.Length - offset));
        }

        private static string[] SplitLines(string text)
            => text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

        public Dictionary<string, IReadOnlyList<TemplateAttributeDefinition>> Build()
        {
            var result = new Dictionary<string, IReadOnlyList<TemplateAttributeDefinition>>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var pair in _bindings)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var resolved = new List<(string Name, TemplateValueType Type, string Group, string? Label)>(
                    pair.Value.Count);

                foreach (var binding in pair.Value)
                {
                    var name = binding.Name;
                    if (name.Length == 0 || !seen.Add(name)) continue;
                    if (!TryResolveValueType(name, out var valueType))
                    {
                        UntypedCount++;
                        continue;
                    }

                    resolved.Add((name, valueType, GroupOf(name), binding.Label));
                }

                if (resolved.Count == 0) continue;

                var heavyGroups = HeavyGroups(resolved.Select(r => r.Group));
                var definitions = new List<TemplateAttributeDefinition>(resolved.Count);
                foreach (var r in resolved)
                {
                    definitions.Add(new TemplateAttributeDefinition(
                        r.Name, r.Type, r.Group, r.Label, heavyGroups.Contains(r.Group)));
                }

                result[pair.Key] = definitions;
            }

            return result;
        }

        /// <summary>
        /// Splits groups into "traverses to another model object" (expensive) and "describes a
        /// constituent of this one" (cheap).
        /// </summary>
        /// <remarks>
        /// Membership has to be named rather than measured. Size alone looks tempting — object
        /// traversals re-expose a whole content type and land in the hundreds (<c>MAIN_PART</c> 285,
        /// <c>CAST_UNIT</c> 703) while constituents contribute dozens — but the populations overlap
        /// where it matters: on a bolt <c>ASSEMBLY</c> (37) and <c>PROJECT</c> (64) are traversals,
        /// yet on a part <c>PROFILE</c> (72) is a constituent. No threshold separates 37 from 72.
        /// <para>
        /// So the constituents are listed, and anything unrecognised is treated as a traversal. That
        /// direction matters: a group this list has never heard of costs nothing in the Associated
        /// tier, it just waits in Full. A custom environment can add groups without ever making the
        /// default view slow — the worst case is an attribute the user has to switch modes or pin
        /// to see, never a hang.
        /// </para>
        /// </remarks>
        private static readonly HashSet<string> ConstituentGroups = new(StringComparer.OrdinalIgnoreCase)
        {
            "NUT", "WASHER", "HOLE",     // a bolt's own parts — the case this feature was built for
            "PROFILE", "MATERIAL",       // catalog lookups describing a part
            "PHASE", "HISTORY",          // cheap metadata carried by the object itself
            "CUSTOM", "EXTERNAL", "REPORT",
        };

        private static HashSet<string> HeavyGroups(IEnumerable<string> groups)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in groups)
            {
                if (group.Length == 0) continue;
                counts.TryGetValue(group, out var count);
                counts[group] = count + 1;
            }

            var heavy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in counts)
            {
                // Size still has a vote, as a guard: a listed group that has been stuffed with
                // hundreds of attributes in some custom environment is expensive whatever it means.
                if (!ConstituentGroups.Contains(pair.Key) || pair.Value >= RelatedObjectGroupSize)
                    heavy.Add(pair.Key);
            }

            return heavy;
        }

        /// <summary>
        /// Datatypes are declared for bare names only (<c>WEIGHT</c>) while bindings are dotted
        /// (<c>NUT.WEIGHT</c>), so the last segment is what carries the type.
        /// </summary>
        private bool TryResolveValueType(string name, out TemplateValueType valueType)
        {
            if (_valueTypes.TryGetValue(name, out valueType)) return true;

            var lastDot = name.LastIndexOf('.');
            if (lastDot >= 0 && lastDot < name.Length - 1)
                return _valueTypes.TryGetValue(name.Substring(lastDot + 1), out valueType);

            valueType = TemplateValueType.Character;
            return false;
        }

        private void ParseDefinition(string line)
        {
            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return;
            if (!TryParseValueType(parts[1], out var valueType)) return;

            var name = NormalizeName(parts[0]);
            if (name.Length == 0) return;

            // First declaration wins: files are applied in Tekla's override order (model, project,
            // firm, system), so whatever got here first is the more specific one.
            if (!_valueTypes.ContainsKey(name))
                _valueTypes[name] = valueType;
        }

        private void ParseBinding(string line)
        {
            var equals = line.IndexOf('=');
            if (equals <= 0) return;

            var contentType = line.Substring(0, equals).Trim();
            if (contentType.Length == 0 || !IsContentTypeToken(contentType)) return;

            var token = ReadAttributeToken(line.Substring(equals + 1), out var afterToken);
            if (token.Length == 0) return;

            var name = NormalizeName(token);
            if (name.Length == 0) return;

            if (!_bindings.TryGetValue(contentType, out var list))
                _bindings[contentType] = list = new List<RawBinding>();

            list.Add(new RawBinding(name, ReadQuotedLabel(afterToken)));
        }

        private static bool TryParseValueType(string token, out TemplateValueType valueType)
        {
            switch (token.ToUpperInvariant())
            {
                case "CHARACTER": valueType = TemplateValueType.Character; return true;
                case "FLOAT": valueType = TemplateValueType.Float; return true;
                case "INTEGER": valueType = TemplateValueType.Integer; return true;
                default: valueType = TemplateValueType.Character; return false;
            }
        }

        private static bool IsContentTypeToken(string token)
        {
            foreach (var c in token)
            {
                if (c >= 'A' && c <= 'Z') continue;
                if (c >= '0' && c <= '9') continue;
                if (c == '_') continue;
                return false;
            }
            return true;
        }

        /// <summary>
        /// Reads the attribute name off the right-hand side of a binding. Whitespace inside a
        /// bracketed virtual node is part of the token — <c>USERDEFINED.[Base plate].SPACE_B</c>
        /// is one name, not three — so the scan tracks bracket depth instead of splitting.
        /// </summary>
        private static string ReadAttributeToken(string text, out string remainder)
        {
            var start = 0;
            while (start < text.Length && char.IsWhiteSpace(text[start])) start++;

            var depth = 0;
            var end = start;
            while (end < text.Length)
            {
                var c = text[end];
                if (c == '[') depth++;
                else if (c == ']') { if (depth > 0) depth--; }
                else if (depth == 0 && char.IsWhiteSpace(c)) break;
                end++;
            }

            remainder = end < text.Length ? text.Substring(end) : string.Empty;
            return text.Substring(start, end - start);
        }

        private static string? ReadQuotedLabel(string text)
        {
            var open = text.IndexOf('"');
            if (open < 0) return null;
            var close = text.IndexOf('"', open + 1);
            if (close <= open + 1) return null;
            return text.Substring(open + 1, close - open - 1).Trim();
        }

        private readonly struct RawBinding
        {
            public RawBinding(string name, string? label)
            {
                Name = name;
                Label = label;
            }

            public string Name { get; }
            public string? Label { get; }
        }
    }

    /// <summary>
    /// Turns an authored name into the one the property API accepts. Bracketed virtual nodes are
    /// pure grouping in the Template Editor tree, and a <c>#N</c> suffix marks a version-specific
    /// variant of the same underlying attribute; neither belongs in a query.
    /// </summary>
    internal static string NormalizeName(string raw)
    {
        var segments = raw.Split('.');
        var kept = new List<string>(segments.Length);

        foreach (var segment in segments)
        {
            var trimmed = segment.Trim();
            if (trimmed.Length == 0) continue;
            if (trimmed[0] == '[') continue;

            var hash = trimmed.IndexOf('#');
            if (hash >= 0) trimmed = trimmed.Substring(0, hash);

            trimmed = trimmed.Trim();
            if (trimmed.Length > 0) kept.Add(trimmed);
        }

        return string.Join(".", kept);
    }

    /// <summary>Leading dotted segment, which is how Tekla names a related object's attributes.</summary>
    internal static string GroupOf(string normalizedName)
    {
        var dot = normalizedName.IndexOf('.');
        return dot <= 0 ? string.Empty : normalizedName.Substring(0, dot);
    }

    private static string StripComment(string line)
    {
        var inQuotes = false;
        for (var i = 0; i < line.Length - 1; i++)
        {
            if (line[i] == '"') inQuotes = !inQuotes;
            else if (!inQuotes && line[i] == '/' && line[i + 1] == '/')
                return line.Substring(0, i);
        }
        return line;
    }
}
