using System;
using Tekla.Structures.Model;

namespace TeklaDump.Cli;

/// <summary>
/// Switches the session to the global work plane for the duration of a dump and puts the user's
/// plane back afterwards.
/// </summary>
/// <remarks>
/// Open API coordinates are expressed in the CURRENT work plane. A dump taken while the user has a
/// local plane set is not wrong exactly — the header says <c>"workPlane":"custom"</c> — but every
/// point in it regenerates in the wrong place, silently, in code written a week later. So the CLI
/// and the macro normalize to global and restore in a <c>finally</c>; the library only reports what
/// it saw, because changing a user's session is not a library's business.
/// </remarks>
internal sealed class GlobalWorkPlane : IDisposable
{
    private readonly WorkPlaneHandler? _handler;
    private readonly TransformationPlane? _previous;

    private GlobalWorkPlane(WorkPlaneHandler? handler, TransformationPlane? previous)
    {
        _handler = handler;
        _previous = previous;
    }

    /// <summary>True when the session was actually on a local plane and we changed it.</summary>
    public bool Changed { get; private set; }

    public static GlobalWorkPlane Enter(Model model, out string? notice)
    {
        notice = null;

        try
        {
            var handler = model.GetWorkPlaneHandler();
            var previous = handler.GetCurrentTransformationPlane();

            var state = TeklaDump.Session.WorkPlaneState.FromMatrix(previous.TransformationMatrixToGlobal);
            if (state.IsGlobal) return new GlobalWorkPlane(handler, null);

            handler.SetCurrentTransformationPlane(new TransformationPlane());
            notice = "Work plane was local; switched to global for the dump and will restore it.";
            return new GlobalWorkPlane(handler, previous) { Changed = true };
        }
        catch (Exception ex)
        {
            notice = "Could not read or set the work plane (" + ex.Message +
                     "). Coordinates are in whatever plane the session is on; see the header.";
            return new GlobalWorkPlane(null, null);
        }
    }

    public void Dispose()
    {
        if (_handler is null || _previous is null) return;

        try
        {
            _handler.SetCurrentTransformationPlane(_previous);
        }
        catch (Exception)
        {
            // Leaving the user on the global plane is a visible, harmless state; throwing here
            // would replace a finished dump with a stack trace.
        }
    }
}
