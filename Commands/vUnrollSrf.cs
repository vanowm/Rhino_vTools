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
  public override string EnglishName => "vUnrollSrf";

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    // Snapshot all existing object IDs (visible or hidden) so we can identify
    // what UnrollSrf adds.
    var snapshot = new HashSet<Guid>(
      doc.Objects.GetObjectList(new ObjectEnumeratorSettings
      {
        IncludeGrips   = false,
        DeletedObjects = false
      }).Select(o => o.Id));

    // Delegate entirely to the built-in command, preserving all its prompts
    // and options.  RunScript blocks until the user finishes the interaction.
    if (!RhinoApp.RunScript("_UnrollSrf", false))
      return Result.Cancel;

    // Find objects added by UnrollSrf that are not TextDots.
    // TextDots are the labels Rhino places on the original 3D surface for
    // curve/point correspondence — they should stay unselected.
    var newObjects = doc.Objects
      .GetObjectList(new ObjectEnumeratorSettings
      {
        IncludeGrips   = false,
        DeletedObjects = false,
        VisibleFilter  = true
      })
      .Where(o => !snapshot.Contains(o.Id) && o.Geometry is not TextDot)
      .ToList();

    if (newObjects.Count == 0)
      return Result.Success;

    doc.Objects.UnselectAll();
    foreach (var obj in newObjects)
      obj.Select(true);

    doc.Views.Redraw();
    return Result.Success;
  }
}
