using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace vTools.Commands;

/// <summary>
/// Builds multi-part zipper-style geometry from a selected center curve and
/// preselected helper curves, driven by the shared <c>vTools.config.json</c> file.
/// </summary>
public class vUzip : Command
{
  /// <summary>
  /// Rhino command name.
  /// </summary>
  public override string EnglishName => "vUzip";

  // Shared config and default runtime values for vUzip.
  private const string ToolsConfigFileName = "vTools.config.json";
  private const string DefaultLayerCutName = "CUT1";
  private const string DefaultLayerPlotName = "PLOT";
  private const string DefaultLayerReferenceName = "Reference";
  private const string DefaultLayerCutColor = "#CC3333";
  private const string DefaultLayerPlotColor = "#0F8A8A";
  private const string DefaultLayerReferenceColor = "#FFFFFF";
  private const string DefaultLabel = "";
  private const double DefaultTail = 0.75;
  private const double PartGap = 0.5;
  private const double LabelOffsetAlong = 0.125;
  private const double LabelOffsetPerp = 0.125;

  private static string LayerCut = DefaultLayerCutName;
  private static string LayerPlot = DefaultLayerPlotName;
  private static string LayerReference = DefaultLayerReferenceName;

  private static readonly JsonSerializerOptions JsonOptions = new()
  {
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true
  };

  /// <summary>
  /// Serializable layer entry for one logical layer in configuration.
  /// </summary>
  private sealed class LayerConfigEntry
  {
    public string Name { get; set; } = "";
    public string Color { get; set; } = "";
  }

  /// <summary>
  /// Layer configuration block used by vUzip.
  /// </summary>
  private sealed class UZipLayersConfig
  {
    public LayerConfigEntry Reference { get; set; } = new();
    public LayerConfigEntry Plot { get; set; } = new();
    public LayerConfigEntry Cut { get; set; } = new();
  }

  /// <summary>
  /// vUzip section inside the shared tools configuration file.
  /// </summary>
  private sealed class UZipConfigSection
  {
    public string Label { get; set; } = DefaultLabel;
    public double Tail { get; set; } = DefaultTail;
    public UZipLayersConfig Layers { get; set; } = new();
    public List<PartSpec> Parts { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
  }

  /// <summary>
  /// Root document for the shared tools configuration file.
  /// </summary>
  private sealed class ToolsConfigRoot
  {
    public UZipConfigSection? vUzip { get; set; } = new();

    // Backward compatibility for existing config files that still use the old section key.
    [JsonPropertyName("vUZIP")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UZipConfigSection? LegacyVUZIP { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalSections { get; set; }
  }

  /// <summary>
  /// Normalized layer names/colors resolved from config defaults and overrides.
  /// </summary>
  private sealed class LayerRuntime
  {
    public string ReferenceName { get; set; } = DefaultLayerReferenceName;
    public string PlotName { get; set; } = DefaultLayerPlotName;
    public string CutName { get; set; } = DefaultLayerCutName;
    public string ReferenceColorHex { get; set; } = DefaultLayerReferenceColor;
    public string PlotColorHex { get; set; } = DefaultLayerPlotColor;
    public string CutColorHex { get; set; } = DefaultLayerCutColor;

    /// <summary>
    /// Converts the normalized runtime values into a serializable config block.
    /// </summary>
    public UZipLayersConfig ToConfig()
    {
      return new UZipLayersConfig
      {
        Reference = new LayerConfigEntry { Name = ReferenceName, Color = ReferenceColorHex },
        Plot = new LayerConfigEntry { Name = PlotName, Color = PlotColorHex },
        Cut = new LayerConfigEntry { Name = CutName, Color = CutColorHex }
      };
    }
  }

  /// <summary>
  /// Curve payload plus source metadata used during part construction.
  /// </summary>
  private sealed record CurveItem(Curve Curve, string LayerName, Guid? ObjectId);

  /// <summary>
  /// One offset definition in a part specification.
  /// </summary>
  private sealed class OffsetSpec
  {
    public string Name { get; set; } = "";
    public double Offset { get; set; }
    public string? Layer { get; set; }
  }

  /// <summary>
  /// One generated part definition consumed by the vUzip build pipeline.
  /// </summary>
  private sealed class PartSpec
  {
    public string Name { get; set; } = "";
    public string? CenterLayer { get; set; }
    public string CenterEndMode { get; set; } = "";
    public string Note { get; set; } = "";
    public bool MirrorPart { get; set; }
    public List<OffsetSpec> Offsets { get; set; } = new();
    public string BandInsideName { get; set; } = "";
    public string BandOutsideName { get; set; } = "";
  }

  /// <summary>
  /// Executes vUzip: reads config, gathers inputs, builds parts, and updates config.
  /// </summary>
  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    var configPath = GetToolsConfigPath();

    var toolsConfig = LoadToolsConfig(configPath);
    var uZipConfig = EnsureUZipSection(toolsConfig);
    var layerRuntime = NormalizeLayerRuntime(uZipConfig.Layers);
    ApplyLayerRuntime(layerRuntime);
    EnsureCommandLayers(doc, layerRuntime);

    var defaultLabel = (uZipConfig.Label ?? DefaultLabel).Trim();
    var defaultTail = Math.Max(0.0, uZipConfig.Tail);
    uZipConfig.Label = defaultLabel;
    uZipConfig.Tail = defaultTail;
    uZipConfig.Layers = layerRuntime.ToConfig();
    SaveToolsConfig(configPath, toolsConfig);

    var preselected = doc.Objects.GetSelectedObjects(false, false)
      .Where(o => o != null)
      .Select(o => o.Id)
      .ToList();

    var selected = SelectCenterCurve(doc, defaultLabel, defaultTail);
    if (selected.CenterId == Guid.Empty)
    {
      uZipConfig.Label = (selected.Label ?? DefaultLabel).Trim();
      uZipConfig.Tail = Math.Max(0.0, selected.Tail);
      uZipConfig.Layers = layerRuntime.ToConfig();
      SaveToolsConfig(configPath, toolsConfig);
      return Result.Cancel;
    }

    var centerCurve = CurveFromId(doc, selected.CenterId);
    if (centerCurve == null)
      return Result.Failure;

    var centerLayer = ObjLayerName(doc, selected.CenterId);
    var centerItem = new CurveItem(centerCurve.DuplicateCurve(), centerLayer, selected.CenterId);

    var touching = CollectPreselected(doc, selected.CenterId, centerCurve, preselected, requireTouch: true);
    var allPreselected = CollectPreselected(doc, selected.CenterId, centerCurve, preselected, requireTouch: false);

    var plane = GetCurvePlane(centerCurve);
    var insideSign = SolveInsideSign(doc, centerCurve, plane);

    var (endCurves, endParentIds) = BuildEndCurves(doc, touching, centerCurve, plane, selected.Tail);
    if (Math.Abs(selected.Tail) <= RhinoMath.ZeroTolerance && endParentIds.Count > 0)
    {
      allPreselected = allPreselected.Where(i => !i.ObjectId.HasValue || !endParentIds.Contains(i.ObjectId.Value)).ToList();
      touching = touching.Where(i => !i.ObjectId.HasValue || !endParentIds.Contains(i.ObjectId.Value)).ToList();
    }

    var touchingIds = touching.Where(t => t.ObjectId.HasValue).Select(t => t.ObjectId!.Value).ToHashSet();
    var specs = ResolvePartSpecs(centerLayer, uZipConfig.Parts, layerRuntime);
    uZipConfig.Parts = specs;
    if (specs.Count == 0)
    {
      RhinoApp.WriteLine("vUzip: no part definitions found in config.");
      return Result.Failure;
    }
    var stamp = DateTime.Now.ToString("yyMMddHHmmss", CultureInfo.InvariantCulture);

    var watch = System.Diagnostics.Stopwatch.StartNew();

    var allPartObjectIds = new List<List<Guid>>();
    var createdGroups = new List<string>();

    doc.Views.RedrawEnabled = false;
    try
    {
      for (var i = 0; i < specs.Count; i++)
      {
        var (partIds, groupName) = MakePart(
          doc,
          i + 1,
          stamp,
          selected.Label,
          centerItem,
          allPreselected,
          touchingIds,
          centerCurve,
          plane,
          insideSign,
          endCurves,
          specs[i]);

        allPartObjectIds.Add(partIds);
        if (!string.IsNullOrWhiteSpace(groupName))
          createdGroups.Add(groupName);
      }

      StackPartsDown(doc, allPartObjectIds, PartGap);
    }
    finally
    {
      doc.Views.RedrawEnabled = true;
      doc.Views.Redraw();
    }

    watch.Stop();
    RhinoApp.WriteLine($"vUzip built in {watch.Elapsed.TotalSeconds:F3}s");

    var anchorGroup = createdGroups.Count > 0 ? createdGroups[0] : null;
    PlaceGroupsWithPickOrDelete(doc, createdGroups, anchorGroup);

    uZipConfig.Label = (selected.Label ?? DefaultLabel).Trim();
    uZipConfig.Tail = Math.Max(0.0, selected.Tail);
    uZipConfig.Layers = layerRuntime.ToConfig();
    uZipConfig.Parts = specs;

    SaveToolsConfig(configPath, toolsConfig);
    doc.Views.Redraw();
    return Result.Success;
  }

  /// <summary>
  /// Returns the plug-in deployment directory used for configuration storage.
  /// </summary>
  private static string GetPluginDataDirectory()
  {
    var pluginDir = Path.GetDirectoryName(typeof(vUzip).Assembly.Location) ?? string.Empty;
    if (string.IsNullOrWhiteSpace(pluginDir))
      pluginDir = ".";

    Directory.CreateDirectory(pluginDir);
    return pluginDir;
  }

  /// <summary>
  /// Gets the full path of the shared tools configuration file.
  /// </summary>
  private static string GetToolsConfigPath()
  {
    return Path.Combine(GetPluginDataDirectory(), ToolsConfigFileName);
  }

  /// <summary>
  /// Loads shared tools configuration from disk, falling back to defaults.
  /// </summary>
  private static ToolsConfigRoot LoadToolsConfig(string path)
  {
    try
    {
      if (File.Exists(path))
      {
        var json = File.ReadAllText(path);
        if (!string.IsNullOrWhiteSpace(json))
        {
          var loaded = JsonSerializer.Deserialize<ToolsConfigRoot>(json, JsonOptions);
          if (loaded != null)
            return loaded;
        }
      }
    }
    catch
    {
    }

    return CreateDefaultToolsConfigRoot();
  }

  /// <summary>
  /// Saves the shared tools configuration atomically via a temporary file.
  /// </summary>
  private static bool SaveToolsConfig(string path, ToolsConfigRoot config)
  {
    try
    {
      var parent = Path.GetDirectoryName(path);
      if (!string.IsNullOrWhiteSpace(parent))
        Directory.CreateDirectory(parent);

      var json = JsonSerializer.Serialize(config, JsonOptions);
      var tmp = path + ".tmp";
      File.WriteAllText(tmp, json);
      File.Copy(tmp, path, true);
      File.Delete(tmp);
      return true;
    }
    catch
    {
      return false;
    }
  }

  /// <summary>
  /// Creates a default tools configuration document with vUzip defaults.
  /// </summary>
  private static ToolsConfigRoot CreateDefaultToolsConfigRoot()
  {
    var layers = NormalizeLayerRuntime(null);
    return new ToolsConfigRoot
    {
      vUzip = new UZipConfigSection
      {
        Label = DefaultLabel,
        Tail = DefaultTail,
        Layers = layers.ToConfig(),
        Parts = CreateDefaultPartSpecs(layers)
      }
    };
  }

  /// <summary>
  /// Ensures the vUzip section exists and is normalized for safe runtime use.
  /// </summary>
  private static UZipConfigSection EnsureUZipSection(ToolsConfigRoot root)
  {
    if (root.vUzip == null)
      root.vUzip = root.LegacyVUZIP ?? new UZipConfigSection();

    root.LegacyVUZIP = null;

    var section = root.vUzip!;
    section.Label = (section.Label ?? DefaultLabel).Trim();
    section.Tail = Math.Max(0.0, section.Tail);

    if (section.Parts == null)
      section.Parts = new List<PartSpec>();

    var runtime = NormalizeLayerRuntime(section.Layers);
    section.Layers = runtime.ToConfig();

    if (section.Parts.Count == 0)
      section.Parts = CreateDefaultPartSpecs(runtime);

    return section;
  }

  /// <summary>
  /// Normalizes layer names and colors from config and applies defaults.
  /// </summary>
  private static LayerRuntime NormalizeLayerRuntime(UZipLayersConfig? layers)
  {
    var runtime = new LayerRuntime
    {
      ReferenceName = NormalizeLayerName(layers?.Reference?.Name, DefaultLayerReferenceName),
      PlotName = NormalizeLayerName(layers?.Plot?.Name, DefaultLayerPlotName),
      CutName = NormalizeLayerName(layers?.Cut?.Name, DefaultLayerCutName),
      ReferenceColorHex = NormalizeHexColor(layers?.Reference?.Color, DefaultLayerReferenceColor),
      PlotColorHex = NormalizeHexColor(layers?.Plot?.Color, DefaultLayerPlotColor),
      CutColorHex = NormalizeHexColor(layers?.Cut?.Color, DefaultLayerCutColor)
    };

    return runtime;
  }

  /// <summary>
  /// Applies normalized runtime layer names to command-level fields.
  /// </summary>
  private static void ApplyLayerRuntime(LayerRuntime runtime)
  {
    LayerReference = runtime.ReferenceName;
    LayerPlot = runtime.PlotName;
    LayerCut = runtime.CutName;
  }

  private static string NormalizeLayerName(string? candidate, string fallback)
  {
    var name = (candidate ?? "").Trim();
    return string.IsNullOrWhiteSpace(name) ? fallback : name;
  }

  private static string NormalizeHexColor(string? candidate, string fallback)
  {
    if (TryParseHexColor(candidate, out var parsed))
      return $"#{parsed.R:X2}{parsed.G:X2}{parsed.B:X2}";

    if (TryParseHexColor(fallback, out parsed))
      return $"#{parsed.R:X2}{parsed.G:X2}{parsed.B:X2}";

    return "#FFFFFF";
  }

  private static bool TryParseHexColor(string? value, out System.Drawing.Color color)
  {
    color = System.Drawing.Color.White;

    if (string.IsNullOrWhiteSpace(value))
      return false;

    var s = value.Trim();
    if (s.StartsWith("#", StringComparison.Ordinal))
      s = s[1..];

    if (s.Length != 6)
      return false;

    if (!int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
      return false;

    color = System.Drawing.Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
    return true;
  }

  private static (Guid CenterId, string Label, double Tail) SelectCenterCurve(RhinoDoc doc, string defaultLabel, double defaultTail)
  {
    var label = defaultLabel;
    var tail = Math.Max(0.0, defaultTail);

    while (true)
    {
      var tailOpt = new OptionDouble(tail, 0.0, 1e9);
      var go = new GetObject();
      go.SetCommandPrompt("Select center curve of U-Zip");
      go.GeometryFilter = ObjectType.Curve;
      go.SubObjectSelect = false;
      go.EnablePreSelect(false, true);
      go.AlreadySelectedObjectSelect = true;
      go.EnableClearObjectsOnEntry(false);
      go.EnableUnselectObjectsOnExit(false);
      go.DeselectAllBeforePostSelect = false;
      go.AcceptString(true);
      go.AcceptNumber(true, false);

      var labelOptionIndex = go.AddOption("Label", label);
      go.AddOptionDouble("Tail", ref tailOpt);

      var result = go.Get();
      tail = Math.Max(0.0, tailOpt.CurrentValue);
      if (go.CommandResult() != Result.Success)
        return (Guid.Empty, label, tail);

      if (result == GetResult.Object)
      {
        var obj = go.Object(0);
        if (obj == null)
          continue;
        return (obj.ObjectId, label, tail);
      }

      if (result == GetResult.Option)
      {
        var opt = go.Option();
        if (opt != null && opt.Index == labelOptionIndex)
        {
          var rc = RhinoGet.GetString("Label", true, ref label);
          if (rc == Result.Cancel)
            return (Guid.Empty, label, tail);
          label = (label ?? DefaultLabel).Trim();
        }
        continue;
      }

      if (result == GetResult.Number)
      {
        tail = Math.Max(0.0, go.Number());
        continue;
      }

      if (result == GetResult.String)
      {
        var typed = go.StringResult()?.Trim();
        if (!string.IsNullOrWhiteSpace(typed))
          label = typed;
        continue;
      }

      var residualTyped = go.StringResult()?.Trim();
      if (!string.IsNullOrWhiteSpace(residualTyped))
      {
        label = residualTyped;
        continue;
      }

      return (Guid.Empty, label, tail);
    }
  }

  private static Curve? CurveFromId(RhinoDoc doc, Guid id)
  {
    var obj = doc.Objects.FindId(id);
    return obj?.Geometry as Curve;
  }

  private static string ObjLayerName(RhinoDoc doc, Guid id)
  {
    var obj = doc.Objects.FindId(id);
    if (obj == null)
      return doc.Layers.CurrentLayer.FullPath;

    var idx = obj.Attributes.LayerIndex;
    var layer = doc.Layers[idx];
    return layer?.FullPath ?? doc.Layers.CurrentLayer.FullPath;
  }

  private static HashSet<string> ObjectGroups(RhinoDoc doc, Guid id)
  {
    var obj = doc.Objects.FindId(id);
    if (obj == null)
      return new HashSet<string>();

    var names = obj.Attributes.GetGroupList()
      ?.Select(groupIndex => doc.Groups.FindIndex(groupIndex)?.Name)
      .Where(name => !string.IsNullOrWhiteSpace(name))
      .Cast<string>()
      .ToHashSet();

    return names ?? new HashSet<string>();
  }

  private static List<CurveItem> CollectPreselected(
    RhinoDoc doc,
    Guid centerId,
    Curve centerCurve,
    List<Guid> sourceIds,
    bool requireTouch)
  {
    var result = new List<CurveItem>();
    var centerGroups = ObjectGroups(doc, centerId);

    foreach (var id in sourceIds)
    {
      if (id == centerId)
        continue;

      if (centerGroups.Count > 0)
      {
        var groups = ObjectGroups(doc, id);
        if (groups.Overlaps(centerGroups))
          continue;
      }

      var curve = CurveFromId(doc, id);
      if (curve == null)
        continue;

      if (requireTouch && !CurvesIntersect(centerCurve, curve, doc.ModelAbsoluteTolerance))
        continue;

      result.Add(new CurveItem(curve.DuplicateCurve(), ObjLayerName(doc, id), id));
    }

    return result;
  }

  private static bool CurvesIntersect(Curve a, Curve b, double tolerance)
  {
    var events = Rhino.Geometry.Intersect.Intersection.CurveCurve(a, b, tolerance, tolerance);
    return events != null && events.Count > 0;
  }

  private static Plane GetCurvePlane(Curve curve)
  {
    return curve.TryGetPlane(out var plane) ? plane : Plane.WorldXY;
  }

  private static Curve? PickPrimaryOffset(IEnumerable<Curve> curves)
  {
    Curve? best = null;
    var bestLen = -1.0;

    foreach (var curve in curves)
    {
      var len = CurveLength(curve);
      if (len > bestLen)
      {
        bestLen = len;
        best = curve;
      }
    }

    return best;
  }

  private static Curve? OffsetSingle(RhinoDoc doc, Curve curve, Plane plane, double signedDistance)
  {
    var offsets = curve.Offset(plane, signedDistance, doc.ModelAbsoluteTolerance, CurveOffsetCornerStyle.Sharp);
    if (offsets == null)
      return null;
    return PickPrimaryOffset(offsets);
  }

  private static double CurveLength(Curve? curve)
  {
    if (curve == null)
      return 0.0;
    return curve.GetLength();
  }

  private static Point3d CurveMidpoint(Curve curve)
  {
    var len = curve.GetLength();
    if (len <= RhinoMath.ZeroTolerance)
      return curve.PointAtStart;
    return curve.LengthParameter(0.5 * len, out var t) ? curve.PointAt(t) : curve.PointAt(curve.Domain.Mid);
  }

  private static Point3d OpeningReferencePoint(Curve curve)
  {
    return new Point3d(
      (curve.PointAtStart.X + curve.PointAtEnd.X) * 0.5,
      (curve.PointAtStart.Y + curve.PointAtEnd.Y) * 0.5,
      (curve.PointAtStart.Z + curve.PointAtEnd.Z) * 0.5);
  }

  private static int SolveInsideSign(RhinoDoc doc, Curve centerCurve, Plane plane)
  {
    var openingPt = OpeningReferencePoint(centerCurve);
    var sampleDist = Math.Max(doc.ModelAbsoluteTolerance * 10.0, 0.01);

    var plus = OffsetSingle(doc, centerCurve, plane, sampleDist);
    var minus = OffsetSingle(doc, centerCurve, plane, -sampleDist);

    if (plus == null && minus == null)
      return 1;
    if (plus == null)
      return -1;
    if (minus == null)
      return 1;

    var dPlus = CurveMidpoint(plus).DistanceTo(openingPt);
    var dMinus = CurveMidpoint(minus).DistanceTo(openingPt);
    return dPlus <= dMinus ? 1 : -1;
  }

  private static Curve? OffsetAtSide(RhinoDoc doc, Curve centerCurve, Plane plane, int insideSign, double distanceAbs, string side)
  {
    if (distanceAbs <= RhinoMath.ZeroTolerance)
      return centerCurve.DuplicateCurve();

    var sign = side.Equals("inside", StringComparison.OrdinalIgnoreCase) ? insideSign : -insideSign;
    var curve = OffsetSingle(doc, centerCurve, plane, sign * distanceAbs);
    if (curve != null)
      return curve;

    return OffsetSingle(doc, centerCurve, plane, -sign * distanceAbs);
  }

  private static double ClosestPointDistance(Curve curve, Point3d point)
  {
    if (!curve.ClosestPoint(point, out var t))
      return double.PositiveInfinity;

    return curve.PointAt(t).DistanceTo(point);
  }

  private static double CenterDistanceToEnd(Curve centerCurve, double parameter, bool toStart)
  {
    var d0 = centerCurve.Domain.T0;
    var d1 = centerCurve.Domain.T1;
    var min = Math.Min(d0, d1);
    var max = Math.Max(d0, d1);
    var p = Math.Max(min, Math.Min(max, parameter));

    var span = toStart ? new Interval(d0, p) : new Interval(p, d1);
    if (Math.Abs(span.Length) <= RhinoMath.ZeroTolerance)
      return 0.0;

    try
    {
      var part = centerCurve.Trim(span);
      if (part != null)
        return Math.Max(0.0, part.GetLength());
    }
    catch
    {
    }

    var point = centerCurve.PointAt(p);
    return point.DistanceTo(toStart ? centerCurve.PointAtStart : centerCurve.PointAtEnd);
  }

  private static CurveItem? SelectEndCapCurve(
    RhinoDoc doc,
    IReadOnlyList<CurveItem> items,
    Curve centerCurve,
    bool forStart,
    double tolerance)
  {
    CurveItem? best = null;
    var endPoint = forStart ? centerCurve.PointAtStart : centerCurve.PointAtEnd;
    var bestEndDist = double.PositiveInfinity;
    var tieSideDist = double.PositiveInfinity;

    foreach (var item in items)
    {
      var centerParams = UniqueParams(IntersectionParams(doc, centerCurve, item.Curve), tolerance);
      if (centerParams.Count == 0)
        continue;

      var itemBest = double.PositiveInfinity;
      foreach (var p in centerParams)
      {
        var sideDist = CenterDistanceToEnd(centerCurve, p, toStart: forStart);
        var oppositeDist = CenterDistanceToEnd(centerCurve, p, toStart: !forStart);

        // This crossing must actually belong to the requested end side.
        if (sideDist <= oppositeDist + tolerance)
          itemBest = Math.Min(itemBest, sideDist);
      }

      if (!double.IsFinite(itemBest))
        continue;

      var itemEndDist = ClosestPointDistance(item.Curve, endPoint);
      if (best == null
          || itemEndDist < bestEndDist - tolerance
          || (Math.Abs(itemEndDist - bestEndDist) <= tolerance && itemBest < tieSideDist - tolerance))
      {
        best = item;
        bestEndDist = itemEndDist;
        tieSideDist = itemBest;
      }
    }

    return best;
  }

  private static (List<Curve> EndCurves, HashSet<Guid> EndParentIds) BuildEndCurves(
    RhinoDoc doc,
    List<CurveItem> preselected,
    Curve centerCurve,
    Plane plane,
    double tail)
  {
    var endCurves = new List<Curve>();
    var parentIds = new HashSet<Guid>();
    if (preselected.Count == 0)
      return (endCurves, parentIds);

    var centerMid = CurveMidpoint(centerCurve);
    var tol = doc.ModelAbsoluteTolerance;
    var endSources = new List<(CurveItem Item, Point3d EndPoint)>();

    var startEndPoint = centerCurve.PointAtStart;
    var endEndPoint = centerCurve.PointAtEnd;

    var start = SelectEndCapCurve(doc, preselected, centerCurve, forStart: true, tol);
    if (start != null)
    {
      endSources.Add((start, startEndPoint));
    }

    // Choose end cap independently from start selection.
    var end = SelectEndCapCurve(doc, preselected, centerCurve, forStart: false, tol);

    if (end != null)
      endSources.Add((end, endEndPoint));

    var tailAbs = Math.Abs(tail);
    foreach (var (item, endPoint) in endSources)
    {
      var baseCurve = item.Curve.DuplicateCurve();
      if (baseCurve == null)
        continue;

      if (tailAbs > RhinoMath.ZeroTolerance)
      {
        var shifted = OffsetAwayFromCenter(doc, baseCurve, plane, tailAbs, centerCurve, centerMid, endPoint);
        if (shifted != null)
        {
          endCurves.Add(shifted);
          continue;
        }
      }
      else if (item.ObjectId.HasValue)
      {
        parentIds.Add(item.ObjectId.Value);
      }

      endCurves.Add(baseCurve);
    }

    return (endCurves, parentIds);
  }

  private static double CurveDistanceScoreToCenterCurve(Curve testCurve, Curve centerCurve)
  {
    var samples = new[] { testCurve.Domain.T0, testCurve.Domain.Mid, testCurve.Domain.T1 };
    var dists = new List<double>();

    foreach (var t in samples)
    {
      var pt = testCurve.PointAt(t);
      if (!centerCurve.ClosestPoint(pt, out var tc))
        continue;
      dists.Add(pt.DistanceTo(centerCurve.PointAt(tc)));
    }

    return dists.Count == 0 ? 0.0 : dists.Average();
  }

  private static double AnchorDistanceToCenterMid(Curve testCurve, Point3d anchor, Point3d centerMid)
  {
    if (!testCurve.ClosestPoint(anchor, out var t))
      return 0.0;
    return testCurve.PointAt(t).DistanceTo(centerMid);
  }

  private static Curve? OffsetAwayFromCenter(
    RhinoDoc doc,
    Curve curve,
    Plane plane,
    double distance,
    Curve centerCurve,
    Point3d centerMid,
    Point3d anchor)
  {
    var plus = OffsetSingle(doc, curve, plane, distance);
    var minus = OffsetSingle(doc, curve, plane, -distance);

    if (plus == null && minus == null)
      return null;
    if (plus == null)
      return minus;
    if (minus == null)
      return plus;

    var plusAnchor = AnchorDistanceToCenterMid(plus, anchor, centerMid);
    var minusAnchor = AnchorDistanceToCenterMid(minus, anchor, centerMid);
    var plusScore = CurveDistanceScoreToCenterCurve(plus, centerCurve);
    var minusScore = CurveDistanceScoreToCenterCurve(minus, centerCurve);

    var tol = doc.ModelAbsoluteTolerance;
    if (plusAnchor > minusAnchor + tol)
      return plus;
    if (minusAnchor > plusAnchor + tol)
      return minus;
    return plusScore >= minusScore ? plus : minus;
  }

  private static List<double> IntersectionParams(RhinoDoc doc, Curve a, Curve b)
  {
    var values = new List<double>();
    var events = Rhino.Geometry.Intersect.Intersection.CurveCurve(a, b, doc.ModelAbsoluteTolerance, doc.ModelAbsoluteTolerance);
    if (events == null)
      return values;

    foreach (var ev in events)
    {
      if (ev.IsPoint)
      {
        values.Add(ev.ParameterA);
      }
      else if (ev.IsOverlap)
      {
        values.Add(ev.OverlapA.T0);
        values.Add(ev.OverlapA.T1);
      }
    }

    return values;
  }

  private static List<double> UniqueParams(IEnumerable<double> values, double tolerance)
  {
    var sorted = values.OrderBy(v => v).ToList();
    var unique = new List<double>();
    foreach (var v in sorted)
    {
      if (unique.Count == 0 || Math.Abs(v - unique[^1]) > tolerance)
        unique.Add(v);
    }
    return unique;
  }

  private static Curve TrimToMiddleBetweenEndCurves(RhinoDoc doc, Curve curve, IEnumerable<Curve> endCurves, Point3d centerMid)
  {
    var endList = endCurves.ToList();
    if (endList.Count == 0)
      return curve.DuplicateCurve();

    var allParams = new List<double>();
    foreach (var end in endList)
      allParams.AddRange(IntersectionParams(doc, curve, end));

    var splitParams = UniqueParams(allParams, doc.ModelAbsoluteTolerance);
    if (splitParams.Count == 0)
      return curve.DuplicateCurve();

    var pieces = curve.Split(splitParams);
    if (pieces == null || pieces.Length == 0)
      return curve.DuplicateCurve();

    Curve? best = null;
    double? bestDist = null;
    foreach (var piece in pieces)
    {
      if (piece == null)
        continue;
      var dist = CurveMidpoint(piece).DistanceTo(centerMid);
      if (best == null || dist < bestDist)
      {
        best = piece;
        bestDist = dist;
      }
    }

    return best ?? curve.DuplicateCurve();
  }

  private static Curve ExtendCurveToEnd(RhinoDoc doc, Curve curve, IEnumerable<Curve> endCurves, Point3d centerMid)
  {
    var endList = endCurves.ToList();
    if (endList.Count == 0)
      return curve.DuplicateCurve();

    var original = curve.DuplicateCurve();
    var candidate = curve.DuplicateCurve();

    for (var i = 0; i < 4; i++)
    {
      var allParams = new List<double>();
      foreach (var end in endList)
        allParams.AddRange(IntersectionParams(doc, candidate, end));
      if (UniqueParams(allParams, doc.ModelAbsoluteTolerance).Count >= 2)
        break;

      var extAmount = Math.Max(candidate.GetLength() * 2.0, 1.0);
      var extended = candidate.Extend(CurveEnd.Both, extAmount, CurveExtensionStyle.Line);
      if (extended == null)
        break;
      candidate = extended;
    }

    var trimmed = TrimToMiddleBetweenEndCurves(doc, candidate, endList, centerMid);

    var checkParams = new List<double>();
    foreach (var end in endList)
      checkParams.AddRange(IntersectionParams(doc, trimmed, end));

    if (UniqueParams(checkParams, doc.ModelAbsoluteTolerance).Count < 2)
      return original;

    return trimmed;
  }

  private static void EnsureCommandLayers(RhinoDoc doc, LayerRuntime runtime)
  {
    EnsureLayer(doc, runtime.ReferenceName, ParseHexColorOrDefault(runtime.ReferenceColorHex, DefaultLayerReferenceColor));
    EnsureLayer(doc, runtime.PlotName, ParseHexColorOrDefault(runtime.PlotColorHex, DefaultLayerPlotColor));
    EnsureLayer(doc, runtime.CutName, ParseHexColorOrDefault(runtime.CutColorHex, DefaultLayerCutColor));
  }

  private static System.Drawing.Color ParseHexColorOrDefault(string candidate, string fallback)
  {
    if (TryParseHexColor(candidate, out var parsed))
      return parsed;
    if (TryParseHexColor(fallback, out parsed))
      return parsed;
    return System.Drawing.Color.White;
  }

  private static int EnsureLayer(RhinoDoc doc, string layerName)
  {
    return EnsureLayer(doc, layerName, null);
  }

  private static int EnsureLayer(RhinoDoc doc, string layerName, System.Drawing.Color? color)
  {
    var idx = doc.Layers.FindByFullPath(layerName, -1);
    if (idx >= 0)
    {
      if (color.HasValue)
      {
        var existing = doc.Layers[idx];
        if (existing != null && existing.Color.ToArgb() != color.Value.ToArgb())
        {
          existing.Color = color.Value;
          doc.Layers.Modify(existing, idx, true);
        }
      }
      return idx;
    }

    var layer = new Layer { Name = layerName };
    if (color.HasValue)
      layer.Color = color.Value;

    var added = doc.Layers.Add(layer);
    if (added >= 0)
      return added;

    return doc.Layers.CurrentLayerIndex;
  }

  private static Guid AddCurve(RhinoDoc doc, Curve? curve, string layerName)
  {
    if (curve == null)
      return Guid.Empty;

    var attr = new ObjectAttributes { LayerIndex = EnsureLayer(doc, layerName) };
    return doc.Objects.AddCurve(curve, attr);
  }

  private static Guid AddText(RhinoDoc doc, TextEntity text, string layerName)
  {
    var attr = new ObjectAttributes { LayerIndex = EnsureLayer(doc, layerName) };
    return doc.Objects.AddText(text, attr);
  }

  private static void LockTextTopLeftToPoint(RhinoDoc doc, Guid textId, Point3d topLeftTarget)
  {
    var ro = doc.Objects.FindId(textId);
    if (ro?.Geometry is not TextEntity te)
      return;

    var bb = te.GetBoundingBox(true);
    if (!bb.IsValid)
      return;

    var plane = te.Plane;
    var xMin = double.PositiveInfinity;
    var yMax = double.NegativeInfinity;
    foreach (var c in bb.GetCorners())
    {
      var v = c - plane.Origin;
      var x = Vector3d.Multiply(v, plane.XAxis);
      var y = Vector3d.Multiply(v, plane.YAxis);
      if (x < xMin) xMin = x;
      if (y > yMax) yMax = y;
    }

    if (!double.IsFinite(xMin) || !double.IsFinite(yMax))
      return;

    var currentTopLeft = plane.Origin + plane.XAxis * xMin + plane.YAxis * yMax;
    var move = topLeftTarget - currentTopLeft;
    if (!move.IsValid || move.IsTiny())
      return;

    var copy = te.Duplicate() as TextEntity;
    if (copy == null)
      return;

    if (!copy.Transform(Transform.Translation(move)))
      return;

    doc.Objects.Replace(textId, copy);
  }

  private static bool CurveInsideOuterBoundary(RhinoDoc doc, Curve curve, Curve outerBoundary, Curve centerCurve)
  {
    var sampleParams = new[] { curve.Domain.T0, curve.Domain.Mid, curve.Domain.T1 };
    var insideHits = 0;
    var sampleCount = 0;

    foreach (var t in sampleParams)
    {
      var pt = curve.PointAt(t);
      if (!centerCurve.ClosestPoint(pt, out var tc))
        continue;
      var centerPt = centerCurve.PointAt(tc);
      var distToCenter = pt.DistanceTo(centerPt);

      if (!outerBoundary.ClosestPoint(centerPt, out var to))
        continue;
      var outerLimit = centerPt.DistanceTo(outerBoundary.PointAt(to));

      sampleCount++;
      if (distToCenter <= outerLimit + doc.ModelAbsoluteTolerance)
        insideHits++;
    }

    return sampleCount > 0 && insideHits * 2 >= sampleCount;
  }

  private static bool CurveOutsideInnerBoundary(RhinoDoc doc, Curve curve, Curve innerBoundary, Curve centerCurve)
  {
    var sampleParams = new[] { curve.Domain.T0, curve.Domain.Mid, curve.Domain.T1 };

    foreach (var t in sampleParams)
    {
      var pt = curve.PointAt(t);
      if (!centerCurve.ClosestPoint(pt, out var tc))
        continue;
      var centerPt = centerCurve.PointAt(tc);
      var distToCenter = pt.DistanceTo(centerPt);

      if (!innerBoundary.ClosestPoint(centerPt, out var ti))
        continue;
      var innerLimit = centerPt.DistanceTo(innerBoundary.PointAt(ti));

      if (distToCenter <= innerLimit + doc.ModelAbsoluteTolerance)
        return false;
    }

    return true;
  }

  private static List<Curve> SplitAndFilterForBand(RhinoDoc doc, Curve source, Curve innerBoundary, Curve outerBoundary, Curve centerCurve, bool useEndpointSideKeep, string? logKey = null)
  {
    var tol = doc.ModelAbsoluteTolerance;
    var final = new List<Curve>();

    var outParams = UniqueParams(IntersectionParams(doc, source, outerBoundary), tol);
    if (logKey != null)
      vToolsPlugIn.TryLog($"{logKey} outerParams={outParams.Count}[{string.Join(",", outParams.Select(p => $"{p:F4}"))}]");
    List<Curve> afterOuter;
    if (outParams.Count == 0)
    {
      afterOuter = new List<Curve> { source.DuplicateCurve() };
    }
    else
    {
      var splitOuter = source.Split(outParams) ?? Array.Empty<Curve>();
      var classifiedOuter = splitOuter
        .Where(c => c != null)
        .Where(c => CurveInsideOuterBoundary(doc, c, outerBoundary, centerCurve))
        .Select(c => c.DuplicateCurve())
        .ToList();
      if (logKey != null)
        vToolsPlugIn.TryLog($"{logKey} splitOuter={splitOuter.Length} classifiedOuter={classifiedOuter.Count}");

      if (classifiedOuter.Count > 0)
      {
        afterOuter = classifiedOuter;
      }
      else
      {
        // When split() drops a near-endpoint param (splitOuter < expected), recover by trimming
        // directly to [min, max] of the outer intersection params before trying the heavier fallback.
        Curve? trimmedOuter = null;
        if (outParams.Count >= 2)
        {
          var directTrim = source.Trim(outParams.Min(), outParams.Max());
          if (directTrim != null && directTrim.GetLength() > tol)
            trimmedOuter = directTrim;
        }
        if (trimmedOuter == null)
          trimmedOuter = TrimCurveEndsOutsideOuter(doc, source, outerBoundary, centerCurve);
        if (logKey != null)
          vToolsPlugIn.TryLog($"{logKey} trimmedOuter={(trimmedOuter != null ? $"len={trimmedOuter.GetLength():F4}" : "NULL")}");
        if (trimmedOuter != null && trimmedOuter.GetLength() > tol && !CurvesNearlySame(doc, trimmedOuter, source))
          afterOuter = new List<Curve> { trimmedOuter };
        else
          afterOuter = new List<Curve>();
      }
    }
    if (logKey != null)
      vToolsPlugIn.TryLog($"{logKey} afterOuter={afterOuter.Count}");

    foreach (var piece in afterOuter)
    {
      var inParams = UniqueParams(IntersectionParams(doc, piece, innerBoundary), tol);
      if (logKey != null)
        vToolsPlugIn.TryLog($"{logKey}   piece len={piece.GetLength():F4} inParams={inParams.Count}[{string.Join(",", inParams.Select(p => $"{p:F4}"))}]");
      if (inParams.Count == 0)
      {
        final.Add(piece.DuplicateCurve());
        continue;
      }

      if (inParams.Count >= 2)
      {
        var (_, keep, _) = OutsidePiecesForInnerTrim(doc, piece, innerBoundary, centerCurve, inParams);
        if (logKey != null)
          vToolsPlugIn.TryLog($"{logKey}   inParams>=2 keep={keep.Count}");
        final.AddRange(keep.Where(c => c != null).Select(c => c.DuplicateCurve()));
        continue;
      }

      var (trimmedInner, trimStart, trimEnd, sCenter, sInner, eCenter, eInner) = TrimCurveEndsInsideInnerDetailed(doc, piece, innerBoundary, centerCurve);
      if (logKey != null)
        vToolsPlugIn.TryLog($"{logKey}   trimmedInner={(trimmedInner != null ? $"len={trimmedInner.GetLength():F4}" : "NULL")} trimStart={trimStart} trimEnd={trimEnd} sC={sCenter:F4} sI={sInner:F4} eC={eCenter:F4} eI={eInner:F4}");

      if (useEndpointSideKeep)
      {
        Curve? keepPiece = null;
        var splitPieces = SplitCurveByBoundary(doc, piece, innerBoundary, inParams);

        if (trimStart == trimEnd)
        {
          var sSignal = sCenter - sInner;
          var eSignal = eCenter - eInner;
          if (sSignal > eSignal)
          {
            trimStart = true;
            trimEnd = false;
          }
          else if (eSignal > sSignal)
          {
            trimStart = false;
            trimEnd = true;
          }
        }

        if (trimStart && !trimEnd)
          keepPiece = PickPieceAttachedToEndpoint(splitPieces, piece.PointAtEnd, tol);
        else if (trimEnd && !trimStart)
          keepPiece = PickPieceAttachedToEndpoint(splitPieces, piece.PointAtStart, tol);

        if (keepPiece != null)
          trimmedInner = keepPiece;
        else
          trimmedInner = OutsidePieceForInnerOneCrossing(doc, piece, innerBoundary, centerCurve) ?? trimmedInner;
      }
      else if (trimmedInner == null)
      {
        trimmedInner = OutsidePieceForInnerOneCrossing(doc, piece, innerBoundary, centerCurve);
      }

      if (logKey != null)
        vToolsPlugIn.TryLog($"{logKey}   after inner resolve: trimmedInner={(trimmedInner != null ? $"len={trimmedInner.GetLength():F4}" : "NULL")}");

      if (trimmedInner != null)
      {
        final.Add(trimmedInner);
        continue;
      }

      var splitInner = piece.Split(inParams) ?? Array.Empty<Curve>();
      var outsideInner = splitInner.Where(c => c != null)
        .Where(c => CurveOutsideInnerBoundary(doc, c, innerBoundary, centerCurve))
        .Select(c => c.DuplicateCurve()).ToList();
      if (logKey != null)
        vToolsPlugIn.TryLog($"{logKey}   fallback splitInner={splitInner.Length} outsideInner={outsideInner.Count}");
      final.AddRange(outsideInner);
    }

    return final.Where(c => c != null && c.IsValid && c.GetLength() > tol).ToList();
  }

  private static List<Curve> SplitCurveByBoundary(RhinoDoc doc, Curve curve, Curve boundary, IEnumerable<double>? precomputed = null)
  {
    var ps = precomputed?.ToList() ?? UniqueParams(IntersectionParams(doc, curve, boundary), doc.ModelAbsoluteTolerance);
    if (ps.Count == 0)
      return new List<Curve> { curve.DuplicateCurve() };
    var split = curve.Split(ps);
    if (split == null || split.Length == 0)
      return new List<Curve> { curve.DuplicateCurve() };
    return split.Where(c => c != null).Select(c => c.DuplicateCurve()).ToList();
  }

  private static (List<Curve> Pieces, List<Curve> Keep, string Mode) OutsidePiecesForInnerTrim(
    RhinoDoc doc,
    Curve curve,
    Curve innerBoundary,
    Curve centerCurve,
    IEnumerable<double>? precomputed = null)
  {
    var tol = doc.ModelAbsoluteTolerance;
    var ps = precomputed?.ToList() ?? UniqueParams(IntersectionParams(doc, curve, innerBoundary), tol);
    var pieces = SplitCurveByBoundary(doc, curve, innerBoundary, ps);

    if (ps.Count >= 2 && pieces.Count >= 3)
    {
      var keep = new List<Curve>();
      if (pieces[0] != null && pieces[0].GetLength() > tol) keep.Add(pieces[0]);
      if (pieces[^1] != null && pieces[^1].GetLength() > tol) keep.Add(pieces[^1]);
      return (pieces, keep, "end-pieces");
    }

    var classified = pieces.Where(p => CurveOutsideInnerBoundary(doc, p, innerBoundary, centerCurve)).ToList();
    return (pieces, classified, "classified");
  }

  private static (Curve? Trimmed, bool TrimStart, bool TrimEnd, double SCenter, double SInner, double ECenter, double EInner)
    TrimCurveEndsInsideInnerDetailed(RhinoDoc doc, Curve curve, Curve innerBoundary, Curve centerCurve)
  {
    var tol = doc.ModelAbsoluteTolerance;
    var d0 = curve.Domain.T0;
    var d1 = curve.Domain.T1;

    var startPt = curve.PointAtStart;
    var endPt = curve.PointAtEnd;

    var ps = UniqueParams(IntersectionParams(doc, curve, innerBoundary), tol);
    if (ps.Count == 0)
      return (curve.DuplicateCurve(), false, false, 0.0, 0.0, 0.0, 0.0);

    if (!centerCurve.ClosestPoint(startPt, out var tcs) || !innerBoundary.ClosestPoint(startPt, out var tis))
      return (curve.DuplicateCurve(), false, false, 0.0, 0.0, 0.0, 0.0);
    if (!centerCurve.ClosestPoint(endPt, out var tce) || !innerBoundary.ClosestPoint(endPt, out var tie))
      return (curve.DuplicateCurve(), false, false, 0.0, 0.0, 0.0, 0.0);

    var sCenter = startPt.DistanceTo(centerCurve.PointAt(tcs));
    var sInner = startPt.DistanceTo(innerBoundary.PointAt(tis));
    var eCenter = endPt.DistanceTo(centerCurve.PointAt(tce));
    var eInner = endPt.DistanceTo(innerBoundary.PointAt(tie));

    var trimStart = sInner + tol < sCenter;
    var trimEnd = eInner + tol < eCenter;
    if (!trimStart && !trimEnd)
      return (curve.DuplicateCurve(), false, false, sCenter, sInner, eCenter, eInner);

    var keepStart = d0;
    var keepEnd = d1;

    if (trimStart)
    {
      var cand = ps.Where(p => p > d0 + tol).ToList();
      if (cand.Count > 0) keepStart = cand.Min();
      else trimStart = false;
    }
    if (trimEnd)
    {
      var cand = ps.Where(p => p < d1 - tol).ToList();
      if (cand.Count > 0) keepEnd = cand.Max();
      else trimEnd = false;
    }

    if (!trimStart && !trimEnd)
      return (curve.DuplicateCurve(), false, false, sCenter, sInner, eCenter, eInner);
    if (keepEnd - keepStart <= tol)
      return (null, trimStart, trimEnd, sCenter, sInner, eCenter, eInner);

    return (curve.Trim(new Interval(keepStart, keepEnd)), trimStart, trimEnd, sCenter, sInner, eCenter, eInner);
  }

  private static Curve? PickPieceAttachedToEndpoint(IEnumerable<Curve> pieces, Point3d endpoint, double tolerance)
  {
    Curve? best = null;
    double? bestDist = null;
    foreach (var piece in pieces)
    {
      if (piece == null)
        continue;
      var d0 = piece.PointAtStart.DistanceTo(endpoint);
      var d1 = piece.PointAtEnd.DistanceTo(endpoint);
      var d = Math.Min(d0, d1);
      if (best == null || d < bestDist)
      {
        best = piece;
        bestDist = d;
      }
    }

    if (best != null && bestDist.HasValue && bestDist.Value <= tolerance * 20.0)
      return best;

    return best;
  }

  private static Curve? OutsidePieceForInnerOneCrossing(RhinoDoc doc, Curve curve, Curve innerBoundary, Curve centerCurve)
  {
    var tol = doc.ModelAbsoluteTolerance;
    var ps = UniqueParams(IntersectionParams(doc, curve, innerBoundary), tol);
    if (ps.Count == 0)
      return null;

    List<Curve> pieces;
    if (ps.Count == 1)
    {
      pieces = new List<Curve>();
      var t = ps[0];
      var d0 = curve.Domain.T0;
      var d1 = curve.Domain.T1;

      if (t > d0 + tol)
      {
        var left = curve.Trim(new Interval(d0, t));
        if (left != null && left.GetLength() > tol) pieces.Add(left);
      }
      if (t < d1 - tol)
      {
        var right = curve.Trim(new Interval(t, d1));
        if (right != null && right.GetLength() > tol) pieces.Add(right);
      }
    }
    else
    {
      pieces = SplitCurveByBoundary(doc, curve, innerBoundary);
    }

    if (pieces.Count == 0)
      return null;

    var ranked = pieces
      .Select(p =>
      {
        var sample = new[] { p.Domain.T0, p.Domain.Mid, p.Domain.T1 };
        var outsideHits = 0;
        var avgMargin = 0.0;
        var count = 0;
        foreach (var tt in sample)
        {
          var pt = p.PointAt(tt);
          if (!centerCurve.ClosestPoint(pt, out var tc))
            continue;
          var centerPt = centerCurve.PointAt(tc);
          var dist = pt.DistanceTo(centerPt);
          if (!innerBoundary.ClosestPoint(centerPt, out var ti))
            continue;
          var limit = centerPt.DistanceTo(innerBoundary.PointAt(ti));
          var margin = dist - limit;
          if (margin > tol) outsideHits++;
          avgMargin += margin;
          count++;
        }
        if (count > 0) avgMargin /= count;
        return new { Piece = p, OutsideHits = outsideHits, AvgMargin = avgMargin, Len = p.GetLength() };
      })
      .OrderByDescending(x => x.OutsideHits)
      .ThenByDescending(x => x.AvgMargin)
      .ThenByDescending(x => x.Len)
      .FirstOrDefault();

    return ranked?.Piece;
  }

  private static Curve? TrimCurveEndsOutsideOuter(RhinoDoc doc, Curve curve, Curve outsideBoundary, Curve centerCurve)
  {
    var tol = doc.ModelAbsoluteTolerance;
    var d0 = curve.Domain.T0;
    var d1 = curve.Domain.T1;

    var startPt = curve.PointAtStart;
    var endPt = curve.PointAtEnd;

    var ps = UniqueParams(IntersectionParams(doc, curve, outsideBoundary), tol);
    if (ps.Count == 0)
      return curve.DuplicateCurve();

    if (!centerCurve.ClosestPoint(startPt, out var tcs) || !outsideBoundary.ClosestPoint(startPt, out var tos))
      return curve.DuplicateCurve();
    if (!centerCurve.ClosestPoint(endPt, out var tce) || !outsideBoundary.ClosestPoint(endPt, out var toe))
      return curve.DuplicateCurve();

    var sCenter = startPt.DistanceTo(centerCurve.PointAt(tcs));
    var sOuter = startPt.DistanceTo(outsideBoundary.PointAt(tos));
    var eCenter = endPt.DistanceTo(centerCurve.PointAt(tce));
    var eOuter = endPt.DistanceTo(outsideBoundary.PointAt(toe));

    var trimStart = sOuter + tol < sCenter;
    var trimEnd = eOuter + tol < eCenter;
    if (!trimStart && !trimEnd)
      return curve.DuplicateCurve();

    var keepStart = d0;
    var keepEnd = d1;

    if (trimStart)
    {
      var cand = ps.Where(p => p > d0 + tol).ToList();
      if (cand.Count > 0) keepStart = cand.Min();
      else trimStart = false;
    }
    if (trimEnd)
    {
      var cand = ps.Where(p => p < d1 - tol).ToList();
      if (cand.Count > 0) keepEnd = cand.Max();
      else trimEnd = false;
    }

    if (!trimStart && !trimEnd)
      return curve.DuplicateCurve();
    if (keepEnd - keepStart <= tol)
      return null;

    return curve.Trim(new Interval(keepStart, keepEnd));
  }

  private static Curve? TrimCurveEndsInsideInner(RhinoDoc doc, Curve curve, Curve innerBoundary, Curve centerCurve)
  {
    var tol = doc.ModelAbsoluteTolerance;
    var d0 = curve.Domain.T0;
    var d1 = curve.Domain.T1;

    var startPt = curve.PointAtStart;
    var endPt = curve.PointAtEnd;

    var ps = UniqueParams(IntersectionParams(doc, curve, innerBoundary), tol);
    if (ps.Count == 0)
      return curve.DuplicateCurve();

    if (!centerCurve.ClosestPoint(startPt, out var tcs) || !innerBoundary.ClosestPoint(startPt, out var tis))
      return curve.DuplicateCurve();
    if (!centerCurve.ClosestPoint(endPt, out var tce) || !innerBoundary.ClosestPoint(endPt, out var tie))
      return curve.DuplicateCurve();

    var sCenter = startPt.DistanceTo(centerCurve.PointAt(tcs));
    var sInner = startPt.DistanceTo(innerBoundary.PointAt(tis));
    var eCenter = endPt.DistanceTo(centerCurve.PointAt(tce));
    var eInner = endPt.DistanceTo(innerBoundary.PointAt(tie));

    var trimStart = sInner + tol < sCenter;
    var trimEnd = eInner + tol < eCenter;
    if (!trimStart && !trimEnd)
      return curve.DuplicateCurve();

    var keepStart = d0;
    var keepEnd = d1;

    if (trimStart)
    {
      var cand = ps.Where(p => p > d0 + tol).ToList();
      if (cand.Count > 0) keepStart = cand.Min();
      else trimStart = false;
    }
    if (trimEnd)
    {
      var cand = ps.Where(p => p < d1 - tol).ToList();
      if (cand.Count > 0) keepEnd = cand.Max();
      else trimEnd = false;
    }

    if (!trimStart && !trimEnd)
      return curve.DuplicateCurve();
    if (keepEnd - keepStart <= tol)
      return null;

    return curve.Trim(new Interval(keepStart, keepEnd));
  }

  private static bool CurveCrossesBoundary(RhinoDoc doc, Curve source, Curve boundary)
  {
    return UniqueParams(IntersectionParams(doc, source, boundary), doc.ModelAbsoluteTolerance).Count > 0;
  }

  private static (Point3d EndsMid, Vector3d XRight, Vector3d YDown, Point3d LeftEnd, Point3d RightEnd)? CenterLocalFrame(Curve centerCurve)
  {
    var pStart = centerCurve.PointAtStart;
    var pEnd = centerCurve.PointAtEnd;
    var endsMid = new Point3d(
      (pStart.X + pEnd.X) * 0.5,
      (pStart.Y + pEnd.Y) * 0.5,
      (pStart.Z + pEnd.Z) * 0.5);

    var okPlane = centerCurve.TryGetPlane(out var plane);
    if (!okPlane)
      plane = Plane.WorldXY;

    var normal = plane.Normal;
    if (!normal.Unitize())
      normal = Vector3d.ZAxis;
    if (Vector3d.Multiply(normal, Vector3d.ZAxis) < 0.0)
      normal = -normal;

    var chord = pEnd - pStart;
    chord -= normal * Vector3d.Multiply(chord, normal);
    if (!chord.Unitize())
      return null;

    Vector3d? yDown = null;
    var bestDev = -1.0;
    var sample = centerCurve.DivideByCount(48, true);
    if (sample != null)
    {
      foreach (var t in sample)
      {
        var p = centerCurve.PointAt(t);
        var v = p - endsMid;
        v -= normal * Vector3d.Multiply(v, normal);
        var perp = v - chord * Vector3d.Multiply(v, chord);
        var dev = perp.Length;
        if (dev > bestDev && dev > RhinoMath.ZeroTolerance)
        {
          if (perp.Unitize())
          {
            bestDev = dev;
            yDown = perp;
          }
        }
      }
    }

    if (!yDown.HasValue)
    {
      var pMid = CurveMidpoint(centerCurve);
      var yd = pMid - endsMid;
      yd -= normal * Vector3d.Multiply(yd, normal);
      if (!yd.Unitize())
      {
        yd = Vector3d.CrossProduct(chord, normal);
        if (!yd.Unitize())
          return null;
      }
      yDown = yd;
    }

    var xRight = Vector3d.CrossProduct(normal, yDown.Value);
    if (!xRight.Unitize())
      return null;

    var sStart = Vector3d.Multiply(pStart - endsMid, xRight);
    var sEnd = Vector3d.Multiply(pEnd - endsMid, xRight);
    var leftEnd = sStart <= sEnd ? pStart : pEnd;
    var rightEnd = sStart <= sEnd ? pEnd : pStart;

    return (endsMid, xRight, yDown.Value, leftEnd, rightEnd);
  }

  private static bool CurvesNearlySame(RhinoDoc doc, Curve a, Curve b)
  {
    var tol = doc.ModelAbsoluteTolerance;
    if (Math.Abs(a.GetLength() - b.GetLength()) > tol * 2.0)
      return false;

    foreach (var t in new[] { a.Domain.T0, a.Domain.Mid, a.Domain.T1 })
    {
      var pt = a.PointAt(t);
      if (!b.ClosestPoint(pt, out var tb))
        return false;
      if (pt.DistanceTo(b.PointAt(tb)) > tol * 2.0)
        return false;
    }

    return true;
  }

  private static List<Guid> AddPartLabel(
    RhinoDoc doc,
    string label,
    HashSet<Guid> endItemIds,
    Curve? outsideCurve,
    Curve? insideCurve,
    Curve? centerCurve)
  {
    var ids = new List<Guid>();
    var text = (label ?? "").Trim();
    if (text.Length == 0 || outsideCurve == null || insideCurve == null || endItemIds.Count == 0)
      return ids;

    var endCurves = endItemIds.Select(id => CurveFromId(doc, id)).Where(c => c != null).Cast<Curve>().ToList();
    if (endCurves.Count == 0)
      return ids;

    var frame = centerCurve != null ? CenterLocalFrame(centerCurve) : null;

    Curve? left = null;
    if (frame.HasValue)
    {
      var (endsMid, xRight, _, _, _) = frame.Value;
      double? bestS = null;
      foreach (var end in endCurves)
      {
        var bestOnCurve = double.MaxValue;
        var div = end.DivideByCount(24, true);
        if (div == null || div.Length == 0)
          div = new[] { end.Domain.Mid };

        foreach (var t in div)
        {
          var p = end.PointAt(t);
          var s = Vector3d.Multiply(p - endsMid, xRight);
          if (s < bestOnCurve)
            bestOnCurve = s;
        }

        if (left == null || bestOnCurve < bestS)
        {
          left = end;
          bestS = bestOnCurve;
        }
      }
    }
    else
    {
      double? leftX = null;
      foreach (var end in endCurves)
      {
        var mid = CurveMidpoint(end);
        if (left == null || mid.X < leftX)
        {
          left = end;
          leftX = mid.X;
        }
      }
    }

    if (left == null)
      return ids;

    var p0 = left.PointAtStart;
    var p1 = left.PointAtEnd;

    var contact = p0;
    var otherEnd = p1;
    if (frame.HasValue)
    {
      var (endsMid, xRight, _, _, _) = frame.Value;
      var s0 = Vector3d.Multiply(p0 - endsMid, xRight);
      var s1 = Vector3d.Multiply(p1 - endsMid, xRight);
      contact = s0 <= s1 ? p0 : p1;
      otherEnd = s0 <= s1 ? p1 : p0;
    }
    else
    {
      contact = p0.X <= p1.X ? p0 : p1;
      otherEnd = p0.X <= p1.X ? p1 : p0;
    }

    if (!outsideCurve.ClosestPoint(contact, out var to) || !insideCurve.ClosestPoint(contact, out var ti))
      return ids;

    var outPt = outsideCurve.PointAt(to);
    var inPt = insideCurve.PointAt(ti);
    var bandMid = new Point3d(
      (inPt.X + outPt.X) * 0.5,
      (inPt.Y + outPt.Y) * 0.5,
      (inPt.Z + outPt.Z) * 0.5);

    var tangent = otherEnd - contact;
    if (!tangent.Unitize())
      tangent = left.TangentAt(left.Domain.Mid);
    if (!tangent.Unitize())
      tangent = Vector3d.XAxis;

    var yAxis = bandMid - contact;
    yAxis -= tangent * Vector3d.Multiply(yAxis, tangent);
    if (!yAxis.Unitize())
      return ids;

    var topCurve = left;
    var curveLen = topCurve.GetLength();
    var alongDist = Math.Max(0.0, Math.Min(Math.Abs(LabelOffsetAlong), curveLen));
    var useFromStart = true;
    if (frame.HasValue)
    {
      var (endsMid, xRight, _, _, _) = frame.Value;
      var sStart = Vector3d.Multiply(topCurve.PointAtStart - endsMid, xRight);
      var sEnd = Vector3d.Multiply(topCurve.PointAtEnd - endsMid, xRight);
      useFromStart = sStart <= sEnd;
    }

    var alongT = topCurve.Domain.Mid;
    if (useFromStart)
    {
      if (!topCurve.LengthParameter(alongDist, out alongT))
        alongT = topCurve.Domain.Mid;
    }
    else
    {
      if (!topCurve.LengthParameter(Math.Max(0.0, curveLen - alongDist), out alongT))
        alongT = topCurve.Domain.Mid;
    }

    var alongPt = topCurve.PointAt(alongT);

    var downRef = CurveMidpoint(outsideCurve) - alongPt;
    downRef -= tangent * Vector3d.Multiply(downRef, tangent);
    if (downRef.Unitize() && Vector3d.Multiply(yAxis, downRef) < 0.0)
      yAxis = -yAxis;

    var topLeftAnchor = alongPt + yAxis * Math.Abs(LabelOffsetPerp);

    var textYAxis = -yAxis;
    if (!textYAxis.Unitize())
      textYAxis = Vector3d.YAxis;

    var plane = new Plane(topLeftAnchor, tangent, textYAxis);
    if (Vector3d.Multiply(plane.ZAxis, Vector3d.ZAxis) < 0.0)
      plane = new Plane(topLeftAnchor, -plane.XAxis, plane.YAxis);

    var te = new TextEntity
    {
      Plane = plane,
      PlainText = text,
      TextHeight = 0.5,
      Justification = TextJustification.TopLeft
    };

    var id = AddText(doc, te, LayerPlot);
    if (id != Guid.Empty)
    {
      LockTextTopLeftToPoint(doc, id, topLeftAnchor);
      ids.Add(id);
    }

    return ids;
  }

  private static Guid? AddFaceDownText(RhinoDoc doc, Curve? outerCurve, Curve? insideCurve, string note)
  {
    if (outerCurve == null || insideCurve == null)
      return null;

    var text = (note ?? "").Trim();
    if (text.Length == 0)
      return null;

    var len = outerCurve.GetLength();
    if (len <= RhinoMath.ZeroTolerance)
      return null;

    var t = outerCurve.LengthParameter(0.5 * len, out var tm) ? tm : outerCurve.Domain.Mid;
    var pMid = outerCurve.PointAt(t);

    if (!insideCurve.ClosestPoint(pMid, out var ti))
      return null;
    var pIn = insideCurve.PointAt(ti);

    var tangent = outerCurve.TangentAt(t);
    if (!tangent.Unitize())
      return null;

    var yAxis = pIn - pMid;
    yAxis -= tangent * Vector3d.Multiply(yAxis, tangent);
    if (!yAxis.Unitize())
      return null;

    var center = pMid + yAxis * 1.5;
    var plane = new Plane(center, tangent, yAxis);

    var te = new TextEntity
    {
      Plane = plane,
      PlainText = text,
      TextHeight = 3.0,
      Justification = TextJustification.MiddleCenter
    };

    var id = AddText(doc, te, LayerReference);
    return id == Guid.Empty ? null : id;
  }

  private static void TransformIdsInPlace(RhinoDoc doc, List<Guid> ids, Transform xform)
  {
    for (var i = 0; i < ids.Count; i++)
    {
      var id = ids[i];
      var newId = doc.Objects.Transform(id, xform, true);
      if (newId != Guid.Empty)
        ids[i] = newId;
    }
  }

  private static void MirrorPartAboutCenterMid(RhinoDoc doc, List<Guid> objectIds, Curve centerCurve)
  {
    var len = centerCurve.GetLength();
    var tm = centerCurve.LengthParameter(0.5 * len, out var t) ? t : centerCurve.Domain.Mid;

    var midPt = centerCurve.PointAt(tm);
    if (!centerCurve.PerpendicularFrameAt(tm, out var frame))
      return;

    var tangent = frame.ZAxis;
    if (!tangent.Unitize())
      return;

    var mirrorPlane = new Plane(midPt, tangent);
    TransformIdsInPlace(doc, objectIds, Transform.Mirror(mirrorPlane));
  }

  private static string? AddPartGroup(RhinoDoc doc, string groupName, IEnumerable<Guid> objectIds)
  {
    var ids = objectIds.Where(id => id != Guid.Empty).Distinct().ToList();
    if (ids.Count == 0)
      return null;

    var finalName = groupName;
    var suffix = 2;
    while (doc.Groups.FindName(finalName) != null)
    {
      finalName = $"{groupName}_{suffix}";
      suffix++;
    }

    var groupIndex = doc.Groups.Add(finalName);
    if (groupIndex < 0)
      return null;

    foreach (var id in ids)
    {
      var obj = doc.Objects.FindId(id);
      if (obj == null)
        continue;
      var attr = obj.Attributes.Duplicate();
      attr.AddToGroup(groupIndex);
      doc.Objects.ModifyAttributes(id, attr, false);
    }

    return finalName;
  }

  private static (List<Guid> PartObjects, string? GroupName) MakePart(
    RhinoDoc doc,
    int partIndex,
    string buildStamp,
    string label,
    CurveItem centerItem,
    List<CurveItem> preselectedItems,
    HashSet<Guid> touchingIds,
    Curve centerCurve,
    Plane plane,
    int insideSign,
    List<Curve> endCurves,
    PartSpec spec)
  {
    var logPfx = $"[MakePart p{partIndex}/{spec.Name}]";
    vToolsPlugIn.TryLog($"{logPfx} START endCurves={endCurves.Count} preselected={preselectedItems.Count} insideSign={insideSign}");
    for (var ei = 0; ei < endCurves.Count; ei++)
    {
      var ec = endCurves[ei];
      vToolsPlugIn.TryLog($"{logPfx}   endCurve[{ei}] len={ec.GetLength():F4} start={ec.PointAtStart:F4} end={ec.PointAtEnd:F4}");
    }
    for (var si = 0; si < preselectedItems.Count; si++)
    {
      var item = preselectedItems[si];
      vToolsPlugIn.TryLog($"{logPfx}   preselected[{si}] layer={item.LayerName} len={item.Curve.GetLength():F4} hasId={item.ObjectId.HasValue} touching={item.ObjectId.HasValue && touchingIds.Contains(item.ObjectId!.Value)}");
    }

    var centerMid = CurveMidpoint(centerCurve);
    var partObjects = new List<Guid>();

    var selected = new List<CurveItem>(preselectedItems);
    var endItemIds = new HashSet<Guid>();

    // Widen end cap curves so that wider offset curves (e.g. +1.125 outer) can reach
    // and intersect them for trimming. End caps are NOT added to `selected` because
    // CurveInsideOuterBoundary uses 3D distance to the nearest center endpoint; for
    // caps past the arm tip the tail component inflates that distance above the outer
    // band limit, causing the cap to be falsely classified as outside and dropped.
    // Instead we trim and add caps directly after the band-filter loop.
    var maxAbsOffset = spec.Offsets.Count > 0 ? spec.Offsets.Max(o => Math.Abs(o.Offset)) : 0.0;
    var addedEndCurves = new List<Curve>();
    var deduplicatedEndCurves = new List<Curve>();
    foreach (var end in endCurves)
    {
      if (deduplicatedEndCurves.Any(existing => CurvesNearlySame(doc, existing, end)))
        continue;
      deduplicatedEndCurves.Add(end);
      var widenedEnd = maxAbsOffset > doc.ModelAbsoluteTolerance
        ? (end.Extend(CurveEnd.Both, maxAbsOffset, CurveExtensionStyle.Line) ?? end)
        : end;
      addedEndCurves.Add(widenedEnd);
    }
    vToolsPlugIn.TryLog($"{logPfx} dedup={deduplicatedEndCurves.Count} maxAbsOffset={maxAbsOffset:F4}");
    for (var wi = 0; wi < addedEndCurves.Count; wi++)
    {
      var wc = addedEndCurves[wi];
      vToolsPlugIn.TryLog($"{logPfx}   widenedCap[{wi}] len={wc.GetLength():F4} start={wc.PointAtStart:F4} end={wc.PointAtEnd:F4}");
    }

    var centerLayer = string.IsNullOrWhiteSpace(spec.CenterLayer) ? centerItem.LayerName : spec.CenterLayer!;
    Curve centerForPart;
    if (spec.CenterEndMode == "extend_trim")
      centerForPart = ExtendCurveToEnd(doc, centerCurve, endCurves, centerMid);
    else if (spec.CenterEndMode == "trim")
      centerForPart = TrimToMiddleBetweenEndCurves(doc, centerCurve, endCurves, centerMid);
    else
      centerForPart = centerCurve.DuplicateCurve();

    var centerId = AddCurve(doc, centerForPart, centerLayer);
    if (centerId != Guid.Empty)
      partObjects.Add(centerId);

    var offsets = new Dictionary<string, Curve>(StringComparer.OrdinalIgnoreCase);
    foreach (var off in spec.Offsets)
    {
      var side = off.Offset >= 0.0 ? "outside" : "inside";
      var distance = Math.Abs(off.Offset);
      var offsetCurve = OffsetAtSide(doc, centerCurve, plane, insideSign, distance, side);
      if (offsetCurve == null)
        continue;
      offsetCurve = ExtendCurveToEnd(doc, offsetCurve, addedEndCurves, centerMid);
      offsets[off.Name] = offsetCurve;
    }

    foreach (var off in spec.Offsets)
    {
      if (!offsets.TryGetValue(off.Name, out var curve))
        continue;
      var targetLayer = string.IsNullOrWhiteSpace(off.Layer) ? centerItem.LayerName : off.Layer!;
      var id = AddCurve(doc, curve, targetLayer);
      if (id != Guid.Empty)
        partObjects.Add(id);
    }

    offsets.TryGetValue(spec.BandInsideName, out var insideBoundary);
    offsets.TryGetValue(spec.BandOutsideName, out var outsideBoundary);

    vToolsPlugIn.TryLog($"{logPfx} bandInside='{spec.BandInsideName}'({(insideBoundary != null ? $"len={insideBoundary.GetLength():F4}" : "NULL")}) bandOutside='{spec.BandOutsideName}'({(outsideBoundary != null ? $"len={outsideBoundary.GetLength():F4}" : "NULL")})");

    if (insideBoundary != null && outsideBoundary != null)
    {
      foreach (var item in selected)
      {
        var source = item.Curve.DuplicateCurve();
        if (source == null)
          continue;

        var useEndpointSideKeep = !item.ObjectId.HasValue || !touchingIds.Contains(item.ObjectId.Value);
        var innerLogKey = $"{logPfx} [{item.LayerName} len={item.Curve.GetLength():F4}]";
        var pieces = SplitAndFilterForBand(doc, source, insideBoundary, outsideBoundary, centerCurve, useEndpointSideKeep, innerLogKey);
        vToolsPlugIn.TryLog($"{logPfx}   selected layer={item.LayerName} len={item.Curve.GetLength():F4} useEPKeep={useEndpointSideKeep} → pieces={pieces.Count}");
        if (pieces.Count == 0)
          continue;

        foreach (var piece in pieces)
        {
          var id = AddCurve(doc, piece, item.LayerName);
          if (id == Guid.Empty)
            continue;

          partObjects.Add(id);
          if (!item.ObjectId.HasValue || touchingIds.Contains(item.ObjectId.Value))
          {
            if (item.LayerName == LayerCut)
              endItemIds.Add(id);
          }
        }
      }
    }
    else
    {
      foreach (var item in preselectedItems)
      {
        var id = AddCurve(doc, item.Curve.DuplicateCurve(), item.LayerName);
        if (id != Guid.Empty)
          partObjects.Add(id);
      }
    }

    // Add end cap curves directly, trimmed to fit the band.  We skip SplitAndFilterForBand
    // for caps (see comment above near deduplicatedEndCurves).
    // The inner/outer boundaries were extended to meet the WIDENED caps (addedEndCurves),
    // so we trim the widened cap using those intersections, not the original narrow cap.
    for (var ci = 0; ci < addedEndCurves.Count; ci++)
    {
      var widenedCap = addedEndCurves[ci];
      var capPieces = TrimCapToBandBoundaries(doc, widenedCap, insideBoundary, outsideBoundary, logPfx, ci);
      foreach (var capPiece in capPieces)
      {
        var capId = AddCurve(doc, capPiece, LayerCut);
        if (capId != Guid.Empty)
        {
          partObjects.Add(capId);
          endItemIds.Add(capId);
        }
      }
    }

    if (endItemIds.Count == 0)
    {
      foreach (var id in partObjects)
      {
        var obj = doc.Objects.FindId(id);
        if (obj == null)
          continue;
        var layerName = doc.Layers[obj.Attributes.LayerIndex]?.FullPath;
        if (string.Equals(layerName, LayerCut, StringComparison.OrdinalIgnoreCase))
          endItemIds.Add(id);
      }
    }

    partObjects.AddRange(AddPartLabel(doc, label, endItemIds, outsideBoundary, insideBoundary, centerForPart));

    var faceDown = AddFaceDownText(doc, outsideBoundary, insideBoundary, spec.Note);
    if (faceDown.HasValue)
      partObjects.Add(faceDown.Value);

    if (spec.MirrorPart)
    {
      MirrorPartAboutCenterMid(doc, partObjects, centerForPart);
      MirrorTextAboutOwnVerticalAxis(doc, partObjects);
    }

    var partToken = string.IsNullOrWhiteSpace(label)
      ? spec.Name.Replace(' ', '_')
      : $"{label.Trim().Replace(' ', '_')}_{spec.Name.Replace(' ', '_')}";

    var group = AddPartGroup(doc, $"U-Zip_{buildStamp}_{partToken}", partObjects);
    return (partObjects, group);
  }

  private static (double MinY, double MaxY)? BBoxYRange(RhinoDoc doc, IEnumerable<Guid> objectIds)
  {
    var ids = objectIds.Where(id => id != Guid.Empty).ToList();
    if (ids.Count == 0)
      return null;

    BoundingBox? bbox = null;
    foreach (var id in ids)
    {
      var obj = doc.Objects.FindId(id);
      if (obj?.Geometry == null)
        continue;
      var gb = obj.Geometry.GetBoundingBox(true);
      if (!gb.IsValid)
        continue;
      bbox = bbox.HasValue ? BoundingBox.Union(bbox.Value, gb) : gb;
    }

    if (!bbox.HasValue || !bbox.Value.IsValid)
      return null;

    return (bbox.Value.Min.Y, bbox.Value.Max.Y);
  }

  private static void StackPartsDown(RhinoDoc doc, List<List<Guid>> partObjectLists, double gap)
  {
    double? currentBottom = null;

    for (var i = 0; i < partObjectLists.Count; i++)
    {
      var ids = partObjectLists[i].Where(id => doc.Objects.FindId(id) != null).ToList();
      if (ids.Count == 0)
        continue;

      var range = BBoxYRange(doc, ids);
      if (!range.HasValue)
        continue;

      var minY = range.Value.MinY;
      var maxY = range.Value.MaxY;

      if (i == 0)
      {
        currentBottom = minY;
        continue;
      }

      if (!currentBottom.HasValue)
      {
        currentBottom = minY;
        continue;
      }

      var targetTop = currentBottom.Value - gap;
      var deltaY = targetTop - maxY;
      if (Math.Abs(deltaY) > doc.ModelAbsoluteTolerance)
      {
        var xform = Transform.Translation(0.0, deltaY, 0.0);
        for (var k = 0; k < ids.Count; k++)
        {
          var oldId = ids[k];
          var newId = doc.Objects.Transform(oldId, xform, true);
          if (newId != Guid.Empty)
          {
            ids[k] = newId;
            var originalIndex = partObjectLists[i].FindIndex(id => id == oldId);
            if (originalIndex >= 0)
              partObjectLists[i][originalIndex] = newId;
          }
        }

        var moved = BBoxYRange(doc, ids);
        currentBottom = moved?.MinY ?? (minY + deltaY);
      }
      else
      {
        currentBottom = minY;
      }
    }
  }

  private static void PlaceGroupsWithPickOrDelete(RhinoDoc doc, List<string> groupNames, string? anchorGroupName)
  {
    var selectedIds = new HashSet<Guid>();
    foreach (var name in groupNames)
    {
      var group = doc.Groups.FindName(name);
      if (group == null)
        continue;

      foreach (var member in doc.Groups.GroupMembers(group.Index))
      {
        if (member != null && doc.Objects.FindId(member.Id) != null)
          selectedIds.Add(member.Id);
      }
    }

    if (selectedIds.Count == 0)
      return;

    var bbox = BoundingBox.Empty;
    foreach (var id in selectedIds)
    {
      var obj = doc.Objects.FindId(id);
      if (obj?.Geometry == null)
        continue;
      var gb = obj.Geometry.GetBoundingBox(true);
      if (!gb.IsValid)
        continue;
      bbox = bbox.IsValid ? BoundingBox.Union(bbox, gb) : gb;
    }

    if (!bbox.IsValid)
      return;

    var basePoint = PartLeftEndOuterAnchor(doc, anchorGroupName)
      ?? new Point3d(bbox.Min.X, bbox.Max.Y, 0.5 * (bbox.Min.Z + bbox.Max.Z));

    var previewItems = new List<(GeometryBase Geom, System.Drawing.Color Color)>();
    foreach (var id in selectedIds)
    {
      var obj = doc.Objects.FindId(id);
      if (obj?.Geometry == null)
        continue;

      var color = System.Drawing.Color.Cyan;
      var layer = doc.Layers[obj.Attributes.LayerIndex];
      if (layer != null)
        color = layer.Color;

      var dup = obj.Geometry.Duplicate();
      if (dup != null)
        previewItems.Add((dup, color));
    }

    foreach (var id in selectedIds)
      doc.Objects.Hide(id, true);
    doc.Views.Redraw();

    var gp = new GetPoint();
    gp.SetCommandPrompt("Pick placement point for created parts (Esc to cancel and delete)");
    EventHandler<GetPointDrawEventArgs> handler = (_, e) =>
    {
      var moveVec = e.CurrentPoint - basePoint;
      var xform = Transform.Translation(moveVec);

      foreach (var (geom, color) in previewItems)
      {
        var draw = geom.Duplicate();
        if (draw == null)
          continue;

        draw.Transform(xform);
        switch (draw)
        {
          case Curve c:
            e.Display.DrawCurve(c, color, 1);
            break;
          case Brep b:
            e.Display.DrawBrepWires(b, color, 1);
            break;
          case Mesh m:
            e.Display.DrawMeshWires(m, color);
            break;
          case TextEntity te:
            e.Display.DrawAnnotation(te, color);
            break;
          case Point p:
            e.Display.DrawPoint(p.Location, Rhino.Display.PointStyle.Simple, 2, color);
            break;
        }
      }
    };
    gp.DynamicDraw += handler;

    var result = gp.Get();
    gp.DynamicDraw -= handler;

    if (result != GetResult.Point)
    {
      DeleteCreatedGroupsAndMembers(doc, groupNames);
      doc.Views.Redraw();
      return;
    }

    var target = gp.Point();
    var move = target - basePoint;
    if (move.Length > doc.ModelAbsoluteTolerance)
    {
      var xform = Transform.Translation(move);
      foreach (var id in selectedIds.ToList())
      {
        var newId = doc.Objects.Transform(id, xform, true);
        if (newId != Guid.Empty)
        {
          selectedIds.Remove(id);
          selectedIds.Add(newId);
        }
      }
    }

    foreach (var id in selectedIds)
      doc.Objects.Show(id, true);

    doc.Objects.UnselectAll();
    foreach (var id in selectedIds)
      doc.Objects.Select(id);
    doc.Views.Redraw();
  }

  private static void DeleteCreatedGroupsAndMembers(RhinoDoc doc, IEnumerable<string> groupNames)
  {
    var ids = new HashSet<Guid>();

    foreach (var name in groupNames)
    {
      if (string.IsNullOrWhiteSpace(name))
        continue;

      var group = doc.Groups.FindName(name);
      if (group == null)
        continue;

      var members = doc.Groups.GroupMembers(group.Index);
      if (members != null)
      {
        foreach (var member in members)
        {
          if (member != null)
            ids.Add(member.Id);
        }
      }
    }

    foreach (var id in ids)
      doc.Objects.Delete(id, true);

    foreach (var name in groupNames)
    {
      if (string.IsNullOrWhiteSpace(name))
        continue;
      var group = doc.Groups.FindName(name);
      if (group != null)
        doc.Groups.Delete(group.Index);
    }
  }

  private static Point3d? PartLeftEndOuterAnchor(RhinoDoc doc, string? groupName)
  {
    if (string.IsNullOrWhiteSpace(groupName))
      return null;

    var group = doc.Groups.FindName(groupName);
    if (group == null)
      return null;

    var cutCurves = new List<Curve>();
    foreach (var member in doc.Groups.GroupMembers(group.Index))
    {
      if (member == null)
        continue;

      var obj = doc.Objects.FindId(member.Id);
      if (obj == null)
        continue;

      var layerName = doc.Layers[obj.Attributes.LayerIndex]?.FullPath;
      if (!string.Equals(layerName, LayerCut, StringComparison.OrdinalIgnoreCase))
        continue;

      if (obj.Geometry is Curve c)
        cutCurves.Add(c);
    }

    if (cutCurves.Count < 2)
      return null;

    var outer = cutCurves.OrderByDescending(c => c.GetLength()).FirstOrDefault();
    if (outer == null)
      return null;

    var p0 = outer.PointAtStart;
    var p1 = outer.PointAtEnd;
    return p0.X <= p1.X ? p0 : p1;
  }

  private static void MirrorTextAboutOwnVerticalAxis(RhinoDoc doc, List<Guid> objectIds)
  {
    for (var i = 0; i < objectIds.Count; i++)
    {
      var id = objectIds[i];
      var obj = doc.Objects.FindId(id);
      if (obj?.Geometry is not TextEntity te)
        continue;

      var plane = te.Plane;
      var lb = te.GetBoundingBox(plane);
      if (!lb.IsValid)
        continue;

      var center = plane.PointAt((lb.Min.X + lb.Max.X) * 0.5, (lb.Min.Y + lb.Max.Y) * 0.5);
      var axisDir = plane.YAxis;
      if (!axisDir.Unitize())
        continue;

      var normal = Vector3d.CrossProduct(axisDir, plane.ZAxis);
      if (!normal.Unitize())
        continue;

      var mirrorPlane = new Plane(center, normal);
      var xform = Transform.Mirror(mirrorPlane);
      var newId = doc.Objects.Transform(id, xform, true);
      if (newId != Guid.Empty)
        objectIds[i] = newId;
    }
  }

  private static List<PartSpec> ResolvePartSpecs(string centerItemLayer, List<PartSpec>? configuredParts, LayerRuntime layerRuntime)
  {
    var specs = ClonePartSpecs(configuredParts);
    if (specs.Count == 0)
      specs = CreateDefaultPartSpecs(layerRuntime);

    specs = specs.Where(spec => spec != null && spec.Offsets != null && spec.Offsets.Count > 0).ToList();

    foreach (var spec in specs)
    {
      spec.Name = string.IsNullOrWhiteSpace(spec.Name) ? "part" : spec.Name.Trim();
      spec.Note = spec.Note ?? "";
      spec.CenterLayer = string.IsNullOrWhiteSpace(spec.CenterLayer) ? centerItemLayer : spec.CenterLayer!.Trim();
      spec.CenterEndMode = (spec.CenterEndMode ?? "").Trim().ToLowerInvariant();
      spec.CenterEndMode = spec.CenterEndMode is "trim" or "extend_trim" ? spec.CenterEndMode : "";

      var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      foreach (var offset in spec.Offsets)
      {
        var name = string.IsNullOrWhiteSpace(offset.Name) ? AutoOffsetName(offset.Offset) : offset.Name.Trim();
        var baseName = name;
        var suffix = 2;
        while (!used.Add(name))
        {
          name = $"{baseName}_{suffix}";
          suffix++;
        }

        offset.Name = name;
      }

      var inside = spec.Offsets.Where(o => o.Offset < 0.0).OrderBy(o => o.Offset).FirstOrDefault();
      var outside = spec.Offsets.Where(o => o.Offset > 0.0).OrderByDescending(o => o.Offset).FirstOrDefault();

      if (inside == null)
        inside = spec.Offsets.OrderBy(o => o.Offset).First();
      if (outside == null)
        outside = spec.Offsets.OrderBy(o => o.Offset).Last();

      spec.BandInsideName = inside.Name;
      spec.BandOutsideName = outside.Name;

      foreach (var offset in spec.Offsets)
      {
        if (!string.IsNullOrWhiteSpace(offset.Layer))
        {
          offset.Layer = offset.Layer.Trim();
          continue;
        }

        offset.Layer = (offset.Name == spec.BandInsideName || offset.Name == spec.BandOutsideName)
          ? LayerCut
          : spec.CenterLayer;
      }
    }

    return specs;
  }

  private static List<PartSpec> ClonePartSpecs(List<PartSpec>? source)
  {
    if (source == null || source.Count == 0)
      return new List<PartSpec>();

    try
    {
      var json = JsonSerializer.Serialize(source, JsonOptions);
      var cloned = JsonSerializer.Deserialize<List<PartSpec>>(json, JsonOptions);
      return cloned ?? new List<PartSpec>();
    }
    catch
    {
      return new List<PartSpec>();
    }
  }

  private static List<PartSpec> CreateDefaultPartSpecs(LayerRuntime layerRuntime)
  {
    return new List<PartSpec>
    {
      new()
      {
        Name = "zipper",
        CenterLayer = layerRuntime.PlotName,
        CenterEndMode = "extend_trim",
        Note = "FACE DOWN",
        Offsets =
        {
          new OffsetSpec { Offset = -0.75 },
          new OffsetSpec { Offset = 0.75 }
        }
      },
      new()
      {
        Name = "inner",
        CenterLayer = layerRuntime.ReferenceName,
        CenterEndMode = "trim",
        Offsets =
        {
          new OffsetSpec { Offset = -0.75 },
          new OffsetSpec { Offset = 1.125 }
        }
      },
      new()
      {
        Name = "outer",
        CenterLayer = layerRuntime.ReferenceName,
        CenterEndMode = "trim",
        MirrorPart = true,
        Offsets =
        {
          new OffsetSpec { Offset = -0.75, Layer = layerRuntime.PlotName },
          new OffsetSpec { Offset = -1.5 },
          new OffsetSpec { Offset = 1.125 }
        }
      }
    };
  }

  private static string AutoOffsetName(double signedOffset)
  {
    var side = signedOffset > 0.0 ? "outside" : signedOffset < 0.0 ? "inside" : "center";
    var magText = Math.Abs(signedOffset).ToString("0.####", CultureInfo.InvariantCulture).Replace('.', 'p');
    if (string.IsNullOrWhiteSpace(magText))
      magText = "0";
    return $"{side}_{magText}";
  }

  /// <summary>
  /// Trims a cap curve (typically the widened end cap) to span only between the inner and outer
  /// band boundary curves.  Both boundaries were extended to meet this widened cap via
  /// ExtendCurveToEnd, so both will intersect it.  Returns two pieces — one per arm end:
  ///   left:  [outer_lo, inner_lo]   right: [inner_hi, outer_hi]
  /// This removes the inner section (between the inner rails) from the cap.
  /// Falls back to a single piece when only one boundary provides params.
  /// </summary>
  private static List<Curve> TrimCapToBandBoundaries(RhinoDoc doc, Curve cap, Curve? innerBoundary, Curve? outerBoundary, string logPfx, int capIndex)
  {
    var tol = doc.ModelAbsoluteTolerance;
    var innerParams = innerBoundary != null ? UniqueParams(IntersectionParams(doc, cap, innerBoundary), tol) : new List<double>();
    var outerParams = outerBoundary != null ? UniqueParams(IntersectionParams(doc, cap, outerBoundary), tol) : new List<double>();
    vToolsPlugIn.TryLog($"{logPfx}   cap[{capIndex}] len={cap.GetLength():F4} start={cap.PointAtStart:F4} end={cap.PointAtEnd:F4}");
    vToolsPlugIn.TryLog($"{logPfx}     innerBoundary={(innerBoundary != null ? $"len={innerBoundary.GetLength():F4} params=[{string.Join(",", innerParams.Select(p => $"{p:F6}"))}]" : "NULL")}");
    vToolsPlugIn.TryLog($"{logPfx}     outerBoundary={(outerBoundary != null ? $"len={outerBoundary.GetLength():F4} params=[{string.Join(",", outerParams.Select(p => $"{p:F6}"))}]" : "NULL")}");

    // Two pieces per cap: [outer_lo, inner_lo] and [inner_hi, outer_hi].
    // Requires at least 2 inner params AND 2 outer params.
    if (innerParams.Count >= 2 && outerParams.Count >= 2)
    {
      var outerLo = outerParams.Min();
      var outerHi = outerParams.Max();
      var innerLo = innerParams.Min();
      var innerHi = innerParams.Max();
      vToolsPlugIn.TryLog($"{logPfx}     → two pieces: [{outerLo:F6},{innerLo:F6}] [{innerHi:F6},{outerHi:F6}] domain={cap.Domain.T0:F6}..{cap.Domain.T1:F6}");
      var result = new List<Curve>();
      var left = cap.Trim(new Interval(outerLo, innerLo));
      if (left != null && left.GetLength() > tol) result.Add(left);
      var right = cap.Trim(new Interval(innerHi, outerHi));
      if (right != null && right.GetLength() > tol) result.Add(right);
      vToolsPlugIn.TryLog($"{logPfx}     → pieces={result.Count} lens=[{string.Join(",", result.Select(c => $"{c.GetLength():F4}"))}]");
      if (result.Count > 0) return result;
    }

    // Fallback: single piece spanning all available params.
    var allParams = new List<double>(innerParams);
    allParams.AddRange(outerParams);
    var ps = UniqueParams(allParams, tol);
    vToolsPlugIn.TryLog($"{logPfx}     combined uniqueParams={ps.Count} [{string.Join(",", ps.Select(p => $"{p:F6}"))}]");
    if (ps.Count < 2)
    {
      vToolsPlugIn.TryLog($"{logPfx}     → <2 params, returning cap unchanged");
      return new List<Curve> { cap.DuplicateCurve() };
    }
    var lo = ps.Min();
    var hi = ps.Max();
    vToolsPlugIn.TryLog($"{logPfx}     → fallback trim [{lo:F6}, {hi:F6}] domain={cap.Domain.T0:F6}..{cap.Domain.T1:F6}");
    var trimmed = cap.Trim(new Interval(lo, hi));
    vToolsPlugIn.TryLog($"{logPfx}     → trimmed len={(trimmed != null ? $"{trimmed.GetLength():F4}" : "NULL")}");
    return new List<Curve> { trimmed ?? cap.DuplicateCurve() };
  }
}
