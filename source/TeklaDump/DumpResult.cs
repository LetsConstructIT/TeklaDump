using System;
using System.Collections.Generic;

namespace TeklaDump;

/// <summary>
/// Something that went wrong with one object, or one attribute, without being a reason to stop.
/// </summary>
/// <remarks>
/// One bad object never kills a run: the record is skipped, a warning is recorded, and the loop
/// continues. <see cref="DumpResult.ObjectsSkipped"/> is what makes that visible rather than silent.
/// </remarks>
public sealed class DumpWarning
{
    public DumpWarning(string code, string message, string? guid = null, string? objectType = null)
    {
        Code = code;
        Message = message;
        Guid = guid;
        ObjectType = objectType;
    }

    /// <summary>Stable, greppable identifier — see <c>schema/v1/README.md</c> for the list.</summary>
    public string Code { get; }

    public string Message { get; }

    /// <summary>The object this is about, when it is about one.</summary>
    public string? Guid { get; }

    public string? ObjectType { get; }

    public override string ToString() =>
        Guid is null ? $"{Code}: {Message}" : $"{Code}: {Message} ({ObjectType} {Guid})";
}

/// <summary>Progress for a bulk run. Raised between objects, never inside an interop call.</summary>
public sealed class DumpProgress
{
    public DumpProgress(int objectsWritten, int objectsSkipped, string phase)
    {
        ObjectsWritten = objectsWritten;
        ObjectsSkipped = objectsSkipped;
        Phase = phase;
    }

    public int ObjectsWritten { get; }
    public int ObjectsSkipped { get; }

    /// <summary>"scanning", "report-join", "writing" — what the run is currently doing.</summary>
    public string Phase { get; }
}

/// <summary>Outcome of <see cref="DumpWriter.Bulk"/>.</summary>
public sealed class DumpResult
{
    internal DumpResult(
        int objectsWritten,
        int objectsSkipped,
        string attributeTier,
        IReadOnlyList<DumpWarning> warnings,
        TimeSpan elapsed)
    {
        ObjectsWritten = objectsWritten;
        ObjectsSkipped = objectsSkipped;
        AttributeTier = attributeTier;
        Warnings = warnings;
        Elapsed = elapsed;
    }

    public int ObjectsWritten { get; }

    public int ObjectsSkipped { get; }

    /// <summary>"T0" | "T1" | "T2" — mirrors the header, so output is explainable after the fact.</summary>
    public string AttributeTier { get; }

    public IReadOnlyList<DumpWarning> Warnings { get; }

    public TimeSpan Elapsed { get; }
}
