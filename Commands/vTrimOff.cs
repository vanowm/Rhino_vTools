using System.Collections.Generic;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace vTools.Commands;

/// <summary>
/// Trims selected curves to the outer boundary of the enclosed region they collectively form.
/// Equivalent to CurveBoolean with the outermost region auto-selected.
/// </summary>
public sealed class vTrimOff : Command
{
  public override string EnglishName => "vTrimOff";

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    var go = new GetObject();
    go.SetCommandPrompt("Select curves to trim");
    go.GeometryFilter = ObjectType.Curve;
    go.SubObjectSelect = false;
    go.GroupSelect = true;
    go.GetMultiple(2, 0);

    if (go.CommandResult() != Result.Success)
      return go.CommandResult();

    var objRefs = new List<ObjRef>();
    var curves = new List<Curve>();

    for (var i = 0; i < go.ObjectCount; i++)
    {
      var objRef = go.Object(i);
      if (objRef.Curve() is { } crv)
      {
        objRefs.Add(objRef);
        curves.Add(crv);
      }
    }

    if (curves.Count < 2)
    {
      RhinoApp.WriteLine("vTrimOff: select at least 2 curves.");
      return Result.Nothing;
    }

    var plane = doc.Views.ActiveView?.ActiveViewport.ConstructionPlane() ?? Plane.WorldXY;
    var tol = doc.ModelAbsoluteTolerance;

    // Find the combined outer boundary of all enclosed regions.
    // combineRegions:true merges all adjacent finite regions into one outer boundary.
    var regions = Curve.CreateBooleanRegions(curves, plane, combineRegions: true, tol);

    if (regions == null || regions.RegionCount == 0)
    {
      RhinoApp.WriteLine("vTrimOff: no enclosed region found in the selected curves.");
      return Result.Nothing;
    }

    var boundaryCurves = new List<Curve>();
    for (var r = 0; r < regions.RegionCount; r++)
    {
      var rc = regions.RegionCurves(r);
      if (rc == null) continue;
      foreach (var c in rc)
        if (c != null) boundaryCurves.Add(c);
    }

    if (boundaryCurves.Count == 0)
    {
      RhinoApp.WriteLine("vTrimOff: failed to extract boundary curves.");
      return Result.Failure;
    }

    // Inherit attributes from the first selected object (preserves layer, color, etc.)
    var attr = objRefs[0].Object()?.Attributes?.Duplicate() ?? new ObjectAttributes();

    foreach (var objRef in objRefs)
      doc.Objects.Delete(objRef.ObjectId, true);

    foreach (var crv in boundaryCurves)
      doc.Objects.AddCurve(crv, attr);

    doc.Views.Redraw();
    return Result.Success;
  }
}
