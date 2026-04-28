using System;
using System.Collections.Generic;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input.Custom;

namespace vTools.Commands;

/// <summary>
/// Native text flip/rotate command ported from TextFlip.py.
/// </summary>
public sealed class vTextFlip : Command
{
  /// <summary>
  /// Rhino command name.
  /// </summary>
  public override string EnglishName => "vTextFlip";

  /// <summary>
  /// Flips/rotates selected annotation text with a persistent command loop.
  /// </summary>
  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    var selectedIds = GetSelectedAnnotationIds(doc);

    // Match Python startup behavior: preselected text flips immediately.
    if (selectedIds.Count > 0)
    {
      selectedIds = DoFlip(doc, selectedIds, world: false);
      selectedIds = DoRotate(doc, selectedIds, Math.PI);
      Highlight(doc, selectedIds);
    }

    while (true)
    {
      var go = new GetObject();
      go.SetCommandPrompt("Select text or choose action");
      go.SetCommandPromptDefault("Enter to exit");
      go.GeometryFilter = ObjectType.Annotation;
      go.AcceptNothing(true);
      go.EnablePreSelect(false, true);
      go.EnableClearObjectsOnEntry(false);
      go.EnableUnselectObjectsOnExit(false);
      go.DeselectAllBeforePostSelect = false;

      var flipIndex = go.AddOption("Flip");
      var rotateIndex = go.AddOption("Rotate");
      var clearIndex = go.AddOption("Clear");

      Highlight(doc, selectedIds);

      var result = go.Get();
      if (go.CommandResult() != Result.Success)
        return Result.Cancel;

      if (result == Rhino.Input.GetResult.Object)
      {
        var objRef = go.Object(0);
        if (objRef != null)
          Toggle(selectedIds, objRef.ObjectId);

        Highlight(doc, selectedIds);
        continue;
      }

      if (result == Rhino.Input.GetResult.Option)
      {
        if (go.OptionIndex() == flipIndex)
        {
          if (selectedIds.Count == 0)
          {
            RhinoApp.WriteLine("vTextFlip: no objects selected.");
            continue;
          }

          selectedIds = DoFlip(doc, selectedIds, world: false);
          selectedIds = DoRotate(doc, selectedIds, Math.PI);
          Highlight(doc, selectedIds);
          continue;
        }

        if (go.OptionIndex() == rotateIndex)
        {
          if (selectedIds.Count == 0)
          {
            RhinoApp.WriteLine("vTextFlip: no objects selected.");
            continue;
          }

          selectedIds = DoRotate(doc, selectedIds, Math.PI * 0.5);
          Highlight(doc, selectedIds);
          continue;
        }

        if (go.OptionIndex() == clearIndex)
        {
          selectedIds.Clear();
          Highlight(doc, selectedIds);
          continue;
        }
      }

      if (result == Rhino.Input.GetResult.Nothing || result == Rhino.Input.GetResult.Cancel)
        break;
    }

    return Result.Success;
  }

  private static List<Guid> GetSelectedAnnotationIds(RhinoDoc doc)
  {
    var ids = new List<Guid>();
    foreach (var obj in doc.Objects.GetSelectedObjects(false, false))
    {
      if (obj == null || obj.ObjectType != ObjectType.Annotation)
        continue;
      ids.Add(obj.Id);
    }

    return ids;
  }

  private static void Toggle(List<Guid> ids, Guid id)
  {
    var index = ids.IndexOf(id);
    if (index >= 0)
      ids.RemoveAt(index);
    else
      ids.Add(id);
  }

  private static void Highlight(RhinoDoc doc, IReadOnlyList<Guid> ids)
  {
    doc.Objects.UnselectAll();
    foreach (var id in ids)
      doc.Objects.Select(id, true);
    doc.Views.Redraw();
  }

  private static List<Guid> DoFlip(RhinoDoc doc, IReadOnlyList<Guid> ids, bool world)
  {
    var newIds = new List<Guid>();

    foreach (var id in ids)
    {
      var obj = doc.Objects.FindId(id);
      if (obj == null)
        continue;

      Transform transform;
      if (world)
      {
        transform = Transform.Scale(Plane.WorldXY, 1.0, 1.0, -1.0);
      }
      else
      {
        var plane = ObjectPlaneFor(obj);
        transform = Transform.Rotation(Math.PI, plane.XAxis, plane.Origin);
      }

      var newId = doc.Objects.Transform(obj.Id, transform, true);
      if (newId != Guid.Empty)
        newIds.Add(newId);
    }

    return newIds;
  }

  private static List<Guid> DoRotate(RhinoDoc doc, IReadOnlyList<Guid> ids, double angleRadians)
  {
    var newIds = new List<Guid>();

    foreach (var id in ids)
    {
      var obj = doc.Objects.FindId(id);
      if (obj == null)
        continue;

      var plane = ObjectPlaneFor(obj);
      var transform = Transform.Rotation(angleRadians, plane.Normal, plane.Origin);
      var newId = doc.Objects.Transform(obj.Id, transform, true);
      if (newId != Guid.Empty)
        newIds.Add(newId);
    }

    return newIds;
  }

  private static Plane ObjectPlaneFor(RhinoObject obj)
  {
    if (obj.Geometry is TextEntity text)
      return text.Plane;

    try
    {
      var bbox = obj.Geometry.GetBoundingBox(true);
      if (bbox.IsValid)
        return new Plane(bbox.Center, Vector3d.ZAxis);
    }
    catch
    {
    }

    return Plane.WorldXY;
  }
}
