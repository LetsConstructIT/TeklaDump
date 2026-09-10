using System;
using System.Collections.Generic;
using System.IO;
using Tekla.Structures;

namespace TeklaDump.Attributes;

/// <summary>
/// Finds the environment's <c>contentattributes*.lst</c> files and caches the parsed
/// <see cref="TemplateAttributeCatalog"/>.
/// </summary>
/// <remarks>
/// Search order mirrors Tekla's own: model folder, then <c>XS_PROJECT</c>, <c>XS_FIRM</c>,
/// <c>XS_SYSTEM</c> and finally <c>XS_TPLED_INI</c> — the option every stock environment uses to
/// name its Template Editor settings folder outright. Each root is checked directly and under
/// both <c>template\settings\</c> and <c>settings\</c>: most environments use the former, but the
/// USA environment reaches its files as <c>...\General\Templates\</c> + <c>settings\</c>, and
/// <c>blank_project</c>'s XS_SYSTEM likewise stops one <c>settings\</c> short of the files.
/// First file of a given NAME wins, per filename rather than per folder — a project that
/// overrides only <c>contentattributes_userdefined.lst</c> still inherits the system
/// <c>_global</c> one.
/// <para>
/// The cache is keyed on the open model's path, so opening a model in a different environment
/// picks up that environment's attributes without anyone having to invalidate anything.
/// </para>
/// </remarks>
internal static class TemplateAttributeCatalogProvider
{
    /// <summary>
    /// <c>XS_TPLED_INI</c> last: it points at the environment's Template Editor settings folder
    /// directly and is the authoritative source, but project/firm/system overrides still win on a
    /// per-filename basis, so it belongs after them like the installation defaults belong after it.
    /// </summary>
    private static readonly string[] SearchOptions = { "XS_PROJECT", "XS_FIRM", "XS_SYSTEM", "XS_TPLED_INI" };

    /// <summary>
    /// Where the Template Editor settings folder sits relative to an installation root. Two forms
    /// because <c>XS_DIR</c> points at the installation while the API assembly resolves inside
    /// <c>bin</c>, and either may be what we are walking from.
    /// </summary>
    private static readonly string[] TplEdRelativePaths =
    {
        Path.Combine("bin", "applications", "Tekla", "Tools", "TplEd", "settings"),
        Path.Combine("applications", "Tekla", "Tools", "TplEd", "settings"),
    };
    private static readonly object Gate = new();

    private static TemplateAttributeCatalog? _catalog;
    private static string? _catalogModelPath;

    /// <summary>
    /// The catalog for the model at <paramref name="modelPath"/>, loaded on first use and cached.
    /// Never null and never throws — with no model path this is
    /// <see cref="TemplateAttributeCatalog.Empty"/> and callers simply emit no template attributes.
    /// </summary>
    /// <remarks>
    /// The model path is a PARAMETER rather than something read from a <c>Model</c> here: the
    /// library keeps every <c>Model</c> touch inside <c>SessionInfo</c>, and this class already
    /// reads the environment through <c>TeklaStructuresSettings</c>, which needs no model.
    /// The cache is keyed on the path, so opening a model in a different environment picks up that
    /// environment's attributes without anyone having to invalidate anything.
    /// </remarks>
    public static TemplateAttributeCatalog Load(string? modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath)) return TemplateAttributeCatalog.Empty;

        lock (Gate)
        {
            if (_catalog is not null &&
                string.Equals(_catalogModelPath, modelPath, StringComparison.OrdinalIgnoreCase))
            {
                return _catalog;
            }

            _catalog = LoadSafely(modelPath!);
            _catalogModelPath = modelPath;
            return _catalog;
        }
    }

    private static TemplateAttributeCatalog LoadSafely(string modelPath)
    {
        try
        {
            var files = DiscoverFiles(modelPath, out var searched);
            return TemplateAttributeCatalog.Load(files, searched);
        }
        catch (Exception)
        {
            // Attribute discovery is an enhancement, never a reason to fail a dump.
            return TemplateAttributeCatalog.Empty;
        }
    }

    /// <param name="modelPath">The open model folder, the first place Tekla itself looks.</param>
    /// <param name="searched">
    /// Every directory actually probed. Reported back so a "found nothing" outcome can say where
    /// it looked instead of leaving the user to guess.
    /// </param>
    private static IReadOnlyList<string> DiscoverFiles(string modelPath, out IReadOnlyList<string> searched)
    {
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var found = new List<string>();
        var probed = new List<string>();
        searched = probed;

        foreach (var directory in CandidateDirectories(modelPath))
        {
            if (!visited.Add(directory)) continue;
            probed.Add(directory);

            string[] files;
            try
            {
                if (!Directory.Exists(directory)) continue;
                // Leading wildcard on purpose: environments prefix their own files
                // (FI_contentattributes_userdefined.lst), and those are real attribute sources.
                files = Directory.GetFiles(directory, "*contentattributes*.lst");
            }
            catch (Exception)
            {
                continue; // unreadable or malformed path — skip it, keep searching
            }

            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                if (claimed.Add(Path.GetFileName(file)))
                    found.Add(file);
            }
        }

        return found;
    }

    private static IEnumerable<string> CandidateDirectories(string modelPath)
    {
        foreach (var root in SearchRoots(modelPath))
        {
            string? templateSettings;
            string? settings;
            try
            {
                templateSettings = Path.Combine(root, "template", "settings");
                // The USA environment keeps its files under "...\Templates\settings" and reaches
                // them via a search-path entry ending at "Templates\" — so the bare "settings\"
                // child matters too, not just "template\settings\".
                settings = Path.Combine(root, "settings");
            }
            catch (ArgumentException)
            {
                continue; // invalid characters in an advanced option entry
            }

            yield return root;
            yield return templateSettings;
            yield return settings;
        }
    }

    private static IEnumerable<string> SearchRoots(string modelPath)
    {
        yield return modelPath;
        yield return Path.Combine(modelPath, "attributes");

        foreach (var option in SearchOptions)
        {
            foreach (var path in TryGetOptionPaths(option))
                yield return path;
        }

        // Last, so anything the environment defines wins. This is where the stock definitions
        // actually live — an environment like Finland ships only a container file that INCLUDEs
        // contentattributes_global.lst from here, so without this root it resolves to nothing.
        foreach (var path in TemplateEditorSettingsDirectories())
            yield return path;
    }

    /// <summary>
    /// The Template Editor's own settings folder inside the Tekla installation
    /// (<c>bin\applications\Tekla\Tools\TplEd\settings</c>), which holds the default attribute set.
    /// </summary>
    /// <remarks>
    /// Located from the <c>XS_DIR</c> advanced option where it answers, then from the <c>XS_DIR</c>
    /// environment variable — the app is started by Tekla and inherits its environment block, so
    /// the variable is present even when the option API is not responding — and last from the
    /// folder the Tekla API assembly was loaded from, which only helps when that is the
    /// installation's <c>bin</c> rather than the GAC.
    /// </remarks>
    private static IEnumerable<string> TemplateEditorSettingsDirectories()
    {
        foreach (var root in InstallationRoots())
        {
            foreach (var relative in TplEdRelativePaths)
            {
                string candidate;
                try
                {
                    candidate = Path.Combine(root, relative);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                yield return candidate;
            }
        }
    }

    private static IEnumerable<string> InstallationRoots()
    {
        var installDirectory = TryGetStringOption("XS_DIR");
        if (!string.IsNullOrWhiteSpace(installDirectory))
            yield return installDirectory!;

        var inheritedInstallDirectory = TryGetEnvironmentVariable("XS_DIR");
        if (inheritedInstallDirectory is not null)
            yield return inheritedInstallDirectory;

        string? assemblyDirectory = null;
        try
        {
            // The Open API packages are compile-only, so at runtime this assembly usually resolves
            // from the GAC — a path under Windows that a walk-up can never turn into an
            // installation root. Only a genuine <install>\bin location is worth walking from.
            // typeof only — the Open API assembly's location, not a Model instance.
            var location = typeof(Tekla.Structures.Model.Model).Assembly.Location;
            if (!string.IsNullOrEmpty(location) &&
                location.IndexOf("GAC", StringComparison.OrdinalIgnoreCase) < 0)
            {
                assemblyDirectory = Path.GetDirectoryName(location);
            }
        }
        catch (Exception)
        {
            // Loaded without a file path at all — nothing to derive.
        }

        // Walk up from the assembly: it sits under <install>\bin, so <install> is a level or two up.
        for (var i = 0; i < 3 && assemblyDirectory is not null; i++)
        {
            yield return assemblyDirectory;
            assemblyDirectory = Path.GetDirectoryName(assemblyDirectory);
        }
    }

    private static string? TryGetEnvironmentVariable(string name)
    {
        try
        {
            var value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? TryGetStringOption(string option)
    {
        try
        {
            var value = string.Empty;
            return TeklaStructuresSettings.GetAdvancedOption(option, ref value) ? value : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> TryGetOptionPaths(string option)
    {
        try
        {
            // Splits the semicolon-separated advanced option and drops blanks for us.
            //
            // The return value is deliberately IGNORED. It means "read successfully AND every
            // entry was a valid path" — one bad entry in a long XS_SYSTEM list makes it false
            // while `paths` still holds all the good ones. Gating on it discards a whole
            // environment because of a single stale folder, which is exactly what stock
            // environments contain.
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
}
