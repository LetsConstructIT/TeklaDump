using System;
using System.Collections.Generic;

namespace TeklaDump.Attributes;

/// <summary>
/// Decides which declared attributes a run reads. Split out so the rule can be exercised without a
/// Tekla connection — it is pure, and it is the part most likely to be got wrong.
/// </summary>
internal static class TemplateAttributeSelection
{
    /// <summary>
    /// <c>USERDEFINED.*</c> duplicates what <c>GetAllUserProperties</c> already reports under
    /// <c>userProperties</c>. Prefixed forms such as <c>ASSEMBLY.MAINPART.USERDEFINED.X</c> belong
    /// to a different object and are kept.
    /// </summary>
    public const string UserDefinedGroup = "USERDEFINED";

    public static bool IsSelected(TemplateAttributeDefinition definition, TemplateAttributeScope scope)
    {
        if (scope == TemplateAttributeScope.None) return false;

        if (string.Equals(definition.Group, UserDefinedGroup, StringComparison.OrdinalIgnoreCase))
            return false;

        if (definition.IsDirect) return true;

        return scope == TemplateAttributeScope.Full || definition.IsAssociated;
    }

    /// <summary>
    /// The definitions to read for one content type, capped at <paramref name="maxAttributes"/>.
    /// </summary>
    /// <param name="declared">Everything the catalog declares for this content type.</param>
    /// <param name="scope">How much of that list to read.</param>
    /// <param name="maxAttributes">Ceiling on how many attributes one pass reads.</param>
    /// <param name="explicitNames">
    /// When non-empty, exactly these names are read and the scope is ignored — that is what
    /// "explicit wins over scope" means. A name the catalog does not declare is still read, with
    /// its value type guessed as CHARACTER, because a user who typed a name knows something the
    /// catalog may not (a firm attribute added without a definition line).
    /// </param>
    /// <param name="truncated">
    /// True when the cap dropped something. Direct attributes are never dropped; only
    /// related-object ones are, so the cheap answer stays complete.
    /// </param>
    public static IReadOnlyList<TemplateAttributeDefinition> Plan(
        IReadOnlyList<TemplateAttributeDefinition> declared,
        TemplateAttributeScope scope,
        IReadOnlyList<string>? explicitNames,
        int maxAttributes,
        out bool truncated)
    {
        truncated = false;

        if (explicitNames is not null && explicitNames.Count > 0)
            return PlanExplicit(declared, explicitNames);

        var selected = new List<TemplateAttributeDefinition>(declared.Count);
        foreach (var definition in declared)
        {
            if (!IsSelected(definition, scope)) continue;

            if (!definition.IsDirect && selected.Count >= maxAttributes)
            {
                truncated = true;
                continue;
            }

            selected.Add(definition);
        }

        return selected;
    }

    private static IReadOnlyList<TemplateAttributeDefinition> PlanExplicit(
        IReadOnlyList<TemplateAttributeDefinition> declared,
        IReadOnlyList<string> names)
    {
        var byName = new Dictionary<string, TemplateAttributeDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in declared) byName[definition.Name] = definition;

        var selected = new List<TemplateAttributeDefinition>(names.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in names)
        {
            var name = TemplateAttributeCatalog.NormalizeName(raw ?? string.Empty);
            if (name.Length == 0 || !seen.Add(name)) continue;

            selected.Add(byName.TryGetValue(name, out var definition)
                ? definition
                : new TemplateAttributeDefinition(
                    name,
                    TemplateValueType.Character,
                    TemplateAttributeCatalog.GroupOf(name),
                    label: null));
        }

        return selected;
    }
}
