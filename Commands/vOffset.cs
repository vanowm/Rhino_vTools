using System;
using Rhino;
using Rhino.Commands;

namespace vTools.Commands;

/// <summary>
/// Native offset command ported from Offset.py.
/// Delegates to built-in _Offset and clears selection after each run.
/// Pressing Enter repeats vOffset so the workflow stays continuous.
/// </summary>
public sealed class vOffset : Command
{
  private static EventHandler? _pendingOffsetIdleHandler;

  /// <summary>
  /// Rhino command name.
  /// </summary>
  public override string EnglishName => "vOffset";

  /// <summary>
  /// Queues the built-in _Offset command via idle handler and returns immediately,
  /// keeping vOffset as Rhino's last command so Enter-repeat re-runs vOffset.
  /// </summary>
  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
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

    // echo:false keeps vOffset as Rhino's last command so Enter-repeat re-runs vOffset.
    RhinoApp.RunScript("_Offset", false);

    doc.Objects.UnselectAll();
    doc.Views.Redraw();
  }
}
