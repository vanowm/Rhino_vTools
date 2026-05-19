using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace vTools.Commands;

/// <summary>
/// Runs the built-in UnrollSrf command and selects all newly created flat
/// objects on completion. TextDot labels placed on the original 3D surface
/// by UnrollSrf are excluded from the post-command selection.
/// </summary>
public sealed class vUnrollSrf : Command
{
  private static bool _restartingAfterDelegate;
  private static EventHandler? _pendingIdleHandler;
  private static HashSet<Guid>? _snapshot;

  public override string EnglishName => "vUnrollSrf";

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    // Silent no-op re-run after delegating — registers vUnrollSrf as the
    // repeatable last command without running anything.
    if (_restartingAfterDelegate)
    {
      _restartingAfterDelegate = false;
      return Result.Success;
    }

    // Snapshot before UnrollSrf adds anything.
    _snapshot = new HashSet<Guid>(
      doc.Objects.GetObjectList(new ObjectEnumeratorSettings
      {
        IncludeGrips   = false,
        DeletedObjects = false
      }).Select(o => o.Id));

    CancelPending();
    _pendingIdleHandler = OnIdleLaunch;
    RhinoApp.Idle += _pendingIdleHandler;
    return Result.Success;
  }

  private static void CancelPending()
  {
    if (_pendingIdleHandler != null)
    {
      RhinoApp.Idle -= _pendingIdleHandler;
      _pendingIdleHandler = null;
    }
  }

  private static void OnIdleLaunch(object? sender, EventArgs e)
  {
    CancelPending();

    var doc = RhinoDoc.ActiveDoc;
    if (doc == null) return;

    var snapshot = _snapshot ?? new HashSet<Guid>();
    _snapshot = null;

    var ok = RhinoApp.RunScript("_UnrollSrf", false);

    if (ok)
    {
      // Collect objects added by UnrollSrf that are not TextDots.
      // TextDots are the correspondence labels Rhino places on the original
      // 3D surface — they should not be selected.
      var newObjects = doc.Objects
        .GetObjectList(new ObjectEnumeratorSettings
        {
          IncludeGrips   = false,
          DeletedObjects = false,
          VisibleFilter  = true
        })
        .Where(o => !snapshot.Contains(o.Id) && o.Geometry is not TextDot)
        .ToList();

      if (newObjects.Count > 0)
      {
        doc.Objects.UnselectAll();
        foreach (var obj in newObjects)
          obj.Select(true);
        doc.Views.Redraw();
      }
    }

    // Silently re-run vUnrollSrf so pressing Enter repeats it, not _UnrollSrf.
    _restartingAfterDelegate = true;
    _ = RhinoApp.RunScript("_vUnrollSrf", false);
    _restartingAfterDelegate = false;
  }
}
