# vTools

vTools is a Rhino 8 plug-in project (C# / .NET 7) that currently provides the vUZIP command for generating multi-part zipper-style toolpath geometry from a selected center curve.

## What this project includes

- Rhino plug-in entry point: vToolsPlugIn
- Main command: vUZIP
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

## Output

Release output is written to:

- bin/Release/net7.0-windows/vTools.dll
- bin/Release/net7.0-windows/vTools.config.json

## Rhino usage

1. Load the plug-in assembly in Rhino.
2. Run command vUZIP.
3. Select the center curve.
4. Adjust runtime options (label/tail) in the command prompt.
5. Pick placement point for generated groups, or cancel placement to remove generated objects.

## Configuration

The command reads and writes vTools.config.json next to the plug-in assembly. The active command section is vUZIP.

Example:

```json
{
  "vUZIP": {
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
- Command run log: logs/vUzip.log

The code resolves a project-local logs folder first and falls back to an assembly-local logs folder.

## Versioning

This project uses CalVer-style metadata (YY.MM.DD.HHMMSS) in project and assembly informational versions.

## License

This project is released under the MIT License. See LICENSE.
