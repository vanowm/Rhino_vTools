// Minimal reproducible example for Rhino forum
// Goal: GetObject loop that
//   1. Accepts pre-selected objects as initial selection
//   2. Lets user add/remove objects from the selection
//   3. Lets user toggle options without losing the accumulated selection
//   4. Confirms with Enter
//
// Problem: we cannot find a reliable way to detect when user DESELECTS
// an already-highlighted object. Rhino appears to deselect it from the
// document but does not report it through go.Object(i) or any obvious API.
//
// Tested approaches (all failed):
//   A. Single GetObject.GetMultiple loop: re-clicks on collected objects
//      are not reported in go.Object(i); IsSelected check after call
//      does not catch them reliably.
//   B. Single GetObject.Get() per click: same silent-deselect problem —
//      clicking an already-selected object deselects it without returning
//      it as GetResult.Object.
//   C. Checking obj.IsSelected(false) after each Get()/GetMultiple() call:
//      sometimes the state is already reset before we read it, or the
//      object is re-selected by the next doc.Objects.Select() call.
//
// Question: What is the correct Rhino 8 RhinoCommon pattern to implement
// a true add/remove toggle selection with persistent options?

using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Input;
using Rhino.Input.Custom;
using System;
using System.Collections.Generic;
using System.Linq;

public class SelectWithOptionsCommand : Command
{
    public override string EnglishName => "MRE_SelectWithOptions";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        // ── Option state ─────────────────────────────────────────────────
        var toggle = new OptionToggle(false, "No", "Yes");

        // ── Phase A: capture any pre-selected objects ─────────────────────
        var goPre = new GetObject();
        goPre.GeometryFilter = ObjectType.Curve;
        goPre.EnablePreSelect(true, false);
        goPre.EnablePostSelect(false);
        goPre.EnableUnselectObjectsOnExit(false);
        goPre.AcceptNothing(true);
        goPre.GetMultiple(0, 0);

        var collectedIds = new HashSet<Guid>();
        var collectedRefs = new Dictionary<Guid, ObjRef>();
        for (int i = 0; i < goPre.ObjectCount; i++)
        {
            var r = goPre.Object(i);
            if (collectedIds.Add(r.ObjectId))
                collectedRefs[r.ObjectId] = r;
        }

        // Keep pre-selected objects visually highlighted
        foreach (var id in collectedIds)
            doc.Objects.Select(id, true);

        // ── Phase B: interactive add / remove ─────────────────────────────
        // We want:
        //   - Click unselected object  → add to collection (highlight it)
        //   - Click selected object    → remove from collection (unhighlight it)
        //   - Toggle option            → update option, keep selection intact
        //   - Press Enter              → confirm and exit loop
        //
        // PROBLEM: no approach we've tried reliably detects the "click to
        // deselect" case. See notes at top of file.

        int iteration = 0;
        while (true)
        {
            var go = new GetObject();
            go.SetCommandPrompt("Select curves. Press Enter when done");
            go.GeometryFilter = ObjectType.Curve;
            go.SubObjectSelect = false;
            go.EnablePreSelect(false, false);       // don't auto-accept
            go.DeselectAllBeforePostSelect = false; // keep highlights
            go.AcceptNothing(true);
            go.EnableUnselectObjectsOnExit(false);
            go.AddOptionToggle("MyOption", ref toggle);

            var res = go.Get();
            RhinoApp.WriteLine($"iter {++iteration}: result={res}  ObjectCount={go.ObjectCount}");

            if (res == GetResult.Cancel)
            {
                foreach (var id in collectedIds) doc.Objects.Select(id, false);
                doc.Views.Redraw();
                return Result.Cancel;
            }

            if (res == GetResult.Nothing)
            {
                // ── PROBLEM: if user clicked an already-selected object,
                // Rhino silently deselects it and returns Nothing here.
                // We cannot distinguish that from a genuine Enter press.
                // Checking IsSelected below sometimes catches it, sometimes not.

                var silentDeselects = collectedIds
                    .Where(id => (doc.Objects.FindId(id)?.IsSelected(false) ?? 0) == 0)
                    .ToList();

                if (silentDeselects.Count > 0)
                {
                    // Treat as deselect click, not Enter — but is this reliable?
                    foreach (var id in silentDeselects)
                    {
                        collectedIds.Remove(id);
                        collectedRefs.Remove(id);
                        RhinoApp.WriteLine($"  silent-deselect: {id.ToString()[..8]}");
                    }
                    doc.Views.Redraw();
                    continue; // keep loop going
                }

                break; // genuine Enter
            }

            if (res == GetResult.Option)
            {
                RhinoApp.WriteLine($"  option: MyOption={toggle.CurrentValue}");
                continue;
            }

            if (res == GetResult.Object)
            {
                var r   = go.Object(0);
                var pid = r.ObjectId;
                if (collectedIds.Contains(pid))
                {
                    // ── This branch is never reached in practice ──
                    // Clicking an already-highlighted object never returns
                    // GetResult.Object with that object; it goes through
                    // the silent-deselect path above (or does nothing).
                    collectedIds.Remove(pid);
                    collectedRefs.Remove(pid);
                    doc.Objects.Select(pid, false);
                    RhinoApp.WriteLine($"  toggle-off: {pid.ToString()[..8]}");
                }
                else
                {
                    collectedIds.Add(pid);
                    collectedRefs[pid] = r;
                    doc.Objects.Select(pid, true);
                    RhinoApp.WriteLine($"  toggle-on: {pid.ToString()[..8]}");
                }
                doc.Views.Redraw();
            }
        }

        RhinoApp.WriteLine($"Final selection: {collectedIds.Count} object(s)");
        doc.Views.Redraw();
        return Result.Success;
    }
}
