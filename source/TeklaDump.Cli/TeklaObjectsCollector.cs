using System;
using System.Collections.Generic;
using System.Linq;
using Tekla.Structures;
using Tekla.Structures.Model;
using TSUI = Tekla.Structures.Model.UI;

namespace TeklaDump.Cli;

/// <summary>
/// Reads model objects from the running session. No writes — purely a query surface.
/// </summary>
/// <remarks>
/// Lifted from TeklaLookup and deliberately placed in the CLI, not the library. Enumeration,
/// selection and filtering are the caller's job; a library that could reach the model would grow
/// into a query engine, and <c>Tekla.Structures.Model.UI</c> (which <c>--selected</c> needs)
/// requires a UI session that a plugin host may not have.
/// </remarks>
internal sealed class TeklaObjectsCollector
{
    private readonly Model _model = new Model();

    public Model Model => _model;

    public bool IsConnected
    {
        get
        {
            try { return _model.GetConnectionStatus(); }
            catch (Exception) { return false; }
        }
    }

    public IEnumerable<ModelObject> GetAllObjects()
    {
        var enumerator = _model.GetModelObjectSelector().GetAllObjects();
        while (enumerator.MoveNext())
            if (enumerator.Current is ModelObject modelObject) yield return modelObject;
    }

    /// <summary>By CLR type. Tekla resolves the matching internal type enum itself.</summary>
    public IEnumerable<ModelObject> GetObjectsOfTypes(Type[] types)
    {
        if (types is null || types.Length == 0) yield break;

        var enumerator = _model.GetModelObjectSelector().GetAllObjectsWithType(types);
        while (enumerator.MoveNext())
            if (enumerator.Current is ModelObject modelObject) yield return modelObject;
    }

    /// <summary>A saved selection filter, by name — the filter files the user already maintains.</summary>
    public IEnumerable<ModelObject> GetObjectsByFilterName(string filterName)
    {
        var enumerator = _model.GetModelObjectSelector().GetObjectsByFilterName(filterName);
        while (enumerator.MoveNext())
            if (enumerator.Current is ModelObject modelObject) yield return modelObject;
    }

    /// <summary>What the user has selected in the Tekla UI. Needs a UI session.</summary>
    public IEnumerable<ModelObject> GetSelectedObjects()
    {
        var enumerator = new TSUI.ModelObjectSelector().GetSelectedObjects();
        while (enumerator.MoveNext())
            if (enumerator.Current is ModelObject modelObject) yield return modelObject;
    }

    /// <summary>Resolves GUIDs, reporting the ones that matched nothing rather than dropping them.</summary>
    public IEnumerable<ModelObject> GetObjectsByGuids(IEnumerable<string> guids, IList<string> unresolved)
    {
        foreach (var raw in guids)
        {
            var token = raw?.Trim();
            if (string.IsNullOrEmpty(token)) continue;

            ModelObject? resolved = null;
            try
            {
                var identifier = _model.GetIdentifierByGUID(token);
                if (identifier.ID != 0) resolved = _model.SelectModelObject(identifier);
            }
            catch (Exception)
            {
                resolved = null;
            }

            if (resolved is null) unresolved.Add(token!);
            else yield return resolved;
        }
    }

    /// <summary>
    /// Maps a CLR type NAME as typed on the command line to the type itself.
    /// </summary>
    /// <remarks>
    /// Only types in <c>Tekla.Structures.Model</c> are considered, and only ones deriving from
    /// <see cref="ModelObject"/>: <c>--type</c> is a convenience, not an arbitrary type loader.
    /// </remarks>
    public static Type? ResolveType(string name)
    {
        var assembly = typeof(ModelObject).Assembly;
        return assembly.GetTypes().FirstOrDefault(candidate =>
            typeof(ModelObject).IsAssignableFrom(candidate) &&
            !candidate.IsAbstract &&
            string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The phase of an object, for <c>--phase</c>. Null when it has none.</summary>
    public static int? PhaseOf(ModelObject modelObject)
    {
        try
        {
            Phase? phase = null;
            return modelObject.GetPhase(out phase) && phase is not null ? phase.PhaseNumber : (int?)null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
