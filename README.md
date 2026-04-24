# vTools

vTools is a Rhino 8 plug-in project (C# / .NET 7) that provides native RhinoCommon commands for zipper, orient, trim/extend, and gumball workflows.

## What this project includes

- Rhino plug-in entry point: vToolsPlugIn
- Native commands:
  - [vCurveToSpline](#vcurvetospline-flow)
  - [vFitBox](#vfitbox-flow)
  - [vOrient2pt](#vorient2pt-flow)
  - [vOrient3pt](#vorient3pt-flow)
  - [vTogglePerpGumball](#vtoggleperpgumball-flow)
  - [vTrim](#vtrim-flow)
  - [vUzip](#vuzip-flow)
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

Native commands: [vCurveToSpline](#vcurvetospline-flow), [vFitBox](#vfitbox-flow), [vOrient2pt](#vorient2pt-flow), [vOrient3pt](#vorient3pt-flow), [vTogglePerpGumball](#vtoggleperpgumball-flow), [vTrim](#vtrim-flow), [vUzip](#vuzip-flow).

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
