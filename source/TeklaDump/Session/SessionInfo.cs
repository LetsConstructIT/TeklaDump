using System;
using System.Collections.Generic;
using Tekla.Structures;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;
using Tekla.Structures.Model.Operations;
using TeklaDump.Interop;
using TSIdentifier = Tekla.Structures.Identifier;

namespace TeklaDump.Session;

/// <summary>
/// The only class in the library that holds a <see cref="Model"/>.
/// </summary>
/// <remarks>
/// The library's non-goal is precise: no enumeration, no selection, no filtering, no work-plane
/// changes, no database writes. Reading SESSION METADATA for the header is allowed, and it is
/// isolated here so that stays checkable by reading one file. Everything this class does is a
/// read, and every one of them is wrapped: a header field that cannot be read is omitted, never
/// fatal, because a dump is still useful without a build number.
/// </remarks>
public sealed class SessionInfo
{
    private readonly Model _model;

    private SessionInfo(Model model)
    {
        _model = model;
    }

    /// <summary>
    /// Reads the session, or returns null when there is no Tekla to read. A null
    /// <see cref="SessionInfo"/> is a supported state: the dump then carries a header with the
    /// tool and schema fields only, which is exactly what a unit test or an offline caller gets.
    /// </summary>
    public static SessionInfo? TryCreate()
    {
        try
        {
            var model = new Model();
            return model.GetConnectionStatus() ? new SessionInfo(model) : null;
        }
        catch (Exception)
        {
            // No Tekla assemblies, no session, no rights — all the same outcome here.
            return null;
        }
    }

    /// <summary>Wraps an existing model. For a host that already holds one (the CLI, the macro).</summary>
    public static SessionInfo FromModel(Model model) => new SessionInfo(model);

    public string? ModelName => Info(info => info.ModelName);

    /// <summary>Off the header unless <c>IncludeSessionDetails</c> is on — it names a customer's disk.</summary>
    public string? ModelPath => Info(info => info.ModelPath);

    public int? CurrentPhase
    {
        get
        {
            try { return _model.GetInfo().CurrentPhase; }
            catch (Exception) { return null; }
        }
    }

    public bool? SharedModel
    {
        get
        {
            try { return _model.GetInfo().SharedModel; }
            catch (Exception) { return null; }
        }
    }

    public string? TeklaVersion => Safe(() => TeklaStructuresInfo.GetCurrentProgramVersion());

    public string? BuildNumber => Safe(() => TeklaStructuresInfo.GetBuildNumber());

    /// <summary>The TEKLA user name, not the Windows account. Session-details gated, same as the path.</summary>
    public string? User => Safe(() => TeklaStructuresInfo.GetCurrentUser());

    /// <summary>
    /// Whether numbering is current. Stamped because position and mark attributes
    /// (<c>PART_POS</c>, <c>ASSEMBLY_POS</c>, the bolt fields) are EMPTY until numbering has run,
    /// and there is no API to run it. A dump with stale numbering is not wrong, it is incomplete
    /// in a way only this flag explains.
    /// </summary>
    public bool? NumberingUpToDate
    {
        get
        {
            try { return Operation.IsNumberingUpToDateAll(); }
            catch (Exception) { return null; }
        }
    }

    /// <summary>
    /// The environment name, or null when nothing answers.
    /// </summary>
    /// <remarks>
    /// UNVERIFIED advanced-option names, so the code treats them as a probe: several candidates
    /// are tried in order and the field is simply omitted when none answers, rather than a guess
    /// being stamped into the header as fact.
    /// </remarks>
    public string? Environment => FirstOption("XS_ENVIRONMENT", "XS_ENVIRONMENT_NAME", "XS_ENV");

    /// <summary>The role (steel, precast, ...), or null when nothing answers. Same caveat as above.</summary>
    public string? Role => FirstOption("XS_ROLE", "XS_ROLE_NAME");

    /// <summary>
    /// "global" or "custom". Open API coordinates are expressed in the CURRENT work plane, so a
    /// dump taken while a user has a local plane set regenerates in the wrong place unless the
    /// consumer knows. The library only reports what it saw; the CLI and the macro switch to
    /// global first and restore in a finally.
    /// </summary>
    public WorkPlaneState WorkPlane
    {
        get
        {
            try
            {
                var plane = _model.GetWorkPlaneHandler().GetCurrentTransformationPlane();
                return WorkPlaneState.FromMatrix(plane.TransformationMatrixToGlobal);
            }
            catch (Exception)
            {
                return WorkPlaneState.Unknown;
            }
        }
    }

    /// <summary>
    /// The GUID of an object whose <see cref="TSIdentifier.GUID"/> is empty. ID-only identifiers
    /// exist (an object fetched by ID, or one handed over by some older API), and this is the only
    /// way to turn one into the stable identity the schema promises.
    /// </summary>
    public string? ResolveGuid(ModelObject modelObject) => ResolveGuid(modelObject.Identifier);

    /// <summary>
    /// The same for a bare <see cref="TSIdentifier"/>. Component input hands identifiers back on
    /// some versions, and this turns one into the schema's identity without selecting the object.
    /// </summary>
    public string? ResolveGuid(TSIdentifier identifier)
    {
        try
        {
            var resolved = _model.GetGUIDByIdentifier(identifier);
            if (string.IsNullOrEmpty(resolved)) return null;
            return Guid.TryParse(resolved, out var parsed) && parsed != Guid.Empty
                ? parsed.ToString()
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private string? Info(Func<ModelInfo, string> read)
    {
        try
        {
            var value = read(_model.GetInfo());
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? Safe(Func<string> read)
    {
        try
        {
            var value = read();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? FirstOption(params string[] names)
    {
        foreach (var name in names)
        {
            try
            {
                var value = string.Empty;
                if (TeklaStructuresSettings.GetAdvancedOption(name, ref value) &&
                    !string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
            catch (Exception)
            {
                // Older or oddly-configured Tekla — treat as "option not set" and try the next.
            }
        }

        return null;
    }
}

/// <summary>The work plane as the header reports it.</summary>
public sealed class WorkPlaneState
{
    private WorkPlaneState(string name, Point? origin, IReadOnlyList<Vector>? axes)
    {
        Name = name;
        Origin = origin;
        Axes = axes;
    }

    /// <summary>"global", "custom", or "unknown" when the handler could not be read.</summary>
    public string Name { get; }

    public Point? Origin { get; }

    /// <summary>X and Y axes of a custom plane; null for the global one.</summary>
    public IReadOnlyList<Vector>? Axes { get; }

    public bool IsGlobal => Name == "global";

    public static WorkPlaneState Unknown { get; } = new WorkPlaneState("unknown", null, null);

    public static WorkPlaneState Global { get; } = new WorkPlaneState("global", null, null);

    /// <summary>
    /// Classifies a transformation matrix.
    /// </summary>
    /// <remarks>
    /// Tekla's <c>Matrix</c> is 4 rows by 3 columns and multiplies row vectors (p' = p * M), so
    /// rows 0..2 are the axes and row 3 is the translation. The global plane is therefore the
    /// identity rotation with a zero translation.
    /// </remarks>
    public static WorkPlaneState FromMatrix(Matrix matrix)
    {
        if (matrix is null) return Unknown;

        try
        {
            var isIdentity = true;
            for (var row = 0; row < 3 && isIdentity; row++)
            {
                for (var column = 0; column < 3; column++)
                {
                    var expected = row == column ? 1d : 0d;
                    if (Math.Abs(matrix[row, column] - expected) > Tolerance) { isIdentity = false; break; }
                }
            }

            var origin = new Point(matrix[3, 0], matrix[3, 1], matrix[3, 2]);
            if (isIdentity &&
                Math.Abs(origin.X) <= Tolerance &&
                Math.Abs(origin.Y) <= Tolerance &&
                Math.Abs(origin.Z) <= Tolerance)
            {
                return Global;
            }

            var axes = new[]
            {
                new Vector(matrix[0, 0], matrix[0, 1], matrix[0, 2]),
                new Vector(matrix[1, 0], matrix[1, 1], matrix[1, 2]),
            };
            return new WorkPlaneState("custom", origin, axes);
        }
        catch (Exception)
        {
            return Unknown;
        }
    }

    /// <summary>Loose enough for accumulated floating point, tight enough that a real 1 mm shift shows.</summary>
    private const double Tolerance = 1e-6;
}
