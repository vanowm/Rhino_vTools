using System;
using Rhino;
using Rhino.Commands;

namespace vTools.Commands;

public sealed class vOffset : Command
{
  private static bool _restartingAfterOffsetDelegate;
  private static EventHandler? _pendingOffsetIdleHandler;

  public override string EnglishName => "vOffset";

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    // Silent no-op re-run after delegating to _Offset — registers vOffset as the
    // repeatable last command without showing any prompt.
    if (_restartingAfterOffsetDelegate)
    {
      _restartingAfterOffsetDelegate = false;
      return Result.Success;
    }

    CancelPendingOffset();
    _pendingOffsetIdleHandler = OnLaunchOffsetOnIdle;
    RhinoApp.Idle += _pendingOffsetIdleHandler;
    return Result.Success;
  }

  private static void CancelPendingOffset()
  {
    if (_pendingOffsetIdleHandler != null)
    {
      RhinoApp.Idle -= _pendingOffsetIdleHandler;
      _pendingOffsetIdleHandler = null;
    }
  }

  private static void OnLaunchOffsetOnIdle(object? sender, EventArgs e)
  {
    CancelPendingOffset();

    var doc = RhinoDoc.ActiveDoc;
    if (doc == null)
      return;

    var ok = RhinoApp.RunScript("_Offset", false);

    doc.Objects.UnselectAll();
    doc.Views.Redraw();

    // Silently re-run vOffset (restart flag set so RunCommand returns immediately)
    // so that pressing Enter afterward repeats vOffset, not _Offset.
    _restartingAfterOffsetDelegate = true;
    _ = RhinoApp.RunScript("_vOffset", false);
    _restartingAfterOffsetDelegate = false; // safety clear if RunScript didn't invoke us

    // Re-queue for the next iteration as long as the user didn't cancel.
    if (ok)
    {
      _pendingOffsetIdleHandler = OnLaunchOffsetOnIdle;
      RhinoApp.Idle += _pendingOffsetIdleHandler;
    }
  }
}
