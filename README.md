# vTools

vTools is a Rhino 8 plug-in project (C# / .NET 7) that provides native RhinoCommon commands for zipper, orient, trim/extend, gumball, curve, line, and text workflows.

## What this project includes

- Rhino plug-in entry point: vToolsPlugIn
- Native commands:
  - [vCurveToSpline](#vcurvetospline-flow) *(26.04.24.093442)* — converts selected curves to interpolated splines with join modes
  - [vFitBox](#vfitbox-flow) *(26.04.24.093442)* — finds the minimum bounding box for selected objects by optimizing rotation angle
  - [vLine](#vline-flow) *(26.04.27.212532)* — draws lines with chain modes, angle lock, length constraint, and perp/tangent endpoint solving
  - [vLineLength](#vlinelength-flow) *(26.04.27.212532)* — resizes an open curve to a target total, additive, or subtractive length
  - [vMiddleCurve](#vmiddlecurve-flow) *(26.04.27)* — creates an interpolated curve equidistant between two selected curves
  - [vOffset](#voffset-flow) *(26.04.27)* — runs built-in Offset in a continuous loop, clearing selection after each run
  - [vOrient2pt](#vorient2pt-flow) *(26.04.24.093442)* — orients objects from a source two-point frame to a target two-point frame
  - [vOrient3pt](#vorient3pt-flow) *(26.04.24.093442)* — orients objects from a source three-point frame to a target three-point frame
  - [vPointNormalToSurface](#vpointnormaltosurface-flow) *(26.04.27.210936)* — places points projected onto the closest surface normal evaluation point
  - [vRectangle](#vrectangle-flow) *(26.04.27)* — creates an axis-aligned rectangle polyline from width/height inputs driven by numeric value or selected curve lengths
  - [vScallop](#vscallop-flow) *(26.04.27.212532)* — creates an arc scallop between two points or along a selected line
  - [vSplitAtCorners](#vsplitatcorners-flow) *(26.04.27.212532)* — splits curves at detected corners with interactive per-corner toggle preview
  - [vTextAligned](#vtextaligned-flow) *(26.04.27.212532)* — places or repositions annotation text aligned and offset along a selected curve
  - [vTextFlip](#vtextflip-flow) *(26.04.27.212532)* — flips or rotates annotation text around its object plane
  - [vTogglePerpGumball](#vtoggleperpgumball-flow) *(26.04.24.171217)* — toggles a monitor that auto-orients the gumball perpendicular to selected control point grips
  - [vTrim](#vtrim-flow) *(26.04.24.163301)* — trims and extends curves with auto-cutter detection, join, and undo/redo
  - [vUzip](#vuzip-flow) *(26.04.24.093442)* — creates U-zip parts from a center curve into labeled reference, plot, and cut output groups
- Shared command configuration file: vTools.config.json
- Runtime command diagnostics in a local logs folder

## Requirements

- Rhino 8 (for RhinoCommon.dll)
- .NET SDK 7.0+
- Windows

## Build

From this folder:

```powershell
dotnet build .\vTools.csproj -c Release
```

Build behavior:

1. Release builds fail fast if output files are locked (for example, if Rhino holds `vTools.dll`).
2. After every successful Release build, a timestamped backup snapshot is created automatically.

Compile command:

1. Standard Release build:

```powershell
dotnet build d:/github/rhino/vTools/vTools.csproj -c Release
```

## Output

Release output is written to:

- bin/Release/net7.0-windows/vTools.dll
- bin/Release/net7.0-windows/vTools.config.json
- Automatic backups: bin/Release/backups/YY.MM.DD.HHMMSS/

## Rhino usage

All command options persist by default unless stated otherwise.

Native commands: [vCurveToSpline](#vcurvetospline-flow), [vFitBox](#vfitbox-flow), [vLine](#vline-flow), [vLineLength](#vlinelength-flow), [vMiddleCurve](#vmiddlecurve-flow), [vOffset](#voffset-flow), [vOrient2pt](#vorient2pt-flow), [vOrient3pt](#vorient3pt-flow), [vPointNormalToSurface](#vpointnormaltosurface-flow), [vRectangle](#vrectangle-flow), [vScallop](#vscallop-flow), [vSplitAtCorners](#vsplitatcorners-flow), [vTextAligned](#vtextaligned-flow), [vTextFlip](#vtextflip-flow), [vTogglePerpGumball](#vtoggleperpgumball-flow), [vTrim](#vtrim-flow), [vUzip](#vuzip-flow).

1. Load the plug-in assembly in Rhino.
1. Run one of the native commands.

### vCurveToSpline flow

1. Select source curves (preselect or postselect is supported).
1. Set `Join` option.

    - `None`: one spline per selected curve.
    - `Connected`: one spline per connected curve island.
    - `All`: one spline through all selected curves.

1. Confirm to create interpolated curve output and select results.

### vFitBox flow

1. Select objects to fit.
1. Adjust options in command line.

    - `AngleStep`: sampling step in degrees and accepts direct numeric input during object picking.
    - `Rotate`: applies final rotation to both fit box and selected objects, keeps the longest in-plane side horizontal, and prefers the equivalent orientation that avoids unnecessary 180-degree flips.
    - `Fit`: optimize by `Height` or `Area`.

1. Confirm selection to generate the fit result.

### vLine flow

1. Run `vLine`.
1. Pick the start point.
1. Start-point options:

    - `Mode`: chain behavior for subsequent segments.
      - `Single`: create one segment and finish.
      - `Multiple`: after each segment, pick a fresh start point.
      - `Chained`: each next segment starts at the previous end point.
      - `Polyline`: build/update one polyline as you add vertices.
    - `BothSides`: creates a symmetric line centered on the picked start point.
    - `Normal`, `Angled`, `Vertical`, `FourPoint`, `Bisector`, `Perpendicular`, `Tangent`, `BiTangent`, `Extension`: delegates to Rhino native line variants.

1. Pick the end point.
1. End-point options:

    - `Perp`: solve endpoint perpendicular to the hovered curve under cursor.
    - `Tangent`: solve endpoint tangent to the hovered curve under cursor.
    - `PerpNear`: solve perpendicular against nearest curve.
    - `TanNear`: solve tangent against nearest curve.
    - `Auto`: choose perpendicular/tangent solution using `Priority`.
    - `Priority`: auto-mode choice policy.
      - `Closest`: whichever solution is closer to cursor.
      - `PerpFirst`: prefer perpendicular when available.
      - `TanFirst`: prefer tangent when available.
      - `KeepCurrent`: keep previous auto choice when possible.
    - `PersistConstraint`: keeps current constraint mode (`Perp`/`Tangent`/etc.) for following segments.
    - `Length`: forces segment length from start point.
    - `AngleLock`: locks direction by angle.
    - `Angle`: angle value used by `AngleLock`.
    - `AngleRef`: `Absolute` uses CPlane X-axis; `Relative` uses previous segment direction.
    - `Mode` and `BothSides`: also available while placing the end point.

Hidden keywords while picking:

- `u` or `undo`: undo last in-session segment action.
- `r` or `redo`: redo last undone in-session segment action.

### vLineLength flow

1. Run `vLineLength`.
1. Click an open curve near the end you want to drive.
1. Use options while editing:

    - `Length`: target value used by current mode.
    - `ExtendMode`: extension style when target requires growth.
      - `Smooth`: smooth extension.
      - `Line`: straight-line extension.
    - `Mode`: how `Length` is interpreted.
      - `Total`: resulting full curve length.
      - `Add`: add value to current length.
      - `Subtract`: subtract value from current length.
    - `Undo`: revert the last in-session edit.

Hidden keywords while editing:

- `total`, `add`, `subtract`: set mode directly.
- `add/subtract`: toggle between add and subtract.

### vMiddleCurve flow

1. Run `vMiddleCurve`.
1. Select exactly 2 curves (preselect supported — press Enter to confirm).
1. The command aligns curve directions and seams automatically, then creates an interpolated curve equidistant between the two inputs.
1. Sample density is chosen adaptively and refined until the middle curve error is within tolerance.

### vOffset flow

1. Run `vOffset`.
1. The built-in `_Offset` command runs interactively.
1. After each offset, selection is cleared automatically.
1. Press Enter to repeat `vOffset` for another offset cycle.

### vOrient2pt flow

1. Select objects to orient.
1. Pick source first point.
1. Pick target first point.
1. Pick source second point.
1. Pick target second point.
1. Toggle `Copy` option as needed during point picking.

### vOrient3pt flow

1. Select objects to orient.
1. Pick source first point.
1. Pick target first point.
1. Pick source second point.
1. Pick target second point.
1. Pick source third point.
1. Pick target third point.
1. Toggle `Copy` option as needed during point picking.

### vPointNormalToSurface flow

1. Run `vPointNormalToSurface`.
1. Select a target surface or polysurface face.
1. Pick points in space.
1. A point is placed on the closest evaluated surface location (normal evaluation point), with live preview from picked point to on-surface point.
1. Press Enter to finish.

Hidden keywords while picking points:

- `u` or `undo`: remove last created point in the current command session.
- `r` or `redo`: recreate last undone point in the current command session.

### vRectangle flow

1. Run `vRectangle`.
1. If curves are preselected, their total length is used as the width automatically.
1. Otherwise, set width: select curves to use their total length, type a number, or press Enter to keep the current value.
1. Set height the same way.
1. Pick the bottom-left corner. Press Enter to reuse the previous bottom-right position.
1. Live preview shows the rectangle while moving the cursor.
1. Use `Width` and `Height` options while picking the corner to adjust values.

### vScallop flow

1. Run `vScallop`.
1. Select a line, or press Enter to pick two points.
1. Pick side point to define bulge direction.
1. Use options:

    - `Size`: scallop bulge distance.
    - `Free`: when `Yes`, bulge is measured from midpoint to picked side point; when `No`, uses fixed `Size`.
    - `DeleteOriginal`: when selecting an existing line input, remove that original line after creating the scallop arc.

### vSplitAtCorners flow

1. Run `vSplitAtCorners`.
1. Select curves to split.
1. Review highlighted corners and click to toggle behavior before applying.
1. Press Enter to run splitting.
1. Use options:

    - `Angle`: minimum detected corner angle in degrees.
    - `MinLength`: minimum resulting segment length to keep.
    - `ClearManual`: remove all manually forced split points.
    - `ClearSuppressed`: restore all suppressed auto-corners.
    - `ClearAll`: clear both manual and suppressed overrides.

Click behavior in preview phase:

- Clicking an auto-highlighted corner toggles suppression for that corner.
- Clicking a manual point toggles that manual split point on/off.

String shortcuts in preview phase:

- `manual`: prints guidance for adding manual split points.
- `suppress`: prints guidance for suppressing auto corners.
- `clear`/`clearall`: clears both manual and suppressed sets.

### vTextAligned flow

1. Run `vTextAligned`.
1. Click a curve to lock orientation base, or click existing text to edit/reposition it.
1. Pick placement point near locked curve.
1. Use options while placing:

    - `Text`: sets content for newly created text and active text edits.
    - `Height`: text height.
    - `Offset`: signed side offset from curve toward text bounds.
    - `Rotate`: rotates text orientation by 90 degrees each use.

Hidden keywords while placing:

- `u` or `undo`: undo last in-session add/move action.
- `r` or `redo`: redo last undone add/move action.

### vTextFlip flow

1. Run `vTextFlip`.
1. Select annotation text objects.
1. Use command options:

    - `Flip`: flips selected text orientation using object-local plane behavior.
    - `Rotate`: rotates selected text by 90 degrees.
    - `Clear`: clears current command selection list.

### vTogglePerpGumball flow

1. Run `vTogglePerpGumball` to toggle monitor state (`ON`/`OFF`).
1. While `ON`, select exactly one grip in a supported viewport.
1. The command auto-orients gumball so it stays perpendicular and view-stable without changing the viewport CPlane.
1. `Perspective` viewports keep default gumball, including when switched to `Parallel` projection.
1. When turning `OFF`, gumball orientation is reset to Rhino default.

### vTrim flow

1. Select cutting curves first, or press Enter to use `AutoClosest` mode.
1. Click target curves to trim against the selected cutters, or against auto-detected cutters when in `AutoClosest` mode.
1. Hold Shift before clicking to switch to extend mode for that click.
1. Adjust options while picking targets.

    - `Extend`: `Line` or `Smooth` extension style.
    - `Join`: `Yes` or `No` for joining kept trim pieces.

1. Preview highlights:

    - Trim removal preview: red.
    - Extend addition preview: green.

Hidden keywords while picking targets:

- `u` or `undo`: undo last vTrim action in the current command session.
- `r` or `redo`: redo last undone vTrim action in the current command session.

### vUzip flow

1. Select the center curve.
1. Adjust runtime options in the command prompt.

    - `Label`: text used when naming generated output.
    - `Tail`: tail distance used when building end curves.

1. Pick placement point for generated groups, or cancel placement to remove generated objects.

## Configuration

Commands read and write vTools.config.json next to the plug-in assembly. Sections are created per command as options are used.

Example:

```json
{
  "vUzip": {
    "label": "",
    "tail": 0.75,
    "layers": {
      "reference": { "name": "Reference", "color": "#FFFFFF" },
      "plot": { "name": "PLOT", "color": "#0F8A8A" },
      "cut": { "name": "CUT1", "color": "#CC3333" }
    },
    "parts": []
  }
}
```

## Logging

- Plug-in startup log: logs/vTools.log

The code resolves a project-local logs folder first and falls back to an assembly-local logs folder.

## Versioning

This project uses CalVer-style metadata (YY.MM.DD.HHMMSS) in project and assembly informational versions.

## License

This project is released under the MIT License. See LICENSE.
