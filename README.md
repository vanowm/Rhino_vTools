# vTools  ·  v26.5.14

vTools is a Rhino 8 plug-in project (C# / .NET 7) that provides native RhinoCommon commands for zipper, orient, trim/extend, gumball, curve, line, text, and tangent/perpendicular alignment workflows.

## What this project includes

- Rhino plug-in entry point: vToolsPlugIn
- Native commands:
  - [vChamfer](#vchamfer-flow) *(26.05.07.0723)* — cuts a corner formed by two curves with a straight line perpendicular to the angle bisector at a specified cut length
  - [vCurveToSpline](#vcurvetospline-flow) *(26.04.24.0934)* — converts selected curves to interpolated splines with join modes
  - [vDiamonds](#vdiamonds-flow) *(26.5.14.1038)* — draws an argyle diamond pattern with optional bounding rectangle and size/count labels; supports BySize centering mode
  - [vFitBox](#vfitbox-flow) *(26.04.24.0934)* — finds the minimum bounding box for selected objects by optimizing rotation angle
  - [vLine](#vline-flow) *(26.04.27.2125)* — draws lines with chain modes, angle lock, length constraint, and perp/tangent endpoint solving
  - [vLineLength](#vlinelength-flow) *(26.04.27.2125)* — resizes an open curve to a target total, additive, or subtractive length
  - [vMiddleCurve](#vmiddlecurve-flow) *(26.04.27.2125)* — creates an interpolated curve equidistant between two selected curves
  - [vOffset](#voffset-flow) *(26.04.27.2125)* — runs built-in Offset in a continuous loop, clearing selection after each run
  - [vOrient2pt](#vorient2pt-flow) *(26.04.24.0934)* — orients objects from a source two-point frame to a target two-point frame
  - [vOrient3pt](#vorient3pt-flow) *(26.04.24.0934)* — orients objects from a source three-point frame to a target three-point frame
  - [vPerpendicularTo](#vperpendicularto-flow) *(26.05.05.0757)* — rotates curve A about its nearest endpoint so it is perpendicular to curve B in the active CPlane
  - [vPointNormalToSurface](#vpointnormaltosurface-flow) *(26.04.27.2109)* — places points projected onto the closest surface normal evaluation point
  - [vRectangle](#vrectangle-flow) *(26.04.27.2125)* — creates an axis-aligned rectangle polyline from width/height inputs driven by numeric value or selected curve lengths
  - [vScallop](#vscallop-flow) *(26.04.27.2125)* — creates an arc scallop between two points or along a selected line
  - [vSplitAtCorners](#vsplitatcorners-flow) *(26.04.27.2125)* — splits curves at detected corners with interactive per-corner toggle preview
  - [vTangent](#vtangent-flow) *(26.05.05.0757)* — moves a curve rigidly so one or both endpoints align tangentially to selected driver curves
  - [vTextAligned](#vtextaligned-flow) *(26.04.27.2125)* — places or repositions annotation text aligned and offset along a selected curve
  - [vTextFlip](#vtextflip-flow) *(26.04.27.2125)* — flips or rotates annotation text around its object plane
  - [vTogglePerpGumball](#vtoggleperpgumball-flow) *(26.04.24.1712)* — toggles a monitor that auto-orients the gumball perpendicular to selected control point grips
  - [vTrim](#vtrim-flow) *(26.04.24.1633)* — trims and extends curves with auto-cutter detection and join
  - [vUzipParts](#vuzipparts-flow) *(26.04.24.0934)* — creates U-zip parts from a center curve into labeled reference, plot, and cut output groups
  - [vUzipCenter](#vuzipcenter-flow) *(26.05.01.2200)* — offsets a U-shape's three curves inward, fillets the inside corners, and produces a single joined open curve
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

Native commands: [vChamfer](#vchamfer-flow), [vCurveToSpline](#vcurvetospline-flow), [vDiamonds](#vdiamonds-flow), [vFitBox](#vfitbox-flow), [vLine](#vline-flow), [vLineLength](#vlinelength-flow), [vMiddleCurve](#vmiddlecurve-flow), [vOffset](#voffset-flow), [vOrient2pt](#vorient2pt-flow), [vOrient3pt](#vorient3pt-flow), [vPerpendicularTo](#vperpendicularto-flow), [vPointNormalToSurface](#vpointnormaltosurface-flow), [vRectangle](#vrectangle-flow), [vScallop](#vscallop-flow), [vSplitAtCorners](#vsplitatcorners-flow), [vTangent](#vtangent-flow), [vTextAligned](#vtextaligned-flow), [vTextFlip](#vtextflip-flow), [vTogglePerpGumball](#vtoggleperpgumball-flow), [vTrim](#vtrim-flow), [vUzipParts](#vuzipparts-flow), [vUzipCenter](#vuzipcenter-flow).

1. Load the plug-in assembly in Rhino.
1. Run one of the native commands.

### vDiamonds flow

1. Run `vDiamonds`.
1. Adjust options while previewing the pattern:

    - `Width` / `Height`: single-diamond cell width and height. Accept decimal, fraction (`3+1/8`), or type `heightxwidth` directly at the placement prompt (e.g. `2x3`).
    - `CountWidth` / `CountHeight`: number of diamond cells across/tall; decimals allowed (`3.5` = 3 full + half cell).
    - `BySize`: enter a target bounding box size as `widthxheight`. Diamonds are counted by `floor(box/cell)` and centered with equal margins. Does not change `CountWidth`/`CountHeight`. Enter `0` to revert to count mode.
    - `Boundary=Yes/No`: show/hide the CUT1 bounding rectangle.
    - `Size=Yes/No`: show/hide the size label (e.g. `2 x 2`), fitted to bbox width.
    - `Count=Yes/No`: show/hide the count label (e.g. `(3 x 3)`), fitted to bbox width independently.

1. Current bounding box dimensions print to command history on every preview update.
1. Pick the placement point to commit. All objects are grouped. Output layers: `PLOT` (diamond lines), `CUT1` (boundary rect), `Reference` (labels).

### vChamfer flow

1. Pick **curve 1** — near the corner to cut.
1. Pick **curve 2** — near the same corner.
1. Adjust options while previewing the cyan cut line:

    - `Length`: the cut line length. The cut is always perpendicular to the angle bisector of the two curves at the corner.

1. Press Enter to apply: both curves are trimmed to the cut endpoints and a new line is added.

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
1. Auto-detected corners appear as orange dots; click any to toggle it off (gray = excluded).
1. Click anywhere along a selected curve to add a manual split point (cyan X); click an existing cyan X to remove it.
1. Press Enter to apply splitting.
1. Options:

    - `Angle`: minimum detected corner angle in degrees.
    - `MinLength`: minimum resulting segment length to keep.
    - `ClearManual`: remove all manually added split points.
    - `ClearAll`: restore all removed auto-corners and remove all manual points.

### vTextAligned flow

1. Run `vTextAligned`.
1. Click a curve to lock orientation base, or click existing text to edit/reposition it.
1. Pick placement point near locked curve.
1. Use options while placing:

    - `Text`: sets content for newly created text and active text edits.
    - `Height`: text height.
    - `Offset`: signed side offset from curve toward text bounds.
    - `Rotate`: rotates text orientation by 90 degrees each use.

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

### vUzipParts flow

1. Select the center curve.
1. Adjust runtime options in the command prompt.

    - `Label`: text used when naming generated output.
    - `Tail`: tail distance used when building end curves.

1. Pick placement point for generated groups, or cancel placement to remove generated objects.
1. While picking the placement point, the `Label` and `Tail` options remain available. Changing either triggers a full rebuild of all parts before placement continues.

### vUzipCenter flow

1. Select three curves that form a U shape (left arm, right arm, bottom). You may preselect up to three curves before running the command; a fourth preselected curve is used as the initial boundary.
1. While selecting curves, adjust offset and fillet options:

    - `Left`: offset distance for the left arm.
    - `Right`: offset distance for the right arm.
    - `Bottom`: offset distance for the bottom curve.
    - `Radius`: fillet radius at the two inside corners.

   Distances accept fractional inch input (`2 3/8`, `2-3/8`, `3/8`, plain decimal) and the shorthand `z`/`zipper` (returns the left-arm default).

1. A cyan preview curve is displayed showing the computed result.
1. While previewing, adjust the same options to recompute live.
1. Click any existing curve to set or replace the boundary: the result is trimmed or extended to meet it.
1. Press Enter to accept and add the result curve to the document.
1. Options are saved to `vTools.config.json` under the `vUzipCenter` section.

### vTangent flow

1. Pick **S1** — click near the end of the subject curve you want aligned to D1.
1. Pick **D1** — the required driver curve; the tangent at the click point is used.
1. Pick **S2** — click the other end of the same subject curve (for a second alignment).
1. Pick **D2** — an optional second driver curve; press Enter to skip.

Behavior:

- With D1 only: the subject curve is translated and rotated rigidly so the S1-end tangent matches the D1 tangent at the pick point.
- With D1 and D2: an additional twist about the D1 tangent axis is applied to minimize the angular error between the S2-end tangent and D2 (or its reverse, whichever needs less rotation).
- The subject curve shape is not changed — only its position and orientation.

### vPerpendicularTo flow

1. Pick **curve A** — the curve to rotate.
1. Pick **curve B** — the reference curve (not moved).

Behavior:

- The nearest endpoint pair between A and B is found automatically.
- Curve A is rotated about its near endpoint in the active CPlane by the angle needed to make it perpendicular to B's tangent at B's near endpoint.
- Of the two possible perpendicular directions, the one requiring the smaller rotation is chosen.

## Configuration

Commands read and write vTools.config.json next to the plug-in assembly. Sections are created per command as options are used.

Example:

```json
{
  "vUzipParts": {
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
