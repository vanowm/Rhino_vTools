# vTools  ·  v26.7.23.1736

vTools is a dual-target Rhino 8 and Rhino 9 plug-in project (C# / .NET 7 and .NET 10) that provides native RhinoCommon commands for notches, orient, trim/extend, gumball, curve, line, text, tangent/perpendicular alignment workflows and more.

## What this project includes

- Rhino plug-in entry point: vToolsPlugIn
- Native commands (42):
  - [vBiminiParts](#vbiminiparts-flow) *(26.5.21.1827)* — builds bimini cover pocket parts (facings, main pocket, secondary pockets, center reference line) from a selected boundary curve; pipe size configures pocket depths
  - [vChamfer](#vchamfer-flow) *(26.5.7.723)* — adds a chamfer line perpendicular to the middle curve at an equidistant gap; places the chamfer where the gap between two diverging curves equals the specified length
  - [vCommandFailSound](#vcommandfailsound-flow) *(26.7.23.045)* — configures and toggles an audible notification when a Rhino command ends with any result other than Success or Cancel
  - [vCurveToSpline](#vcurvetospline-flow) *(26.4.24.934)* — converts selected curves to interpolated splines with join modes
  - [vDiamonds](#vdiamonds-flow) *(26.5.14.928)* — draws an argyle diamond pattern with optional bounding rectangle and size/count labels; supports BySize centering mode
  - [vFacing](#vfacing-flow) *(26.5.29.1333)* — builds a four-piece closed facing boundary from a base curve and two side curves by offsetting the base inward by a specified size; collects inside objects and places the result with a DynamicDraw preview
  - [vFitBox](#vfitbox-flow) *(26.4.24.934)* — finds the minimum bounding box for selected objects by optimizing rotation angle
  - [vGroup](#vgroup-flow) *(26.5.27.1300)* — groups selected objects by closed-curve boundaries; each boundary is grouped with the objects inside it
  - [vIsolate](#visolate-flow) *(26.7.22.1515)* — keeps selected objects visible while hiding every other visible normal object, optionally in a named Rhino hide set
  - [vLine](#vline-flow) *(26.4.27.2125)* — draws lines with chain modes, angle lock, length constraint, and perp/tangent endpoint solving
  - [vLineLength](#vlinelength-flow) *(26.4.27.2125)* — resizes an open curve to a target total, additive, or subtractive length
  - [vMatch](#vmatch-flow) *(26.7.1.1535)* — click near an edge-mate dot produced by vUnrollSrf to align the neighbouring flat part; Auto mode assembles a whole BFS selection with optional randomisation
  - [vMiddleCurve](#vmiddlecurve-flow) *(26.4.27.2243)* — creates an interpolated curve equidistant between two selected curves
  - [vNotches](#vnotches-flow) *(26.6.1.1529)* — places perpendicular notch marks along one or two selected curves at clicked positions; a floating panel controls notch type, dimensions, optional label, and per-curve side/reverse settings
  - [vOffset](#voffset-flow) *(26.4.27.2243)* — runs built-in Offset in a continuous loop, clearing selection after each run
  - [vOrient2pt](#vorient2pt-flow) *(26.4.24.934)* — orients objects from a source two-point frame to a target two-point frame
  - [vOrient3pt](#vorient3pt-flow) *(26.4.24.934)* — orients objects from a source three-point frame to a target three-point frame; intermediate points are optional (Enter at src2 = 1-point translate, Enter at src3 = 2-point orient)
  - [vOverlaps](#voverlaps-flow) *(26.7.3.2104)* — finds all visible curves that follow the same path or are fully covered by another and selects them (duplicates/subsets); one original per duplicate group is kept unselected
  - [vPart](#vpart-flow) *(26.5.18.1742)* — captures a closed perimeter from selected curves (gaps are bridged automatically), collects all visible objects inside the perimeter (curves trimmed at the boundary; other types included whole), and lets the user place the resulting Part with a full preview
  - [vPerpendicularTo](#vperpendicularto-flow) *(26.5.5.757)* — rotates curve A about its nearest endpoint so it is perpendicular to curve B in the active CPlane
  - [vPointNormalToSurface](#vpointnormaltosurface-flow) *(26.4.27.2109)* — places points projected onto the closest surface normal evaluation point
  - [vProjectToSurface](#vprojecttosurface-flow) *(26.7.16.1432)* — projects selected curves and points onto one or more target surfaces or polysurfaces with live preview; overhanging curve portions are clipped away
  - [vPointTrace](#vpointtrace-flow) *(26.4.30.1044)* — maps arc-length positions from a source curve onto a destination curve: pick points along the source and a corresponding point is placed on the destination at the same proportional arc-length position
  - [vRectangle](#vrectangle-flow) *(26.4.27.2259)* — creates an axis-aligned rectangle polyline from width/height inputs driven by numeric value or selected curve lengths
  - [vScallop](#vscallop-flow) *(26.4.27.2125)* — creates an arc scallop between two points or along a selected line
  - [vSetPt](#vsetpt-flow) *(26.5.28.1145)* — previews and aligns preselected edit-point or control-point grips, or cursor-nearest endpoints, using the built-in SetPt
  - [vShow](#vshow-flow) *(26.7.22.1818)* — shows one named hidden-object set, or all named sets, without cancelling the command already in progress
  - [vSplit](#vsplit-flow) *(26.7.9.1647)* — interactively splits selected curves at picked real point markers with cyan remove preview and point snapping
  - [vSplitAtCorners](#vsplitatcorners-flow) *(26.4.27.2125)* — splits curves at detected corners with interactive per-corner toggle preview
  - [vTangent](#vtangent-flow) *(26.5.5.757)* — moves a curve rigidly so one or both endpoints align tangentially to selected driver curves
  - [vTextAligned](#vtextaligned-flow) *(26.4.27.2125)* — places or repositions annotation text aligned and offset along a selected curve
  - [vTextFlip](#vtextflip-flow) *(26.4.27.2125)* — flips or rotates annotation text around its object plane
  - [vTitle](#vtitle-flow) *(26.7.1.1755)* — places or edits a titled annotation text box with optional bounding rectangle; hover to highlight, click existing to edit
  - [vToggleAxes](#vtoggleaxes-flow) *(26.6.22.1811)* — toggles visible viewport axes (grid/construction axes plus display-mode Z axis)
  - [vToggleControlPoints](#vtogglecontrolpoints-flow) *(26.7.13.1046)* — toggles selected objects between edit points on the curve and off-curve control points
  - [vTogglePerpGumball](#vtoggleperpgumball-flow) *(26.4.24.1712)* — toggles a monitor that auto-orients the gumball perpendicular to selected control point grips
  - [vTrim](#vtrim-flow) *(26.4.24.1633)* — trims and extends curves with auto-cutter detection and join
  - [vTrimOff](#vtrimoff-flow) *(26.5.18.849)* — trims selected curves to the outer boundary of the enclosed region they collectively form; protruding ends are removed automatically
  - [vUnrollSrf](#vunrollsrf-flow) *(26.5.19.1918)* — runs the built-in UnrollSrf command and automatically selects all newly created flat objects on completion; TextDot labels are included only when their position touches a newly created flat object (3D-surface correspondence labels are excluded)
  - [vUzip](#vuzip-flow) *(26.4.24.934)* — full U-zip workflow in one command: selects three U-shape arm curves, computes the inward-offset center curve with fillet, and optionally produces glass, vis, and parts output with label and tail settings
  - [vUzipCenter](#vuzipcenter-flow) *(26.5.1.1903)* — offsets a U-shape's three curves inward, fillets the inside corners, and produces a single joined open curve
  - [vUzipParts](#vuzipparts-flow) *(26.5.8.1249)* — creates U-zip parts from a center curve into labeled reference, plot, and cut output groups
- Shared command configuration file: vTools.config.json
- Runtime command diagnostics in a local logs folder

## Requirements

- Rhino 8 and Rhino 9 (or Rhino 9 WIP) for their matching RhinoCommon assemblies
- .NET SDK 10.0+ with the .NET 7 targeting pack
- Windows

## Build

From this folder:

```powershell
.\build.ps1
```

This standalone Release build does not require Git and never commits or pushes.
Repository maintainers can use `.\build.ps1 -Publish` for the semantic-message,
signed-commit, push, and GitHub release workflow used by the normal VS Code build task.

Build behavior:

1. Release builds fail fast if output files are locked (for example, if Rhino holds `vTools.dll`).
2. After every successful Release build, a timestamped backup snapshot is created automatically.
3. When `-Publish` produces either changed DLL, the build commits both runtime outputs once and pushes a full-version tag. GitHub then creates a release containing distinctly named .NET 7 and .NET 10 DLLs plus the toolbar `.rui` file.

## Output

Release output is written to:

- bin/Release/net7.0-windows/vTools.dll
- bin/Release/net7.0-windows/vTools.config.json
- bin/Release/net7.0-windows/vTools.rui
- bin/Release/net10.0-windows/vTools.dll
- bin/Release/net10.0-windows/vTools.config.json
- bin/Release/net10.0-windows/vTools.rui
- Automatic backups: bin/Release/backups/YY.MM.DD.HHMMSS/&lt;target-framework&gt;/

## Rhino usage

All command options persist by default unless stated otherwise.

Native commands (42): [vBiminiParts](#vbiminiparts-flow), [vChamfer](#vchamfer-flow), [vCommandFailSound](#vcommandfailsound-flow), [vCurveToSpline](#vcurvetospline-flow), [vDiamonds](#vdiamonds-flow), [vFacing](#vfacing-flow), [vFitBox](#vfitbox-flow), [vGroup](#vgroup-flow), [vIsolate](#visolate-flow), [vLine](#vline-flow), [vLineLength](#vlinelength-flow), [vMatch](#vmatch-flow), [vMiddleCurve](#vmiddlecurve-flow), [vNotches](#vnotches-flow), [vOffset](#voffset-flow), [vOrient2pt](#vorient2pt-flow), [vOrient3pt](#vorient3pt-flow), [vOverlaps](#voverlaps-flow), [vPart](#vpart-flow), [vPerpendicularTo](#vperpendicularto-flow), [vPointNormalToSurface](#vpointnormaltosurface-flow), [vProjectToSurface](#vprojecttosurface-flow), [vPointTrace](#vpointtrace-flow), [vRectangle](#vrectangle-flow), [vScallop](#vscallop-flow), [vSetPt](#vsetpt-flow), [vShow](#vshow-flow), [vSplit](#vsplit-flow), [vSplitAtCorners](#vsplitatcorners-flow), [vTangent](#vtangent-flow), [vTextAligned](#vtextaligned-flow), [vTextFlip](#vtextflip-flow), [vTitle](#vtitle-flow), [vToggleAxes](#vtoggleaxes-flow), [vToggleControlPoints](#vtogglecontrolpoints-flow), [vTogglePerpGumball](#vtoggleperpgumball-flow), [vTrim](#vtrim-flow), [vTrimOff](#vtrimoff-flow), [vUnrollSrf](#vunrollsrf-flow), [vUzip](#vuzip-flow), [vUzipCenter](#vuzipcenter-flow), [vUzipParts](#vuzipparts-flow).

1. Load the plug-in assembly in Rhino.
1. Run one of the native commands.

### vBiminiParts flow

1. Select bimini boundary curves (preselect supported). Use the `PipeSize` option to select the pipe size group (`7/8`, `1`, `1-1/4`, `1-1/2`).
1. The command joins the selected curves into a single closed boundary, then determines the seam curve (outward offset) and finished curve (either detected from an existing curve on the PLOT layer or computed as an inward offset), and breaks both at corners into top/bottom/left/right segments.
1. **Stage 2 — Main pocket**: click the center of up to 2 seam or finished top/bottom segments to identify the main pocket side(s). Press Enter to skip.
1. **Stage 3 — Secondary pocket**: click the center of remaining top/bottom segments for secondary pockets if fewer than 2 main pockets were picked. Press Enter to skip.
1. Before generating a secondary pocket, its length calculated from the main curve is compared with the previous secondary-pocket length stored in the Rhino document. When they differ, the command shows both lengths and asks whether to reuse the previous length. A successfully created secondary pocket updates the stored document value.
1. Output is built automatically:

    - **Facing parts** (port and starboard sides) with interior objects collected.
    - **Main pocket outline** — closed filleted rectangle trimmed to the boundary.
    - **Secondary pocket outline(s)** — closed filleted shapes with seam clearance.
    - **Center reference line** through all pocket center points.

1. Output layers: `PLOT` (finished curve segments), `CUT1` (seam and pocket outlines), `Reference` (center points and center line).

Options:

- `PipeSize`: selects a pipe size group which sets `MainPktDepth`, `SecPktDepth`, and optional `ExtraRect` dimensions. All groups are configurable in `vTools.config.json` under `vBiminiParts`.

### vChamfer flow

1. Pick **curve 1** — near the corner of the two diverging curves.
1. Pick **curve 2** — near the same corner.
1. Adjust options while previewing the cyan chamfer line:

    - `Length`: the desired chamfer line length — the perpendicular (equidistant) gap between both curves at the chamfer point. The chamfer is placed where the equidistant gap equals this value, with the line perpendicular to the middle curve.
    - `Trim`: `Yes` trims both curves to the chamfer endpoints; `No` only adds the line.
    - `Join` *(Trim=Yes only)*: `Yes` joins trimmed curves and the chamfer line into a single polycurve.

1. Optional — pick a **reference point** to reposition the chamfer: click anywhere near the curves. The chamfer moves to the arc position where the chamfer line's midpoint is exactly `Length` units away from the click (toward the corner). Press `ClearPoint` to revert to the gap-based placement.
1. Press Enter to apply.

Notes:
- Corner detection uses the closest endpoint pair. Extension stubs (toward a virtual corner) are shown in the preview when `Trim=Yes` and the cut falls inside the extension zone.
- All options persist to `vTools.config.json` under `vChamfer`.

### vCommandFailSound flow

1. Failure monitoring is enabled by default when the plug-in loads. Run `vCommandFailSound` and use `Enabled=Yes/No` to change it; the setting persists immediately.
1. `Sound` selects the system default, Asterisk, Exclamation, Hand, Question, or a custom audio file; `AudioFile` opens a file picker and selects `Custom` automatically.
1. `Preview` plays the current choice without changing the watcher state. Sound choices persist immediately in the shared `vTools.config.json`.
1. While enabled, the selected sound plays whenever a Rhino command ends with any result other than `Success` or `Cancel`.
1. Press Enter to finish configuring the command. The watcher detaches automatically when the plug-in shuts down.

### vCurveToSpline flow

1. Select source curves (preselect or postselect is supported).
1. Set `Join` option.

    - `None`: one spline per selected curve.
    - `Connected`: one spline per connected curve island.
    - `All`: one spline through all selected curves.

1. Set `SmoothClose`: `Yes` makes closed outputs smooth; `No` closes with an explicit seam point, leaving a kink.
1. Confirm to create interpolated curve output and select results.

Closed source curves and closed joined chains are created as closed splines.

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

### vFacing flow

1. Select the facing curves (base + two sides). Multiple curves per role are supported. An optional chamfer piece may also be included.

   Role assignment rules:
   - Curves are grouped by layer; each layer group is treated as one role (base, side 1, side 2).
   - If all curves share the same layer: exactly 3 curves — roles are auto-detected by topology; more than 3 — the command prompts you to click the two side curves.
   - A single closed curve — the command splits it at corners (≥30°), highlights the pieces, and prompts you to click the base edge.
   - Four layer groups — the group that shares an endpoint with only one other group is identified as the chamfer piece and merged into the adjacent side.

1. Adjust the `Size` option (inward offset distance for the facing front) while selecting curves.
1. The command builds the four-piece boundary: base, offset front (at `Size` distance inward), and two trimmed side segments. All visible objects inside the closed boundary are collected automatically.
1. A DynamicDraw preview follows the cursor. Pick the placement point to commit.
1. All output objects are placed as new geometry at the picked location and added to a single Rhino group. Originals are not deleted.

Options:

- `Size`: offset distance from the base curve to the inner facing edge. Persists to `vTools.config.json` under `vFacing`.

### vFitBox flow

1. Select objects to fit.
1. Adjust options in command line.

    - `AngleStep`: sampling step in degrees and accepts direct numeric input during object picking.
    - `Rotate`: applies final rotation to both fit box and selected objects, keeps the longest in-plane side horizontal, and prefers the equivalent orientation that avoids unnecessary 180-degree flips.
    - `Fit`: optimize by `Height` or `Area`.

1. Confirm selection to generate the fit result.

### vGroup flow

1. Select objects to group — include both the boundary curves and any objects to be placed inside groups.
1. The command finds all closed polygon boundaries formed by the selected curves:

    - All curves are split at their mutual intersection points.
    - Segments with dead-end endpoints (degree-1 nodes) are iteratively removed until only closed-cycle core segments remain.
    - Surviving segments are joined into closed planar boundaries.

1. For each closed boundary, all selected objects whose representative point falls inside it are collected. Original curves that defined the boundary (e.g. crossing lines whose midpoint lies outside the inner polygon) are included via source-curve tracking.
1. Each boundary and its interior objects are added to a Rhino group (minimum 2 members required).

### vIsolate flow

1. Preselect objects to keep visible, or run `vIsolate` and select them. Prompted selection includes whole groups and excludes subobjects; a direct set name such as `vIsolate "A"` can be supplied before selecting objects.
1. Enter an optional Rhino hide-set name, or press Enter for no named set.
1. Every other visible, unlocked normal object is hidden natively and the isolated objects remain selected. Named isolation records active set membership; blank input clears any prior membership and uses Rhino's ordinary unnamed hide behavior, matching `_Isolate`.
1. `vIsolate` is transparent and can run without cancelling the command already in progress. When nested inside another command, it restores the selection that existed before isolation.
1. The packaged `vTools` toolbar and tab contain a blue/grey `vIsolate / vShow` flyout linked to the `vIsolate` toolbar. That toolbar contains the general `vIsolate / vShow` button, named `vIsolate A` through `vIsolate E` buttons, and the combined built-in `Isolate / Show` button. Named buttons isolate into the matching set on left-click and use transparent `vShow` to restore that set on right-click; the built-in button uses `!_Isolate` and `!_Show`.

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
    - `Tangent`: solve against the hovered curve and use the valid tangent point nearest the cursor.
    - `PerpNear`: solve perpendicular against nearest curve.
    - `TanNear`: solve against the nearest curve and use the valid tangent point nearest the cursor.
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

### vMatch flow

1. Run `vMatch`.
1. Click near an **edge-mate dot** (placed by `vUnrollSrf`) on a flat unrolled part. The neighbouring part snaps so its mating edge aligns with the selected edge at the configured gap distance.
1. Options:

    - `Distance`: gap between matched edges.
    - `Auto`: `Yes` assembles all selected parts via BFS (Breadth-First Search) starting from a single clicked dot; `No` does one match per click.
    - `RandStart` *(Auto only)*: randomise the BFS start part.
    - `RandNext` *(Auto only)*: randomise the order of BFS neighbours.

Options persist to `vTools.config.json` under `vMatch`.

### vMiddleCurve flow

1. Run `vMiddleCurve`.
1. Select exactly 2 curves (preselect supported — press Enter to confirm).
1. The command aligns curve directions and seams automatically, then creates an interpolated curve equidistant between the two inputs.
1. Sample density is chosen adaptively and refined until the middle curve error is within tolerance.

### vNotches flow

1. Select one or two open or closed curves (preselect supported; press Enter to confirm).
1. A floating **Notches** panel opens. Click positions along the curve(s) to place notches.
1. Use the disclosure chevron in each group header to collapse or restore the Notch, Multiple, and Label settings.
1. Numeric controls and readouts display at most three decimal places without unnecessary trailing zeroes.
1. **Notch** group options (panel and command line):

    - The `Notch` header checkbox controls notch geometry output. Notch and Label can both be enabled, but the command keeps at least one enabled.
    - `Type`: five checkbox-sized vector buttons select `I`, `V`, open `\/`, flat-capped `U`, or upside-down `T`, in that order. The icons and active highlight update from the current Width and Length values.
    - `Layer`: target layer for notch geometry, using the same packed-ARGB swatches as vObjectPropertiesPlus.
    - `Length`, `Width`, and `Offset`: compact numeric steppers; width controls `V`, `\/`, and `U` arm separation and the `T` crossbar.
    - Created notch curves are named `NOTCH` and carry `notches.db.*` user-string attributes describing their source curve, placement, dimensions, side, label settings, and layers. The disconnected `\/` legs and branched `T` stem/crossbar are grouped component curves tracked as one notch.

1. **Label** group options:

    - The `Label` header checkbox controls label output. The remaining label settings stay editable whether output is enabled or not.
    - Value text box: the label string placed at the notch.
    - `AutoAdv`: when enabled, increments a trailing numeric suffix after each placement.
    - `FlipSide`: mirrors the label to the opposite side of the curve.
    - `Layer`: target layer for label text, using the same packed-ARGB swatches as vObjectPropertiesPlus.
    - `Size`: manual label text height. `Auto` computes height proportionally from notch geometry; the adjacent percentage stepper scales the auto-computed height.
    - `Offset X` / `Offset Y`: numeric steppers for label position relative to the notch point (along-curve and across-curve).

1. **Multiple** group options:

    - `Start offset` / `End offset`: numeric steppers for the distances from each curve's respective ends to the first and last notch.
    - `Number`: numeric stepper for the total number of notches, including the first and last positions.
    - `Distance`: editable numeric stepper with a `1.0` button increment. Changing `Number` evenly distributes the fixed start/end span. Changing `Distance` repeats that spacing and uses a shorter final gap when needed to preserve the fixed end offset. The shortest enabled curve is the spacing reference.
    - `Add`: creates an evenly spaced notch batch. When labels are enabled, only the first notch position receives the label and auto-advance runs once.

1. Other panel controls:

    - `Percent`: display the click position as a percentage of total curve length in the distance readout.
    - `Group`: group each enabled notch and label output.
    - `Select`: return to individual-curve selection without selecting groups or ending the command. Its inset checkbox defaults unchecked: unchecked selection replaces the current curve set; checked selection keeps the current curves so others can be added or removed. The setting is saved immediately. Existing placed notches remain in the document, and a curve restores its remembered side when re-added.
    - Per-curve row — `Side N` checkbox: which side of the curve the notch and label are drawn on; `Reverse N` button: flip the curve's travel direction; the last column shows curve length rounded to three decimal places.
    - Distance info: **From start**, **From end**, **From previous** show arc-length values rounded to three decimal places.
    - **Undo** / **Redo** buttons: step backward or forward through placements.

1. Press Enter to finish and keep all placed notches. Press Esc to cancel and remove them.

The floating panel adds scrollbars when resized below its content size.

Options persist to `vTools.config.json` under the `vNotches` section.

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
1. Pick source second point, or press Enter for 1-point orient (translation only).
1. Pick target second point, or press Enter to use source second point.
1. Pick source third point, or press Enter for 2-point orient.
1. Pick target third point, or press Enter to use source third point.
1. Toggle `Copy` option as needed during point picking.

### vOverlaps flow

1. Run `vOverlaps`.
1. Optionally preselect curves; if none, press Enter at the prompt to scan all visible curves in the document.
1. Use the `Tolerance` option to adjust the proximity threshold (default 0.001).
1. Press Enter to run: covered and duplicate curves are unselected first, then the overlapping ones are selected.

Behavior:
- **Same-path duplicates**: both curves follow the same path and have the same length. The oldest (lowest runtime serial number) is kept unselected as the original; all others are selected.
- **Covered curves**: a shorter curve lies entirely on top of a longer one. The shorter (covered) curve is selected.

Option persists to `vTools.config.json` under `vOverlaps`.

### vPart flow

1. Select the outer perimeter curves (preselect or postselect; a single closed curve is also valid).
1. The command joins the selected curves into a closed loop.  If endpoints do not quite meet (gaps ≤ 200× model tolerance), straight-line bridge segments are inserted automatically.
1. All visible objects inside the closed perimeter are collected automatically (excluding the selected perimeter curves).  Curves that cross the perimeter are split; only the inside segments are kept.  Non-curve objects (text, dots, points, etc.) are included whole when their representative point falls inside.
1. A full DynamicDraw preview of the Part (perimeter + inside objects) follows the cursor, each object drawn in its original layer color.
1. Pick the placement point to commit.  The Part is added as new objects at that location; originals are not deleted.
1. Press Esc to cancel without adding anything.

Options (available during both curve selection and placement):

- `Group`: when `Yes`, all output objects are placed into a single Rhino group.
- `JoinPerimeter`: when `Yes`, perimeter segments are joined into a single curve instead of being kept as individual segments.

### vPerpendicularTo flow

1. Pick **curve A** — the curve to rotate.
1. Pick **curve B** — the reference curve (not moved).

Behavior:

- The nearest endpoint pair between A and B is found automatically.
- Curve A is rotated about its near endpoint in the active CPlane by the angle needed to make it perpendicular to B's tangent at B's near endpoint.
- Of the two possible perpendicular directions, the one requiring the smaller rotation is chosen.

### vPointNormalToSurface flow

1. Run `vPointNormalToSurface`.
1. Select a target surface or polysurface face.
1. Pick points in space.
1. A point is placed on the closest evaluated surface location (normal evaluation point), with live preview from picked point to on-surface point.
1. Press Enter to finish.

### vProjectToSurface flow

1. Select one or more target surfaces or polysurfaces.
1. Select curves and point objects to project; the projected result previews while the selection changes.
1. Projected point objects and curve pieces are created on the target and selected; curve pieces use the current layer and source objects are left unchanged.

Behavior:
- Curves are pulled to every selected brep face by closest-point projection.
- Portions of a curve that do not touch the trimmed target face are skipped, so curves longer than the surface produce only the projected touching spans.

### vPointTrace flow

1. Run `vPointTrace`.
1. Click the source curve near the end you want to treat as the start.
1. Click the destination curve near the end you want to treat as the start.
1. Source and destination curves are highlighted but left unselected.
1. Pick points constrained to the source curve; a corresponding point is added on the destination at the same arc-length fraction.
1. A green dot previews the destination point while moving along the source.
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

### vSetPt flow

1. Select open curves to align. Preselected curves seed the editable selection; add or remove curves before pressing Enter. Any preselected edit-point or control-point grip also seeds its owning curve and overrides endpoint detection for that curve. Closed curves are ignored.
1. Cyan temporary curves preview the SetPt result: each preselected grip, or otherwise the endpoint nearest to the viewport cursor, moves to a common target that follows the cursor at the selected points' view depth. Edit-point previews rebuild the curve through the target; control-point previews move the selected CV directly.
1. Grips are enabled temporarily and the identified grips are selected automatically. Each curve's original grip visibility is restored when SetPt finishes or is cancelled.
1. The built-in `-SetPt` command launches with `XSet=Yes YSet=Yes ZSet=Yes Alignment=World Copy=No`; click the target location to commit.
1. Press Enter to repeat `vSetPt`.

### vShow flow

1. Run `vShow` while another command is active or from the normal command prompt.
1. Choose one of up to twenty most-recent active set options, enter any set name directly, or press Enter to show every named hidden set while leaving ordinary unnamed hidden objects untouched. Named sets created by Rhino's default `-Hide` command are polled into the same persistent list.
1. Matching active set members are shown directly, their completed set membership is cleared, and the interrupted command remains active with its prior selection restored.
1. The `vIsolate` toolbar passes A-E directly as set-name input when a named isolate button is right-clicked.

### vSplit flow

1. Run `vSplit`.
1. Select curves to split.
1. Click near selected curves to add real red point-object split markers; point picking is constrained to the chosen curves.
1. Existing split markers are snap points; hover one to preview it in cyan, then click to remove it.
1. Press Enter to apply splitting and replace the original curves with split pieces.
1. Options:

    - `Points`: choose `Default`, `CP`, `EditPoints`, or `Hidden` while choosing split points. `Default` leaves the original point visibility untouched on start and restores each selected curve's original hidden/CP/edit-point state when switched back.

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

### vTangent flow

1. Pick **S1** — click near the end of the subject curve you want aligned to D1.
1. Pick **D1** — the required driver curve; the tangent at the click point is used.
1. Pick **S2** — click the other end of the same subject curve (for a second alignment).
1. Pick **D2** — an optional second driver curve; press Enter to skip.

Behavior:

- With D1 only: the subject curve is translated and rotated rigidly so the S1-end tangent matches the D1 tangent at the pick point.
- With D1 and D2: an additional twist about the D1 tangent axis is applied to minimize the angular error between the S2-end tangent and D2 (or its reverse, whichever needs less rotation).
- The subject curve shape is not changed — only its position and orientation.

### vTextAligned flow

1. Run `vTextAligned`.
1. Click a curve to lock orientation base, or click existing text to edit/reposition it.
1. Pick placement point near locked curve.
1. Use options while placing:

    - `Text`: sets content for newly created text and active text edits.
    - `Height`: text height.
    - `Offset`: signed side offset from the curve to the true text bounds, including rotated and styled annotation bounds.
    - `Rotate`: rotates text orientation by 90 degrees each use.
    - `BothSides`: previews and creates a mirrored text pair with both visible text bounds centered at the same curve position and offset equally from the curve.
    - Newly created text inherits every group membership from the locked curve; local redo preserves those groups.

### vTextFlip flow

1. Run `vTextFlip`.
1. Select annotation text objects.
1. Use command options:

    - `Flip`: flips selected text orientation using object-local plane behavior.
    - `Rotate`: rotates selected text by 90 degrees.
    - `Clear`: clears current command selection list.

### vTitle flow

1. Run `vTitle`.
1. Move the cursor — existing vTitle objects highlight when the cursor enters their bounding box.
1. **Click existing title** → enter edit mode (loads its text, size, and settings; the object group is highlighted).
1. **Click empty space** → place a new title at the cursor position.
1. Options while placing / editing:

    - `Text`: title string.
    - `Size`: text height.
    - `Padding`: percentage of text height added as padding on each side of the bounding box.
    - `Box`: `Yes/No` — draw a padded bounding rectangle around the text.
    - `Layer`: target layer. Use `.` or `*` for the current layer; default is `Reference`.

1. Press Enter to confirm, Esc to cancel.

Notes:
- Editing an existing title replaces it in place.
- If the text annotation is later changed externally (e.g. via `Properties`), the bounding box resizes automatically on the next idle frame.
- All settings persist to `vTools.config.json` under `vTitle`.

### vToggleAxes flow

1. Run `vToggleAxes`.
1. The command toggles visible viewport axes: construction-plane grid axes and the display-mode Z axis are shown or hidden together.

### vToggleControlPoints flow

1. Select objects or selected points.
1. Run `vToggleControlPoints`.
1. The command is transparent, so it can be run while another Rhino command is active.
1. If off-curve control points are visible, the command switches the selection to edit points on the curve.
1. If edit points are visible or selected, the command switches the selection to off-curve control points where supported.
1. If no points are visible on the selected curves, the command shows edit points first.
1. Run with nothing selected to turn points off.

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

### vTrimOff flow

1. Select at least 2 curves (open or closed, crossing or adjacent).
1. The command finds all enclosed regions formed by the selected curves and computes their combined outer boundary.
1. The original selected curves are replaced by the trimmed boundary curves; protruding ends that extend outside the enclosed region are discarded.
1. Uses the active CPlane for planar region detection.

### vUnrollSrf flow

1. Optionally preselect a surface or polysurface to unroll.
1. Run `vUnrollSrf`.  The built-in `_UnrollSrf` command runs interactively with all its standard prompts and options.
1. After `UnrollSrf` completes, all newly created flat objects are automatically selected.  TextDot labels are included only when their position touches a newly created flat object; TextDots placed on the original 3D surface by Rhino as correspondence labels are not selected.
1. Press Enter to repeat `vUnrollSrf`.

### vUzip flow

1. Optionally preselect up to three U-shape arm curves before running.
1. Run `vUzip`.
1. Select the three U-shape curves (left arm, right arm, bottom).  Adjust options while selecting:

    - `Left` / `Right` / `Bottom`: inward offset distances for each arm.  Accepts decimal, fraction (`2 3/8`, `2-3/8`), or `z`/`zipper` keyword; offset distances may be `0`.
    - `Radius`: fillet radius at the two inside corners.
    - `Glass`: `Yes/No` — compute and show glass offset curves.
    - `Vis`: `Yes/No` — compute and show vis offset curves.
    - `Parts`: `Yes/No` — enable the parts-output pipeline.
    - `Label` *(when Parts=Yes)*: text label for generated part groups.
    - `Tail` *(when Parts=Yes)*: tail length for end curves.
    - `Options`: opens the full options dialog for layer names, colors, and additional offset values.

1. A cyan preview of the computed center curve is shown live.
1. When `Parts=No`: press Enter to accept, or click a boundary curve to trim/extend the ends.
1. When `Parts=Yes`: select boundary curves to trim/extend end caps; press Enter to accept.
1. The center curve (and optionally glass, vis, and parts output) is committed to the document.

### vUzipCenter flow

1. Select three curves that form a U shape (left arm, right arm, bottom). You may preselect up to three curves before running the command; a fourth preselected curve is used as the initial boundary.
1. While selecting curves, adjust offset and fillet options:

    - `Left`: offset distance for the left arm.
    - `Right`: offset distance for the right arm.
    - `Bottom`: offset distance for the bottom curve.
    - `Radius`: fillet radius at the two inside corners.

   Distances accept fractional inch input (`2 3/8`, `2-3/8`, `3/8`, plain decimal) and the shorthand `z`/`zipper` (returns the left-arm default). Offset distances may be `0`; radius must be greater than `0`.

1. A cyan preview curve is displayed showing the computed result.
1. While previewing, adjust the same options to recompute live.
1. Click any existing curve to set or replace the boundary: the result is trimmed or extended to meet it.
1. Press Enter to accept and add the result curve to the document.
1. Options are saved to `vTools.config.json` under the `vUzipCenter` section.

### vUzipParts flow

1. Select the center curve.
1. Adjust runtime options in the command prompt.

    - `Label`: text used when naming generated output.
    - `Tail`: tail distance used when building end curves.

1. Pick placement point for generated groups, or cancel placement to remove generated objects.
1. While picking the placement point, the `Label` and `Tail` options remain available. Changing either triggers a full rebuild of all parts before placement continues.

## Configuration

Commands read and write vTools.config.json next to the plug-in assembly. Sections are created per command as options are used.
Builds merge newly introduced default keys into that runtime file without replacing existing values or custom sections.

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

## Startup output

When the plug-in loads, Rhino's command history shows:

```
vTools v26.7.3.HMM loaded — N commands: vBiminiParts, vChamfer, ...
```

The same line plus the DLL path is written to `vTools.log` beside the loaded DLL.

## Logging

- `vTools.log` beside the loaded DLL — cleared on every Rhino startup. First lines show version and command list. All commands write diagnostics here via `Log.Write(tag, message)`.

## Versioning

This project uses CalVer-style metadata (`yy.m.d.hmm`) in project and assembly versions, with no seconds and non-padded month/day/hour. The timestamp is generated from the newest plugin source file (`.cs`) rather than the compile time.

## License

This project is released under the MIT License. See LICENSE.
