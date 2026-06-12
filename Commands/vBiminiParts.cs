using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;
using Rhino.Input;
using Rhino.Input.Custom;

namespace vTools.Commands;

/// <summary>
/// Generates bimini pattern parts (Finished/Seam boundary, FacingP/FacingS, main/secondary pockets)
/// from a selected closed boundary curve (or a set of open curves that form one).
///
/// Workflow:
///   Stage 1 — select boundary; detect or create Finished (PLOT) and Seam (CUT1) curves;
///             break both at corners → 4 segments each.
///   Stage 2 — pick 1–2 Main pocket curve(s).
///   Stage 3 — pick 0–2 Secondary pocket curve(s) (skipped if 2 Main selected).
///   Stage 4 — create FacingP / FacingS from side seam segments offset 3" inward.
///   Stage 5 — create Main pocket: zipper offset, side offsets, perpendicular lines.
/// </summary>
public sealed class vBiminiParts : Command
{
  // ── Config POCO types ────────────────────────────────────────────────────────

  private sealed class BiminiLayerEntry
  {
    public string? Name  { get; set; }
    public string? Color { get; set; }
  }

  private sealed class BiminiLayersConfig
  {
    public BiminiLayerEntry? Plot      { get; set; }
    public BiminiLayerEntry? Cut1      { get; set; }
    public BiminiLayerEntry? Reference { get; set; }
  }

  private sealed class ExtraRectConfig
  {
    public double Height      { get; set; } = 1.5;
    public double LengthExtra { get; set; } = 1.0;
  }

  private sealed class PipeSizeConfig
  {
    public double  Size         { get; set; } = 1.0;
    public string  Label        { get; set; } = "1";
    public double  MainPktDepth { get; set; } = 4.0;
    public double  SecPktDepth  { get; set; } = 4.5;
    public ExtraRectConfig? ExtraRect { get; set; }
  }

  private sealed class BiminiConfigSection
  {
    public BiminiLayersConfig   Layers          { get; set; } = new();
    public double SeamAllowance    { get; set; } = 0.5;
    public double FacingInset      { get; set; } = 3.0;
    public double SidePktOutward   { get; set; } = 2.5;
    public double CornerAngleDeg   { get; set; } = 30.0;
    public double FacingMoveOut    { get; set; } = 4.0;
    public double PktSeamClearance { get; set; } = 4.0;
    public List<PipeSizeConfig> PipeSizes { get; set; } = new();
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
  }

  private sealed class BiminiToolsConfigRoot
  {
    public BiminiConfigSection? VBiminiParts { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalSections { get; set; }
  }

  // ── Runtime fields (populated from config each run) ─────────────────────────

  private const string SectionName         = "vBiminiParts";
  private const string PipeSizeKey         = "pipeSizeIdx";
  private const string ToolsConfigFileName = "vTools.config.json";

  private static string _layerPlot      = "PLOT";
  private static string _layerCut1      = "CUT1";
  private static string _layerRef       = "Reference";
  private static Color  _layerPlotColor = Color.FromArgb(15, 138, 138);
  private static Color  _layerCut1Color = Color.FromArgb(204, 51, 51);
  private static Color  _layerRefColor  = Color.FromArgb(255, 255, 255);

  private static double _seamAllowance    = 0.5;
  private static double _facingInset      = 3.0;
  private static double _sidePktOutward   = 2.5;
  private static double _cornerAngleDeg   = 30.0;
  private static double _facingMoveOut    = 4.0;
  private static double _pktSeamClearance = 4.0;
  private static double _mainPktDepth     = 4.0;
  private static double _secPktDepth      = 4.5;
  private static ExtraRectConfig? _extraRect = null;

  // ── Persisted state ─────────────────────────────────────────────────────────

  private static int _pipeSizeIdx = 1;  // index into BiminiConfigSection.PipeSizes

  // ── Debug logging ──────────────────────────────────────────────────────────

  private static StreamWriter? _log;

  private static string GetLogPath()
  {
    var dir = Path.Combine(Path.GetDirectoryName(typeof(vBiminiParts).Assembly.Location)!, "logs");
    Directory.CreateDirectory(dir);
    return Path.Combine(dir, "vBiminiParts.log");
  }

  internal static void InitLog()
  {
    try { File.WriteAllText(GetLogPath(), string.Empty); } catch { }
  }

  private static void L(string s) { _log?.WriteLine(s); }

  public override string EnglishName => "vBiminiParts";

  // ── Persistence ─────────────────────────────────────────────────────────────

  private static void LoadOptions()
  {
    ToolsOptionStore.Read<int>(SectionName, section =>
    {
      if (ToolsOptionStore.TryGetDouble(section, PipeSizeKey, out var ps))
        _pipeSizeIdx = (int)Math.Round(ps);
      return 0;
    });
  }

  private static void SaveOptions() =>
    ToolsOptionStore.Update(SectionName, section => { section[PipeSizeKey] = (double)_pipeSizeIdx; });

  // ── Config file helpers ──────────────────────────────────────────────────────

  private static readonly JsonSerializerOptions _jsonOpts = new()
  {
    WriteIndented            = true,
    PropertyNamingPolicy     = JsonNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling      = JsonCommentHandling.Skip,
    AllowTrailingCommas      = true,
  };

  private static string GetConfigPath()
  {
    var dir = Path.GetDirectoryName(typeof(vBiminiParts).Assembly.Location) ?? ".";
    return Path.Combine(dir, ToolsConfigFileName);
  }

  private static BiminiToolsConfigRoot LoadConfig(string path)
  {
    try
    {
      if (File.Exists(path))
      {
        var json = File.ReadAllText(path);
        if (!string.IsNullOrWhiteSpace(json))
        {
          var loaded = JsonSerializer.Deserialize<BiminiToolsConfigRoot>(json, _jsonOpts);
          if (loaded != null) return loaded;
        }
      }
    }
    catch { }
    return new BiminiToolsConfigRoot();
  }

  private static void SaveConfig(string path, BiminiToolsConfigRoot root)
  {
    try
    {
      var json = JsonSerializer.Serialize(root, _jsonOpts);
      var tmp  = path + ".tmp";
      File.WriteAllText(tmp, json);
      File.Copy(tmp, path, overwrite: true);
      File.Delete(tmp);
    }
    catch { }
  }

  private static List<PipeSizeConfig> DefaultPipeSizes() => new()
  {
    new() { Size = 0.875, Label = "7/8",   MainPktDepth = 4.0, SecPktDepth = 4.5 },
    new() { Size = 1.0,   Label = "1",     MainPktDepth = 4.0, SecPktDepth = 4.5 },
    new() { Size = 1.25,  Label = "1-1/4", MainPktDepth = 5.0, SecPktDepth = 5.5 },
    new() { Size = 1.5,   Label = "1-1/2", MainPktDepth = 5.0, SecPktDepth = 5.5,
            ExtraRect = new() { Height = 1.5, LengthExtra = 1.0 } },
  };

  private static BiminiConfigSection EnsureSection(BiminiToolsConfigRoot root)
  {
    root.VBiminiParts ??= new BiminiConfigSection();
    var s = root.VBiminiParts!;
    if (s.PipeSizes == null || s.PipeSizes.Count == 0)
      s.PipeSizes = DefaultPipeSizes();
    s.Layers ??= new BiminiLayersConfig();
    s.Layers.Plot      ??= new BiminiLayerEntry { Name = "PLOT",      Color = "#0F8A8A" };
    s.Layers.Cut1      ??= new BiminiLayerEntry { Name = "CUT1",      Color = "#CC3333" };
    s.Layers.Reference ??= new BiminiLayerEntry { Name = "Reference", Color = "#00B43C" };
    return s;
  }

  private static Color ParseHexColor(string? hex, Color fallback)
  {
    if (string.IsNullOrWhiteSpace(hex)) return fallback;
    var h = hex.TrimStart('#');
    if (h.Length == 6 && int.TryParse(h, NumberStyles.HexNumber, null, out var rgb))
      return Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
    return fallback;
  }

  private static void ApplyConfig(BiminiConfigSection s, int idx)
  {
    _layerPlot      = string.IsNullOrWhiteSpace(s.Layers?.Plot?.Name)      ? "PLOT"      : s.Layers!.Plot!.Name!;
    _layerCut1      = string.IsNullOrWhiteSpace(s.Layers?.Cut1?.Name)      ? "CUT1"      : s.Layers!.Cut1!.Name!;
    _layerRef       = string.IsNullOrWhiteSpace(s.Layers?.Reference?.Name) ? "Reference" : s.Layers!.Reference!.Name!;
    _layerPlotColor = ParseHexColor(s.Layers?.Plot?.Color,      Color.FromArgb(15, 138, 138));
    _layerCut1Color = ParseHexColor(s.Layers?.Cut1?.Color,      Color.FromArgb(204, 51, 51));
    _layerRefColor  = ParseHexColor(s.Layers?.Reference?.Color, Color.FromArgb(0, 180, 60));
    _seamAllowance    = s.SeamAllowance;
    _facingInset      = s.FacingInset;
    _sidePktOutward   = s.SidePktOutward;
    _cornerAngleDeg   = s.CornerAngleDeg;
    _facingMoveOut    = s.FacingMoveOut;
    _pktSeamClearance = s.PktSeamClearance;
    var i  = Math.Max(0, Math.Min(idx, s.PipeSizes.Count - 1));
    var ps = s.PipeSizes[i];
    _mainPktDepth = ps.MainPktDepth;
    _secPktDepth  = ps.SecPktDepth;
    _extraRect    = ps.ExtraRect;
  }

  // ── Command ──────────────────────────────────────────────────────────────────

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    LoadOptions();
    var configPath = GetConfigPath();
    var configRoot = LoadConfig(configPath);
    var section    = EnsureSection(configRoot);
    ApplyConfig(section, _pipeSizeIdx);
    var tol = doc.ModelAbsoluteTolerance;

    // ── Stage 1: Select bimini boundary ─────────────────────────────────────

    var go = new GetObject();
    go.SetCommandPrompt("Select bimini boundary curves");
    go.GeometryFilter = ObjectType.Curve;
    go.SubObjectSelect = false;
    go.GroupSelect = true;
    go.EnablePreSelect(true, true);
    go.AcceptNothing(false);

    var pipeSizeLabels = section.PipeSizes
        .Select(p => p.Label.Replace("/", "_").Replace("-", "_")
                            .Replace(".", "_").Replace(" ", "") + "in")
        .ToArray();
    var pipeSizeOptIdx = go.AddOptionList("PipeSize", pipeSizeLabels, _pipeSizeIdx);

    while (true)
    {
      var r = go.GetMultiple(1, 0);
      if (r == GetResult.Option)
      {
        if (go.OptionIndex() == pipeSizeOptIdx)
        {
          var opt = go.Option();
          if (opt != null)
          {
            _pipeSizeIdx = opt.CurrentListOptionIndex;
            ApplyConfig(section, _pipeSizeIdx);
            SaveOptions();
          }
        }
        continue;
      }
      if (r != GetResult.Object) return Result.Cancel;
      break;
    }

    SaveConfig(configPath, configRoot);

    var selIds    = new HashSet<Guid>(Enumerable.Range(0, go.ObjectCount).Select(i => go.Object(i).ObjectId));
    var rawCurves = Enumerable.Range(0, go.ObjectCount)
                              .Select(i => go.Object(i).Curve())
                              .Where(c => c != null)
                              .Cast<Curve>()
                              .ToList();

    if (rawCurves.Count == 0) return Result.Cancel;

    // Join into one closed curve
    Curve boundary;
    if (rawCurves.Count == 1 && rawCurves[0].IsClosed)
    {
      boundary = rawCurves[0].DuplicateCurve();
    }
    else
    {
      var joined = Curve.JoinCurves(rawCurves, tol);
      if (joined == null || joined.Length != 1 || !joined[0].IsClosed)
      {
        RhinoApp.WriteLine("vBiminiParts: selected curves do not form a single closed boundary.");
        return Result.Failure;
      }
      boundary = joined[0];
    }

    // Centroid drives offset direction
    var amp = AreaMassProperties.Compute(boundary);
    if (amp == null)
    {
      RhinoApp.WriteLine("vBiminiParts: cannot compute boundary centroid.");
      return Result.Failure;
    }
    var centroid = amp.Centroid;

    // Ensure layers exist first (needed to filter PLOT layer in existing-curve detection)
    var plotIdx = EnsureLayer(doc, _layerPlot, _layerPlotColor);
    var cut1Idx = EnsureLayer(doc, _layerCut1, _layerCut1Color);
    var refIdx  = EnsureLayer(doc, _layerRef,  _layerRefColor);

    // Determine Finished vs Seam.
    // Existing document curves are read-only inputs: duplicate their geometry in memory only;
    // never delete, replace, join, or relayer the original document objects.
    Curve finishedCrv, seamCrv;
    bool finishedIsExisting;
    bool seamIsExisting;

    var selectedCurveObjects = selIds
      .Select(id => doc.Objects.FindId(id))
      .Where(o => o?.Geometry is Curve)
      .Cast<RhinoObject>()
      .ToList();
    var selectedHasPlotCurve = selectedCurveObjects.Any(o => o.Attributes.LayerIndex == plotIdx);

    var inwardCandidate = OffsetToward(boundary, centroid, _seamAllowance, tol);
    var outwardCandidate = OffsetAway(boundary, centroid, _seamAllowance, tol);

    // If selected boundary is the outside seam, the inside finished curve should be near inwardCandidate.
    var existingFinPieces = inwardCandidate != null
      ? FindNearCurves(doc, inwardCandidate, selIds, tol * 50.0, plotIdx)
      : new List<RhinoObject>();

    // If selected boundary is the inside finished curve, the outside seam should be near outwardCandidate.
    var existingSeamPieces = outwardCandidate != null
      ? FindNearCurves(doc, outwardCandidate, selIds, tol * 50.0, cut1Idx)
      : new List<RhinoObject>();

    if (existingFinPieces.Count > 0)
    {
      // Selected boundary is treated as the existing seam; use duplicated existing finished geometry.
      seamCrv = boundary.DuplicateCurve();
      var joinedFin = DuplicateJoinedCurveFromObjects(existingFinPieces, tol);
      if (joinedFin == null)
      {
        RhinoApp.WriteLine("vBiminiParts: failed to read existing finished curve.");
        return Result.Failure;
      }
      finishedCrv = joinedFin;
      finishedIsExisting = true;
      seamIsExisting = true;
    }
    else if (existingSeamPieces.Count > 0)
    {
      // Selected boundary is treated as the existing finished curve; use duplicated existing seam geometry.
      finishedCrv = boundary.DuplicateCurve();
      var joinedSeam = DuplicateJoinedCurveFromObjects(existingSeamPieces, tol);
      if (joinedSeam == null)
      {
        RhinoApp.WriteLine("vBiminiParts: failed to read existing seam curve.");
        return Result.Failure;
      }
      seamCrv = joinedSeam;
      finishedIsExisting = true;
      seamIsExisting = true;
    }
    else
    {
      // No matching seam found. Keep the selected boundary untouched; generate a new seam.
      finishedCrv = boundary.DuplicateCurve();
      seamCrv     = outwardCandidate ?? boundary.DuplicateCurve();
      finishedIsExisting = selectedHasPlotCurve;
      seamIsExisting = false;
    }
    // Break both curves at corners → 4 open segments each
    var finSegs  = BreakAtCorners(finishedCrv, _cornerAngleDeg);
    var seamSegs = BreakAtCorners(seamCrv,     _cornerAngleDeg);

    var plotAttr   = MakeAttr(plotIdx);
    var cut1Attr   = MakeAttr(cut1Idx);
    var finIds     = new HashSet<Guid>();
    var seamIds    = new HashSet<Guid>();
    var finDocIds  = new List<Guid>();          // parallel to finSegs  — for picker
    var seamDocIds = new List<Guid>();          // parallel to seamSegs — for picker

    // Exclude selected inputs and existing boundary pieces from reuse/interior checks.
    var toExclude = new HashSet<Guid>(selIds);
    foreach (var o in existingFinPieces) toExclude.Add(o.Id);
    foreach (var o in existingSeamPieces) toExclude.Add(o.Id);

    // Add broken fin/seam segments as temporary objects for per-segment picking feedback
    // and interior collection. They are deleted after all generated parts are built.
    var finTempIds  = new List<Guid>();
    var seamTempIds = new List<Guid>();
    foreach (var s in finSegs)
    {
      var id = doc.Objects.AddCurve(s, plotAttr);
      finTempIds.Add(id);
      finDocIds.Add(id != Guid.Empty ? id : Guid.Empty);
      if (id != Guid.Empty) finIds.Add(id);
    }
    foreach (var s in seamSegs)
    {
      // Temp segment: exists only during picking/interior collection; deleted after building.
      var id = doc.Objects.AddCurve(s, cut1Attr);
      if (id != Guid.Empty)
      {
        seamTempIds.Add(id);
        seamIds.Add(id);
        seamDocIds.Add(id);
      }
      else
      {
        seamDocIds.Add(Guid.Empty);
      }
    }
    // Do not delete or modify selected/existing boundary curves.
    doc.Views.Redraw();  // show seam/fin segments before stage-2 prompt

    // Exclude original selected curves, existing boundary pieces, and temp seam picker segments.
    // Do NOT exclude finIds: finished temp pieces inside the facing area must be collected into the facings.
    var excludeInterior = new HashSet<Guid>(toExclude);
    excludeInterior.UnionWith(seamIds);
    // Classify segments as Top / Bottom / Left / Right
    var finParts  = Classify(finSegs,  centroid);
    var seamParts = Classify(seamSegs, centroid);

    // Open log before picker so picker actions are captured
    var logPath = GetLogPath();
    _log = new StreamWriter(logPath, true, System.Text.Encoding.UTF8) { AutoFlush = true };
    L($"── vBiminiParts {DateTime.Now} ──");
    L($"tol={doc.ModelAbsoluteTolerance}  selIds={selIds.Count}  seamIds={seamIds.Count}  finIds={finIds.Count}  excludeInterior={excludeInterior.Count}");
    L($"seamCandidates: {seamSegs.Count} segs, {seamDocIds.Count} docIds");
    for (var _si = 0; _si < seamSegs.Count; _si++)
      L($"  seamSeg[{_si}]: id={(_si < seamDocIds.Count ? seamDocIds[_si] : Guid.Empty)}  len={seamSegs[_si].GetLength():F2}  start={seamSegs[_si].PointAtStart}  end={seamSegs[_si].PointAtEnd}");

    // ── Stage 2: Main pocket curve selection ────────────────────────────────
    // Picker candidates: seam AND finished top/bottom (exclude Left/Right sides).
    var mainCandidates   = new List<Curve>();
    var mainCandidateIds = new List<Guid>();
    var mainSideIds      = new List<int>();  // 0=Top, 1=Bottom; shared between seam and fin
    for (var _ci = 0; _ci < seamSegs.Count; _ci++)
    {
      var _s = seamSegs[_ci];
      if (ReferenceEquals(_s, seamParts.Left) || ReferenceEquals(_s, seamParts.Right)) continue;
      mainCandidates.Add(_s);
      mainCandidateIds.Add(_ci < seamDocIds.Count ? seamDocIds[_ci] : Guid.Empty);
      mainSideIds.Add(ReferenceEquals(_s, seamParts.Top) ? 0 : 1);
    }
    for (var _fi = 0; _fi < finSegs.Count; _fi++)
    {
      var _f = finSegs[_fi];
      if (ReferenceEquals(_f, finParts.Left) || ReferenceEquals(_f, finParts.Right)) continue;
      mainCandidates.Add(_f);
      mainCandidateIds.Add(_fi < finDocIds.Count ? finDocIds[_fi] : Guid.Empty);
      mainSideIds.Add(ReferenceEquals(_f, finParts.Top) ? 0 : 1);
    }
    L($"mainCandidates (seam+fin top/bottom): {mainCandidates.Count}");

    if (!PickPocketCurves("Select center of main pocket curve", 2, doc, mainCandidates, mainCandidateIds, out var mainPicks, mainSideIds))
      return CancelWithTempCleanup(doc, finTempIds, seamTempIds);

    // ── Stage 3: Secondary pocket curve selection ────────────────────────────

    // Carry side exclusions forward so secondary can't re-pick the same Top/Bottom side.
    var mainConsumedIdxs = new HashSet<int>();
    foreach (var (mc, _) in mainPicks)
    {
      var mcMid = mc.PointAtNormalizedLength(0.5);
      var bestCi = -1; var bestD = double.MaxValue;
      for (var _ci = 0; _ci < mainCandidates.Count; _ci++)
      {
        mainCandidates[_ci].ClosestPoint(mcMid, out var _t);
        var _d = mcMid.DistanceTo(mainCandidates[_ci].PointAt(_t));
        if (_d < bestD) { bestD = _d; bestCi = _ci; }
      }
      if (bestCi >= 0)
      {
        var side = mainSideIds[bestCi];
        for (var _ci = 0; _ci < mainCandidates.Count; _ci++)
          if (mainSideIds[_ci] == side) mainConsumedIdxs.Add(_ci);
      }
    }

    List<(Curve Curve, Point3d Center)> secPicks;
    if (mainPicks.Count >= 2)
    {
      secPicks = new List<(Curve, Point3d)>();
    }
    else
    {
      var maxSec = mainPicks.Count == 0 ? 2 : 1;
      if (!PickPocketCurves("Select center of secondary pocket curve", maxSec, doc, mainCandidates,
                            mainCandidateIds, out secPicks, mainSideIds, clearFirst: false, initialPickedIdxs: mainConsumedIdxs))
        return CancelWithTempCleanup(doc, finTempIds, seamTempIds);
    }

    L($"mainPicks={mainPicks.Count}  secPicks={secPicks.Count}");

    // ── Stage 4: Facing parts (FacingP = port/left, FacingS = stbd/right) ───

    BuildFacingParts(doc, seamParts, centroid, cut1Idx, excludeInterior, tol);

    // ── Stage 5: Main pocket geometry ───────────────────────────────────────

    // excludeInterior already contains seamIds (covers all global seam curves incl. Left/Right).
    // finIds are NOT excluded so finished-edge segments inside the pocket outline are collected.
    var pocketExclude = new HashSet<Guid>(excludeInterior);
    L($"pocketExclude: {pocketExclude.Count} ids (selIds + seamIds; finIds not excluded)");

    if (mainPicks.Count > 0)
      BuildMainPocket(doc, mainPicks, seamParts, finParts, centroid, cut1Idx, tol, pocketExclude);

    // ── Stage 6: Secondary pocket geometry ──────────────────────────────────────

    if (secPicks.Count > 0)
      BuildSecondaryPockets(doc, secPicks, mainPicks, seamParts, finParts, centroid, cut1Idx, tol, pocketExclude);

    // ── Stage 6.5: Collect all pocket center points; draw single center line ──
    {
      var refIdxCL = EnsureLayer(doc, _layerRef, _layerRefColor);
      var ctrPts   = new List<Point3d>();

      foreach (var (mc, pktCtr) in mainPicks)
      {
        var adjF = ClosestOf(mc, finParts.Top, finParts.Bottom, finParts.Left, finParts.Right);
        if (adjF == null) continue;
        adjF.ClosestPoint(pktCtr, out var tF);
        ctrPts.Add(adjF.PointAt(tF));
      }
      foreach (var (sc, sp) in secPicks)
      {
        var adjF = ClosestOf(sc, finParts.Top, finParts.Bottom, finParts.Left, finParts.Right);
        if (adjF == null) continue;
        adjF.ClosestPoint(sp, out var tF);
        ctrPts.Add(adjF.PointAt(tF));
      }

      // Single-pick: also place a center point at opposite fin midpoint.
      if (mainPicks.Count + secPicks.Count == 1)
      {
        var sp0     = mainPicks.Count == 1 ? mainPicks[0] : secPicks[0];
        var adjFinS = ClosestOf(sp0.Curve, finParts.Top, finParts.Bottom, finParts.Left, finParts.Right);
        Curve? oppFin = ReferenceEquals(adjFinS, finParts.Top)    ? finParts.Bottom
                      : ReferenceEquals(adjFinS, finParts.Bottom) ? finParts.Top
                      : ReferenceEquals(adjFinS, finParts.Left)   ? finParts.Right
                      :                                              finParts.Left;
        if (oppFin != null)
        {
          var ptOpp = oppFin.PointAtNormalizedLength(0.5);
          if (!NearbyPointExists(doc, ptOpp, tol))
            doc.Objects.AddPoint(ptOpp, new ObjectAttributes { LayerIndex = refIdxCL });
          ctrPts.Add(ptOpp);
        }
      }

      // Sort spatially along the dominant axis, then connect with line(s).
      if (ctrPts.Count >= 2)
      {
        double minX = ctrPts[0].X, maxX = ctrPts[0].X;
        double minY = ctrPts[0].Y, maxY = ctrPts[0].Y;
        foreach (var p in ctrPts)
        {
          if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X;
          if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y;
        }
        bool sortByX = (maxX - minX) >= (maxY - minY);
        ctrPts.Sort((a, b) => sortByX ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
        for (var i = 0; i < ctrPts.Count - 1; i++)
          doc.Objects.AddLine(ctrPts[i], ctrPts[i + 1],
                              new ObjectAttributes { LayerIndex = refIdxCL });
      }
    }

    // ── Stage 7: Extra rectangle for 1-1/2" pipe ────────────────────────────

    if (_extraRect != null && mainPicks.Count > 0)
      BuildExtraRect(doc, mainPicks, seamParts, centroid, cut1Idx, _extraRect, tol);

    // Remove temporary picker segments.
    foreach (var id in finTempIds) if (id != Guid.Empty) doc.Objects.Delete(id, false);
    foreach (var id in seamTempIds) if (id != Guid.Empty) doc.Objects.Delete(id, false);

    // Add permanent boundaries only if the command generated them. Existing document curves stay untouched.
    if (!finishedIsExisting)
      FindOrAddCurve(doc, finishedCrv, plotIdx, plotAttr, toExclude, doc.ModelAbsoluteTolerance);

    if (!seamIsExisting)
      FindOrAddCurve(doc, seamCrv, cut1Idx, cut1Attr, toExclude, doc.ModelAbsoluteTolerance);

    doc.Views.Redraw();
    _log?.Dispose();
    _log = null;
    return Result.Success;
  }
  private static Result CancelWithTempCleanup(RhinoDoc doc, IEnumerable<Guid> finTempIds, IEnumerable<Guid> seamTempIds)
  {
    foreach (var id in finTempIds)
      if (id != Guid.Empty)
        doc.Objects.Delete(id, false);

    foreach (var id in seamTempIds)
      if (id != Guid.Empty)
        doc.Objects.Delete(id, false);

    _log?.Dispose();
    _log = null;
    doc.Views.Redraw();
    RhinoApp.WriteLine("vBiminiParts: canceled.");
    return Result.Cancel;
  }

  // ── Pocket curve picker ─────────────────────────────────────────────────────

  // snapTol: how close a GetPoint click must be to a candidate seam to count as "on" it.
  private const double PickSnapTol = 1.0;

    private static bool PickPocketCurves(
    string prompt, int maxCount, RhinoDoc doc,
    List<Curve> candidates, List<Guid> candidateIds,
    out List<(Curve Curve, Point3d Center)> picks,
    List<int>? sideIds = null, bool clearFirst = true, HashSet<int>? initialPickedIdxs = null)
  {
    picks = new List<(Curve, Point3d)>();
    var pickedIdxs = initialPickedIdxs != null ? new HashSet<int>(initialPickedIdxs) : new HashSet<int>();

    L($"PickPocketCurves: prompt=\"{prompt}\"  maxCount={maxCount}  candidates={candidates.Count}  preExcluded={pickedIdxs.Count}");

    if (clearFirst)
    {
      doc.Objects.UnselectAll();
      doc.Views.Redraw();
    }

    for (var i = 0; i < maxCount; i++)
    {
      var confirmed = false;
      while (true)
      {
        var gp = new GetPoint();
        gp.SetCommandPrompt($"{prompt} ({i + 1}/{maxCount}). Press Enter to finish, Esc to cancel.");
        gp.AcceptNothing(true);

        var res = gp.Get();
        L($"  pick {i}: result={res}");

        if (res == GetResult.Cancel)
        {
          L($"  pick {i}: canceled");
          return false;
        }

        if (res == GetResult.Nothing)
          break;

        if (res != GetResult.Point)
        {
          L($"  pick {i}: unexpected result {res}; canceling");
          return false;
        }

        var pt      = gp.Point();
        var bestIdx = -1;
        var bestDist = double.MaxValue;
        for (var ci = 0; ci < candidates.Count; ci++)
        {
          if (pickedIdxs.Contains(ci)) continue;
          candidates[ci].ClosestPoint(pt, out var t);
          var d = pt.DistanceTo(candidates[ci].PointAt(t));
          if (d < bestDist) { bestDist = d; bestIdx = ci; }
        }
        L($"  pick {i}: bestIdx={bestIdx}  bestDist={bestDist:F3}  tol={PickSnapTol}");

        if (bestIdx < 0 || bestDist > PickSnapTol)
        {
          RhinoApp.WriteLine($"Click ON a seam curve (missed by {bestDist:F2}). Try again.");
          continue;
        }

        // Mark all candidates on the same side as consumed (prevents re-picking opposite type on same side)
        if (sideIds != null)
        {
          var side = sideIds[bestIdx];
          for (var ci = 0; ci < candidates.Count; ci++)
            if (sideIds[ci] == side) pickedIdxs.Add(ci);
        }
        else
        {
          pickedIdxs.Add(bestIdx);
        }
        picks.Add((candidates[bestIdx].DuplicateCurve(), pt));
        L($"  pick {i}: confirmed idx={bestIdx}  pt={pt}");

        // Highlight the confirmed curve
        if (bestIdx < candidateIds.Count && candidateIds[bestIdx] != Guid.Empty)
          doc.Objects.Select(candidateIds[bestIdx], true);
        doc.Views.Redraw();
        confirmed = true;
        break;
      }
      if (!confirmed) break;  // user pressed Enter; stop asking for more picks
    }
    return true;
  }

  // -- Stage 4: Facing parts ───────────────────────────────────────────────────

  private static void BuildFacingParts(RhinoDoc doc, Parts seam,
                                        Point3d centroid, int cut1Idx,
                                        HashSet<Guid> excludeIds, double tol)
  {
    if (seam.Left != null)
      BuildOneFacing(doc, seam.Left, seam.Top, seam.Bottom, centroid, cut1Idx, excludeIds, tol, "FacingP");

    if (seam.Right != null)
      BuildOneFacing(doc, seam.Right, seam.Top, seam.Bottom, centroid, cut1Idx, excludeIds, tol, "FacingS");
  }

  private static void BuildOneFacing(RhinoDoc doc, Curve seamSide,
                                      Curve? seamTop, Curve? seamBot,
                                      Point3d centroid, int cut1Idx,
                                      HashSet<Guid> excludeIds, double tol,
                                      string label)
  {
    // 1. Offset seam side 3" toward centroid → inner facing edge (CUT1)
    var innerEdge = OffsetToward(seamSide, centroid, _facingInset, tol);
    if (innerEdge == null)
    {
      RhinoApp.WriteLine($"vBiminiParts: {label} — offset failed.");
      return;
    }

    // 2. Extend inner edge at both ends to meet seam top/bottom
    var extendTo = new List<GeometryBase>();
    if (seamTop != null) extendTo.Add(seamTop);
    if (seamBot != null) extendTo.Add(seamBot);
    if (extendTo.Count > 0)
    {
      var e1 = innerEdge.Extend(CurveEnd.Start, CurveExtensionStyle.Smooth, extendTo);
      if (e1 != null) innerEdge = e1;
      var e2 = innerEdge.Extend(CurveEnd.End, CurveExtensionStyle.Smooth, extendTo);
      if (e2 != null) innerEdge = e2;
    }

    // 3. Trim seamTop and seamBot to only the facing portion
    //    (between where seamSide endpoint meets them and where innerEdge meets them)
    var connTol      = Math.Max(tol * 200, 0.1);
    var facingCurves = new List<Curve> { innerEdge, seamSide.DuplicateCurve() };
    if (seamTop != null) TryAddTrimmedSeam(facingCurves, innerEdge, seamSide, seamTop, connTol);
    if (seamBot != null) TryAddTrimmedSeam(facingCurves, innerEdge, seamSide, seamBot, connTol);

    // 4. Collect objects inside the facing boundary (before move)
    var interiorObjects = new List<(GeometryBase Geom, ObjectAttributes Attr)>();
    {
      var boundaryTol = Math.Max(tol * 200, 0.1);
      Curve? closedBoundary = null;
      var joined = Curve.JoinCurves(facingCurves.Select(c => c.DuplicateCurve()).ToArray(), boundaryTol);
      if (joined?.Length == 1 && joined[0].IsClosed)
      {
        closedBoundary = joined[0];
      }
      else
      {
        // Fallback: connect innerEdge and seamSide with straight lines at the corners
        var ieS    = innerEdge.PointAtStart;
        var ieE    = innerEdge.PointAtEnd;
        var ssFlip = seamSide.PointAtEnd.DistanceTo(ieE) < seamSide.PointAtStart.DistanceTo(ieE);
        var ssCopy = seamSide.DuplicateCurve();
        if (ssFlip) ssCopy.Reverse();
        var pc = new PolyCurve();
        pc.Append(innerEdge.DuplicateCurve());
        pc.Append(new LineCurve(ieE, ssCopy.PointAtStart));
        pc.Append(ssCopy);
        pc.Append(new LineCurve(ssCopy.PointAtEnd, ieS));
        if (pc.IsClosed || pc.MakeClosed(boundaryTol))
          closedBoundary = pc;
      }
      if (closedBoundary != null)
        interiorObjects = CollectInsideObjects(doc, excludeIds, closedBoundary, Plane.WorldXY, tol);
      L($"  {label}: facingCurves={facingCurves.Count}  boundaryFormed={closedBoundary != null}  excludeIds={excludeIds.Count}  interiorFound={interiorObjects.Count}");
      if (closedBoundary == null)
        L($"  {label}: joined?.Length={joined?.Length}  joined[0].IsClosed={(joined?.Length > 0 ? joined![0].IsClosed.ToString() : "n/a")}");
      foreach (var (g, a) in interiorObjects)
        L($"    interior: {g.GetType().Name}  layer={a.LayerIndex}");
    }

    // 5. Move the whole facing outward so the nearest edge (innerEdge) ends up
    //    FacingMoveOut inches clear of the bimini seam (total = FacingMoveOut + FacingInset)
    var outDir = seamSide.PointAtNormalizedLength(0.5) - centroid;
    outDir.Unitize();
    var xf   = Transform.Translation(outDir * (_facingMoveOut + _facingInset));
    var attr = MakeAttr(cut1Idx);
    attr.Name = label;
    var addedIds = new List<Guid>();
    foreach (var c in facingCurves)
    {
      var copy = c.DuplicateCurve();
      copy.Transform(xf);
      var id = doc.Objects.AddCurve(copy, attr);
      if (id != Guid.Empty) addedIds.Add(id);
    }
    foreach (var (geom, geomAttr) in interiorObjects)
    {
      var copy = geom.Duplicate()!;
      copy.Transform(xf);
      geomAttr.RemoveFromAllGroups();
      var id = AddObjectToDoc(doc, copy, geomAttr);
      if (id != Guid.Empty) addedIds.Add(id);
    }

    // 6. Group all facing objects together
    if (addedIds.Count > 1)
    {
      var grpIdx = doc.Groups.Add();
      foreach (var id in addedIds)
      {
        var obj = doc.Objects.FindId(id);
        if (obj == null) continue;
        obj.Attributes.AddToGroup(grpIdx);
        obj.CommitChanges();
      }
    }
  }

  /// <summary>
  /// Trims <paramref name="topBot"/> to the segment spanning from where
  /// <paramref name="seamSide"/> meets it to where <paramref name="innerEdge"/> meets it,
  /// then appends the trimmed piece to <paramref name="result"/>.
  /// </summary>
  private static void TryAddTrimmedSeam(List<Curve> result,
                                         Curve innerEdge, Curve seamSide,
                                         Curve topBot, double tol)
  {
    // Use actual curve intersections for caps. Endpoint-only trimming can miss
    // bulged top/bottom curves or pick the wrong side of the bulge.
    var tSide = NearestEndpointParam(seamSide, topBot, tol);
    if (!tSide.HasValue)
      return;

    var candidates = CurveIntersectionParamsOnSecondCurve(innerEdge, topBot, tol);

    var tEndpoint = NearestEndpointParam(innerEdge, topBot, tol);
    if (tEndpoint.HasValue)
      candidates.Add(tEndpoint.Value);

    candidates = candidates
      .Where(t => Math.Abs(t - tSide.Value) > RhinoMath.ZeroTolerance)
      .Distinct(new DoubleTolComparer(Math.Max(tol, RhinoMath.ZeroTolerance) * 10.0))
      .ToList();

    if (candidates.Count == 0)
      return;

    var bestT = candidates
      .OrderBy(t => CurveLengthBetween(topBot, tSide.Value, t))
      .First();

    var lo = Math.Min(tSide.Value, bestT);
    var hi = Math.Max(tSide.Value, bestT);
    if (hi - lo < RhinoMath.ZeroTolerance)
      return;

    var trimmed = topBot.Trim(lo, hi);
    if (trimmed != null && trimmed.GetLength() > tol)
      result.Add(trimmed);
  }

  private static List<double> CurveIntersectionParamsOnSecondCurve(Curve first, Curve second, double tol)
  {
    var list = new List<double>();
    var events = Intersection.CurveCurve(first, second, tol, tol);
    if (events == null)
      return list;

    foreach (var ev in events)
    {
      if (ev.IsOverlap)
      {
        list.Add(ev.OverlapB.T0);
        list.Add(ev.OverlapB.T1);
      }
      else
      {
        list.Add(ev.ParameterB);
      }
    }

    return list;
  }

  private static double CurveLengthBetween(Curve curve, double t0, double t1)
  {
    var lo = Math.Min(t0, t1);
    var hi = Math.Max(t0, t1);
    if (hi - lo < RhinoMath.ZeroTolerance)
      return 0.0;

    try
    {
      return curve.GetLength(new Interval(lo, hi));
    }
    catch
    {
      return hi - lo;
    }
  }

  private sealed class DoubleTolComparer : IEqualityComparer<double>
  {
    private readonly double _tol;

    public DoubleTolComparer(double tol)
    {
      _tol = Math.Max(tol, RhinoMath.ZeroTolerance);
    }

    public bool Equals(double x, double y)
    {
      return Math.Abs(x - y) <= _tol;
    }

    public int GetHashCode(double obj)
    {
      return Math.Round(obj / _tol).GetHashCode();
    }
  }



  /// <summary>
  /// Returns the parameter on <paramref name="onCurve"/> of the endpoint of
  /// <paramref name="source"/> (start or end) that lies within <paramref name="tol"/>,
  /// or null if neither endpoint qualifies.
  /// </summary>
  private static double? NearestEndpointParam(Curve source, Curve onCurve, double tol)
  {
    foreach (var pt in new[] { source.PointAtStart, source.PointAtEnd })
    {
      onCurve.ClosestPoint(pt, out var t);
      if (pt.DistanceTo(onCurve.PointAt(t)) < tol)
        return t;
    }
    return null;
  }

  // ── Stage 5: Main pocket ────────────────────────────────────────────────────

  private static void BuildMainPocket(RhinoDoc doc, List<(Curve Curve, Point3d Center)> mainPicks,
                                       Parts seam, Parts fin,
                                       Point3d centroid, int cut1Idx,
                                       double tol, HashSet<Guid> globalExclude)
  {
    double pocketDepth = _mainPktDepth;
    const double extLen  = 24.0;

    L($"BuildMainPocket: pocketDepth={pocketDepth}  picks={mainPicks.Count}");
    foreach (var (mc, pktCenter) in mainPicks)
    {
      var adjSeam = ClosestOf(mc, seam.Top, seam.Bottom, seam.Left, seam.Right);
      if (adjSeam == null) { L($"  mc: no adjSeam"); continue; }

      // Dedup: delete any previously created pocket objects for this same seam
      var seamMid = adjSeam.PointAtNormalizedLength(0.5);
      var seamKey = $"{seamMid.X:F2},{seamMid.Y:F2}";
      var grpName  = "vBiminiPkt:" + seamKey;
      var toDelete = new List<Guid>();

      // Primary: find by UserString tag (set on every object since build 1817)
      foreach (var o in doc.Objects)
        if (o.Attributes.GetUserString("vBiminiPkt") == seamKey)
          toDelete.Add(o.Id);

      // Fallback: find by named group (named group also set since build 1817)
      if (toDelete.Count == 0)
      {
        for (var gi = 0; gi < doc.Groups.Count; gi++)
        {
          if (doc.Groups.GroupName(gi) == grpName)
          {
            var grouped = doc.Objects.FindByGroup(gi);
            if (grouped != null) foreach (var go in grouped) toDelete.Add(go.Id);
            break;
          }
        }
      }

      var dedupTagged = toDelete.Count > 0;
      L($"  mc: seamKey={seamKey}  dedup found={toDelete.Count}  tagged={dedupTagged}");
      foreach (var delId in toDelete) doc.Objects.Delete(delId, true);

      var zipperRaw = OffsetToward(adjSeam, centroid, pocketDepth, tol);
      if (zipperRaw == null) { L($"  mc: zipperRaw offset failed"); continue; }

      var offLeft  = seam.Left  != null ? OffsetAway(seam.Left,  centroid, _sidePktOutward, tol) : null;
      var offRight = seam.Right != null ? OffsetAway(seam.Right, centroid, _sidePktOutward, tol) : null;
      if (offLeft == null || offRight == null) { L($"  mc: offLeft={offLeft != null} offRight={offRight != null} seamL={seam.Left != null} seamR={seam.Right != null}"); continue; }

      // Build mirrored end flares at each corner where adjSeam meets the fin sides
      var adjFin   = ClosestOf(adjSeam, fin.Top, fin.Bottom, fin.Left, fin.Right);
      Curve? mirLeft = null, mirRight = null;
      if (adjFin != null)
      {
        if (fin.Left  != null) { var c = FindSharedEndpoint(adjFin, fin.Left);  if (c.IsValid) mirLeft  = BuildMirroredEnd(adjSeam, fin.Left,  c, pocketDepth, extLen); }
        if (fin.Right != null) { var c = FindSharedEndpoint(adjFin, fin.Right); if (c.IsValid) mirRight = BuildMirroredEnd(adjSeam, fin.Right, c, pocketDepth, extLen); }
      }
      L($"  mc: adjFin={adjFin != null}  mirLeft={mirLeft != null}  mirRight={mirRight != null}");

      // Build closed pocket outline: adjSeam (top) → mirRight → offRight → perpR
      //   → zipper (full width to finished sides) → perpL → offLeft → mirLeft → back
      var pocketOutline = BuildPocketOutline(adjSeam, mirLeft, mirRight,
                                             offLeft, offRight, zipperRaw, fin.Left, fin.Right, extLen, tol);

      // Collect objects inside the pocket boundary before moving
      var interiorObjects = new List<(GeometryBase Geom, ObjectAttributes Attr)>();
      L($"  mc: pocketOutline={pocketOutline != null}  closed={(pocketOutline?.IsClosed)}  globalExclude={globalExclude.Count}");
      if (pocketOutline != null)
        L($"  mc: outline bbox={pocketOutline.GetBoundingBox(true)}");
      if (pocketOutline != null && pocketOutline.IsClosed)
        interiorObjects = CollectInsideObjects(doc, globalExclude, pocketOutline, Plane.WorldXY, tol);
      L($"  mc: interior collected={interiorObjects.Count}");

      // outDir = perpendicular to adjSeam pointing outward, derived from the zipper offset.
      var adjMid = adjSeam.PointAtNormalizedLength(0.5);
      var outDir = adjMid - zipperRaw.PointAtNormalizedLength(0.5);
      outDir.Unitize();

      // Move pocket so the minimum 3D closest-point distance from the pocket's inner boundary
      // to adjSeam equals PktSeamClearance.  The seam is curved (arched), so the projection
      // approach gives wrong results; binary-search for the exact move distance instead.
      // "Inner boundary" = outline points whose closest adjSeam point is in the -outDir direction
      // (i.e. the pocket is currently inside the bimini relative to adjSeam at those points).
      double distToMove;
      if (pocketOutline != null && pocketOutline.IsClosed)
      {
        var innerPts = new List<Point3d>();
        for (var si = 0; si < 128; si++)
        {
          var pt = pocketOutline.PointAtNormalizedLength((double)si / 128);
          adjSeam.ClosestPoint(pt, out var tAdj);
          if (Vector3d.Multiply(pt - adjSeam.PointAt(tAdj), outDir) < 0)
            innerPts.Add(pt);
        }

        double lo = 0, hi = 100;
        for (var iter = 0; iter < 24; iter++)
        {
          var dm = (lo + hi) * 0.5;
          var minD = double.MaxValue;
          foreach (var pt in innerPts)
          {
            var mpt = pt + dm * outDir;
            adjSeam.ClosestPoint(mpt, out var tAdj);
            var d = mpt.DistanceTo(adjSeam.PointAt(tAdj));
            if (d < minD) minD = d;
          }
          if (minD < _pktSeamClearance) lo = dm; else hi = dm;
        }
        distToMove = (lo + hi) * 0.5;
      }
      else
      {
        distToMove = pocketDepth + _pktSeamClearance;  // fallback when outline failed
      }
      L($"  mc: distToMove={distToMove:F3}  outDir={outDir}  innerCount={(pocketOutline != null ? (int)(distToMove * 0) : 0)}");
      var xf = Transform.Translation(outDir * distToMove);

      var addedIds = new List<Guid>();
      if (pocketOutline != null)
      {
        pocketOutline.Transform(xf);

        // Geo-fallback dedup: for untagged pockets from pre-1817 builds, find existing closed
        // curves whose bounding box matches the new pocket outline and delete their whole group.
        if (!dedupTagged)
        {
          var newBbox = pocketOutline.GetBoundingBox(true);
          var newArea = newBbox.Diagonal.X * newBbox.Diagonal.Y;
          var geoFound = new List<Guid>();
          foreach (var o in doc.Objects)
          {
            if (globalExclude.Contains(o.Id)) continue;
            if (o.Geometry is not Curve crv || !crv.IsClosed) continue;
            var ob = crv.GetBoundingBox(true);
            var ix = BoundingBox.Intersection(newBbox, ob);
            if (!ix.IsValid) continue;
            var ixArea  = ix.Diagonal.X  * ix.Diagonal.Y;
            var objArea = ob.Diagonal.X  * ob.Diagonal.Y;
            if (ixArea > 0.5 * newArea && ixArea > 0.5 * objArea)
              geoFound.Add(o.Id);
          }
          if (geoFound.Count > 0)
          {
            // Expand to full groups; skip if group contains any protected (excluded) objects
            var geoIds = new HashSet<Guid>();
            foreach (var id in geoFound)
            {
              var obj = doc.Objects.FindId(id);
              if (obj == null) continue;
              var grps = obj.Attributes.GetGroupList();
              var safe = true;
              var ids  = new HashSet<Guid> { id };
              if (grps != null)
              {
                foreach (var gi in grps)
                {
                  var grouped = doc.Objects.FindByGroup(gi);
                  if (grouped == null) continue;
                  foreach (var go in grouped)
                  {
                    if (globalExclude.Contains(go.Id)) { safe = false; break; }
                    ids.Add(go.Id);
                  }
                  if (!safe) break;
                }
              }
              if (safe) foreach (var i in ids) geoIds.Add(i);
            }
            L($"  mc: geo fallback dedup: deleting {geoIds.Count} untagged pocket objects");
            foreach (var id in geoIds) doc.Objects.Delete(id, true);
          }
        }

        var outlineAttr = MakeAttr(cut1Idx);
        outlineAttr.SetUserString("vBiminiPkt", seamKey);
        var id2 = doc.Objects.AddCurve(pocketOutline, outlineAttr);
        if (id2 != Guid.Empty) addedIds.Add(id2);
      }

      // Center point — project to finished edge; Reference layer
      var refIdx = EnsureLayer(doc, _layerRef, _layerRefColor);
      var ptFinished = pktCenter;
      if (adjFin != null)
      {
        adjFin.ClosestPoint(pktCenter, out var ptT);
        ptFinished = adjFin.PointAt(ptT);
      }
      var ptPocket = ptFinished;
      ptPocket.Transform(xf);
      if (!NearbyPointExists(doc, ptPocket, tol))
      {
        var ptAttr = MakeAttr(refIdx);
        ptAttr.SetUserString("vBiminiPkt", seamKey);
        var ptId = doc.Objects.AddPoint(ptPocket, ptAttr);
        if (ptId != Guid.Empty) addedIds.Add(ptId);
        L($"  mc: pocket center point at {ptPocket}");
      }
      // Center point on finished curve (not moved — center line anchor or standalone ref)
      if (!NearbyPointExists(doc, ptFinished, tol))
      {
        var finPtAttr = MakeAttr(refIdx);
        finPtAttr.SetUserString("vBiminiPkt", seamKey);
        doc.Objects.AddPoint(ptFinished, finPtAttr);
        L($"  mc: finished center point at {ptFinished}");
      }

      foreach (var (geom, geomAttr) in interiorObjects)
      {
        var copy = geom.Duplicate()!;
        copy.Transform(xf);
        geomAttr.RemoveFromAllGroups();
        geomAttr.SetUserString("vBiminiPkt", seamKey);
        var id = AddObjectToDoc(doc, copy, geomAttr);
        if (id != Guid.Empty) addedIds.Add(id);
      }

      // Group all objects under a named group for future dedup
      if (addedIds.Count > 1)
      {
        var grpIdx = doc.Groups.Add(grpName);
        foreach (var id in addedIds)
        {
          var obj = doc.Objects.FindId(id);
          if (obj == null) continue;
          var attr = obj.Attributes;
          attr.AddToGroup(grpIdx);
          doc.Objects.ModifyAttributes(obj, attr, true);
        }
      }
    }

  }

  // ── Stage 6: Secondary pocket ──────────────────────────────────────────────

  // Builds a secondary pocket sleeve and places ±halfInner reference marks on the finished curve.
  // Width formula from BiminiSecondary.py.
  // Pocket: seam segment (top) + straight side walls + inward-offset zipper (bottom),
  //         corners filleted at SecFilletR.  Moved outward by binary-search PktSeamClearance.
  // The ±halfInner stitch marks remain stationary on the finished (PLOT) curve.
  private static void BuildSecondaryPockets(
    RhinoDoc doc,
    List<(Curve Curve, Point3d Center)> secPicks,
    List<(Curve Curve, Point3d Center)> mainPicks,
    Parts seam, Parts fin,
    Point3d centroid, int cut1Idx,
    double tol, HashSet<Guid> globalExclude)
  {
    var refIdx   = EnsureLayer(doc, _layerRef,  _layerRefColor);
    var plotIdx  = EnsureLayer(doc, _layerPlot, _layerPlotColor);
    var refCurve  = mainPicks.Count > 0 ? mainPicks[0].Curve : secPicks[0].Curve;
    var mainHalf  = refCurve.GetLength() / 2.0;
    var secondary = Math.Ceiling(mainHalf / 6.0) * 6.0;
    if (secondary - mainHalf < 2.0) secondary += 6.0;
    var halfFull  = secondary / 2.0;          // cut edge: ±halfFull from center on seam
    var halfInner = halfFull - 1.0;           // stitch/fold mark: ±halfInner on finished curve
    var pktDepth  = _secPktDepth;

    L($"BuildSecondaryPockets: mainHalf={mainHalf:F3}  secondary={secondary}  halfFull={halfFull}  halfInner={halfInner}  depth={pktDepth}");

    foreach (var (secCrv, pickPt) in secPicks)
    {
      var adjSeam = ClosestOf(secCrv, seam.Top, seam.Bottom, seam.Left, seam.Right);
      var adjFin  = ClosestOf(secCrv, fin.Top,  fin.Bottom,  fin.Left,  fin.Right);
      if (adjSeam == null) { L("  sc: no adjSeam"); continue; }

      // Pre-compute finished-curve centers for endLineDir and center line (shared).
      // ptPartnerFinCtr: nearest main fin center, or other secondary's fin center (2-sec/no-main).
      Point3d? ptSecFinCtr = null, ptPartnerFinCtr = null;
      if (adjFin != null)
      {
        adjFin.ClosestPoint(pickPt, out var tCF);
        ptSecFinCtr = adjFin.PointAt(tCF);
      }
      if (mainPicks.Count > 0)
      {
        foreach (var mp in mainPicks)
        {
          var mf = ClosestOf(mp.Curve, fin.Top, fin.Bottom, fin.Left, fin.Right);
          if (mf == null) continue;
          mf.ClosestPoint(mp.Center, out var tMF);
          var cand = mf.PointAt(tMF);
          if (!ptPartnerFinCtr.HasValue ||
              (ptSecFinCtr.HasValue && cand.DistanceTo(ptSecFinCtr.Value) < ptPartnerFinCtr.Value.DistanceTo(ptSecFinCtr.Value)))
            ptPartnerFinCtr = cand;
        }
      }
      else if (secPicks.Count >= 2)
      {
        foreach (var (sc2, sp2) in secPicks)
        {
          if (ReferenceEquals(sc2, secCrv)) continue;
          var sf2 = ClosestOf(sc2, fin.Top, fin.Bottom, fin.Left, fin.Right);
          if (sf2 == null) continue;
          sf2.ClosestPoint(sp2, out var tSF2);
          ptPartnerFinCtr = sf2.PointAt(tSF2);
          break;
        }
      }

      var seamMid = adjSeam.PointAtNormalizedLength(0.5);
      var seamKey = $"sec:{seamMid.X:F2},{seamMid.Y:F2}";
      var grpName = "vBiminiPkt:" + seamKey;

      var toDelete = new List<Guid>();
      foreach (var o in doc.Objects)
        if (o.Attributes.GetUserString("vBiminiPkt") == seamKey)
          toDelete.Add(o.Id);
      foreach (var delId in toDelete) doc.Objects.Delete(delId, true);
      L($"  sc: seamKey={seamKey}  dedup={toDelete.Count}");

      // Center on adjSeam and compute ±halfFull arc-length bounds.
      adjSeam.ClosestPoint(pickPt, out var tCtr);
      var sCtr      = adjSeam.GetLength(new Interval(adjSeam.Domain.Min, tCtr));
      var seamLen   = adjSeam.GetLength();
      var sFwd      = Math.Min(sCtr + halfFull, seamLen);
      var sBwd      = Math.Max(sCtr - halfFull, 0.0);
      if (!adjSeam.LengthParameter(sFwd, out var tFwd) ||
          !adjSeam.LengthParameter(sBwd, out var tBwd))
      { L($"  sc: LengthParameter failed  sCtr={sCtr:F3}  total={seamLen:F3}"); continue; }

      var tLoSeam  = Math.Min(tFwd, tBwd);
      var tHiSeam  = Math.Max(tFwd, tBwd);
      var seamSeg  = adjSeam.Trim(tLoSeam, tHiSeam);
      if (seamSeg == null) { L("  sc: seamSeg trim failed"); continue; }
      var ptL = seamSeg.PointAtStart;   // left end of seam segment
      var ptR = seamSeg.PointAtEnd;     // right end of seam segment

      // Inward offset → full zipper curve.
      var zipFull = OffsetToward(adjSeam, centroid, pktDepth, tol);
      if (zipFull == null) { L("  sc: zipFull offset failed"); continue; }

      // End-line direction: same source as center line (sec→partner on finished curves).
      Vector3d endLineDir;
      if (ptSecFinCtr.HasValue && ptPartnerFinCtr.HasValue)
        endLineDir = ptPartnerFinCtr.Value - ptSecFinCtr.Value;
      else
      {
        Curve? oppSeam = ReferenceEquals(adjSeam, seam.Top)    ? seam.Bottom
                       : ReferenceEquals(adjSeam, seam.Bottom) ? seam.Top
                       : ReferenceEquals(adjSeam, seam.Left)   ? seam.Right
                       :                                          seam.Left;
        endLineDir = (oppSeam?.PointAtNormalizedLength(0.5) ?? centroid) - adjSeam.PointAtNormalizedLength(0.5);
      }
      endLineDir.Unitize();

      // Fallback: closest points on zipper.
      zipFull.ClosestPoint(ptL, out var tZL);
      zipFull.ClosestPoint(ptR, out var tZR);
      var ptZL_fb = zipFull.PointAt(tZL);
      var ptZR_fb = zipFull.PointAt(tZR);

      // Angled end-line intersections with zipFull.
      Point3d ptZR_ang, ptZL_ang;
      {
        var ray = new LineCurve(ptR - endLineDir * 80.0, ptR + endLineDir * 80.0);
        var ev  = Intersection.CurveCurve(ray, zipFull, tol, tol);
        if (ev != null && ev.Count > 0)
        {
          var bd = double.MaxValue; var bi = 0;
          for (var k = 0; k < ev.Count; k++)
          { var d = ray.PointAt(ev[k].ParameterA).DistanceTo(ptR); if (d < bd) { bd = d; bi = k; } }
          ptZR_ang = zipFull.PointAt(ev[bi].ParameterB);
        }
        else ptZR_ang = ptZR_fb;
      }
      {
        var ray = new LineCurve(ptL - endLineDir * 80.0, ptL + endLineDir * 80.0);
        var ev  = Intersection.CurveCurve(ray, zipFull, tol, tol);
        if (ev != null && ev.Count > 0)
        {
          var bd = double.MaxValue; var bi = 0;
          for (var k = 0; k < ev.Count; k++)
          { var d = ray.PointAt(ev[k].ParameterA).DistanceTo(ptL); if (d < bd) { bd = d; bi = k; } }
          ptZL_ang = zipFull.PointAt(ev[bi].ParameterB);
        }
        else ptZL_ang = ptZL_fb;
      }

      // Re-trim zipper between angled intersection points; orient ptZR_ang → ptZL_ang.
      zipFull.ClosestPoint(ptZR_ang, out var tZR2);
      zipFull.ClosestPoint(ptZL_ang, out var tZL2);
      var tLoZip = Math.Min(tZR2, tZL2);
      var tHiZip = Math.Max(tZR2, tZL2);
      var zipSeg = zipFull.Trim(tLoZip, tHiZip);
      if (zipSeg == null) { L("  sc: zipSeg trim failed"); continue; }
      if (zipSeg.PointAtStart.DistanceTo(ptZR_ang) > zipSeg.PointAtStart.DistanceTo(ptZL_ang))
        zipSeg.Reverse();

      var wallR    = new LineCurve(ptR,      ptZR_ang);
      var wallLrev = new LineCurve(ptZL_ang, ptL);

      // Perpendicular to endLineDir in XY, pointing from ptR toward ptL (toward center).
      var edXY   = new Vector3d(endLineDir.X, endLineDir.Y, 0); edXY.Unitize();
      var perp90 = new Vector3d(-edXY.Y, edXY.X, 0);
      var toLeft = ptL - ptR; toLeft.Z = 0;
      if (Vector3d.Multiply(toLeft, perp90) < 0) perp90 = -perp90;

      // Pre-compute offset end marks (1" toward center) — added to doc after xf is applied.
      var topBound     = (Curve?)(adjFin) ?? adjSeam;
      var endMarkLines = new List<LineCurve>();
      foreach (var (wStart, wEnd, pSign) in new (Point3d, Point3d, double)[]
               { (ptR, ptZR_ang, 1.0), (ptL, ptZL_ang, -1.0) })
      {
        var shift      = perp90 * pSign;
        var markOrigin = wStart + shift;
        var markRay    = new LineCurve(markOrigin - endLineDir * 40.0,
                                       markOrigin + endLineDir * 40.0);
        Point3d ptMarkTop = wStart + shift;
        if (topBound != null)
        {
          var evT = Intersection.CurveCurve(markRay, topBound, tol * 10, tol);
          if (evT != null && evT.Count > 0) ptMarkTop = evT[0].PointA;
        }
        Point3d ptMarkBot = wEnd + shift;
        {
          var evB = Intersection.CurveCurve(markRay, zipFull, tol * 10, tol);
          if (evB != null && evB.Count > 0)
          {
            ptMarkBot = evB[0].PointA;
            var bd = ptMarkBot.DistanceTo(wEnd + shift);
            for (var k = 1; k < evB.Count; k++)
            { var d = evB[k].PointA.DistanceTo(wEnd + shift); if (d < bd) { bd = d; ptMarkBot = evB[k].PointA; } }
          }
        }
        if (ptMarkTop.DistanceTo(ptMarkBot) > tol * 10)
          endMarkLines.Add(new LineCurve(ptMarkTop, ptMarkBot));
      }
      L($"  sc: endLineDir={endLineDir}  endMarkLines={endMarkLines.Count}");

      // Closed outline: seamSeg → wallR → zipSeg(R→L) → wallLrev
      var pocketOutline = BuildSecondaryOutline(seamSeg, wallR, zipSeg, wallLrev, tol);
      if (pocketOutline == null) { L("  sc: outline build failed"); continue; }

      var interiorObjects = CollectInsideObjects(doc, globalExclude, pocketOutline, Plane.WorldXY, tol);
      L($"  sc: interior collected={interiorObjects.Count}");

      // Binary-search distToMove for PktSeamClearance.
      var outDir = adjSeam.PointAtNormalizedLength(0.5) - zipFull.PointAtNormalizedLength(0.5);
      outDir.Unitize();

      var innerPts = new List<Point3d>();
      for (var si = 0; si < 128; si++)
      {
        var pt = pocketOutline.PointAtNormalizedLength((double)si / 128);
        adjSeam.ClosestPoint(pt, out var tAdj);
        if (Vector3d.Multiply(pt - adjSeam.PointAt(tAdj), outDir) < 0)
          innerPts.Add(pt);
      }
      double lo = 0, hi = 100;
      if (innerPts.Count > 0)
        for (var iter = 0; iter < 24; iter++)
        {
          var dm   = (lo + hi) * 0.5;
          var minD = double.MaxValue;
          foreach (var pt in innerPts)
          {
            var mpt = pt + dm * outDir;
            adjSeam.ClosestPoint(mpt, out var tAdj);
            var d = mpt.DistanceTo(adjSeam.PointAt(tAdj));
            if (d < minD) minD = d;
          }
          if (minD < _pktSeamClearance) lo = dm; else hi = dm;
        }
      var distToMove = (lo + hi) * 0.5;
      L($"  sc: distToMove={distToMove:F3}  outDir={outDir}");
      var xf = Transform.Translation(outDir * distToMove);

      pocketOutline.Transform(xf);
      var addedIds = new List<Guid>();

      var outlineAttr = MakeAttr(cut1Idx);
      outlineAttr.SetUserString("vBiminiPkt", seamKey);
      var outlineId = doc.Objects.AddCurve(pocketOutline, outlineAttr);
      if (outlineId != Guid.Empty) addedIds.Add(outlineId);

      foreach (var (geom, geomAttr) in interiorObjects)
      {
        var copy = geom.Duplicate()!;
        copy.Transform(xf);
        geomAttr.RemoveFromAllGroups();
        geomAttr.SetUserString("vBiminiPkt", seamKey);
        var id = AddObjectToDoc(doc, copy, geomAttr);
        if (id != Guid.Empty) addedIds.Add(id);
      }

      // Offset end marks: 1" toward center, PLOT layer, moved with pocket.
      foreach (var em in endMarkLines)
      {
        em.Transform(xf);
        var emAttr = MakeAttr(plotIdx);
        emAttr.SetUserString("vBiminiPkt", seamKey);
        var emId = doc.Objects.AddCurve(em, emAttr);
        if (emId != Guid.Empty) addedIds.Add(emId);
      }

      // Center point on pocket (Reference layer, moved with pocket)
      var ptPktCtr = adjSeam.PointAt(tCtr);
      ptPktCtr.Transform(xf);
      if (!NearbyPointExists(doc, ptPktCtr, tol))
      {
        var pktPtAttr = MakeAttr(refIdx);
        pktPtAttr.SetUserString("vBiminiPkt", seamKey);
        var pktPtId = doc.Objects.AddPoint(ptPktCtr, pktPtAttr);
        if (pktPtId != Guid.Empty) addedIds.Add(pktPtId);
        L($"  sc: pocket center point at {ptPktCtr}");
      }

      if (addedIds.Count > 1)
      {
        var grpIdx = doc.Groups.Add(grpName);
        foreach (var id in addedIds)
        {
          var obj = doc.Objects.FindId(id);
          if (obj == null) continue;
          var attr = obj.Attributes;
          attr.AddToGroup(grpIdx);
          doc.Objects.ModifyAttributes(obj, attr, true);
        }
      }

      // Place ±halfInner stitch marks on the FINISHED curve and draw center line in REF layer.
      if (adjFin != null)
      {
        adjFin.ClosestPoint(pickPt, out var tCtrFin);
        var sCtrFin  = adjFin.GetLength(new Interval(adjFin.Domain.Min, tCtrFin));
        var finLen   = adjFin.GetLength();
        foreach (var sign in new[] { -1.0, 1.0 })
        {
          var s = sCtrFin + sign * halfInner;
          if (s < 0 || s > finLen) { L($"    sc: fin mark s={s:F3} out of range"); continue; }
          if (!adjFin.LengthParameter(s, out var t)) continue;
          var pt = adjFin.PointAt(t);
          doc.Objects.AddPoint(pt, new ObjectAttributes { LayerIndex = refIdx });
          L($"    sc: fin mark sign={sign:+#;-#}  pt={pt}");
        }

        // Center point on finished curve (Reference layer, not moved)
        var ptSecCtr = adjFin.PointAt(tCtrFin);
        if (!NearbyPointExists(doc, ptSecCtr, tol))
        {
          var finPtAttr = MakeAttr(refIdx);
          finPtAttr.SetUserString("vBiminiPkt", seamKey);
          doc.Objects.AddPoint(ptSecCtr, finPtAttr);
          L($"    sc: finished center point at {ptSecCtr}");
        }

      }

      L($"  sc: added {addedIds.Count} pocket objects for {seamKey}");
    }
  }

  private static void BuildExtraRect(
    RhinoDoc doc, List<(Curve Curve, Point3d Center)> mainPicks,
    Parts seam, Point3d centroid, int cut1Idx, ExtraRectConfig cfg, double tol)
  {
    foreach (var (mc, _) in mainPicks)
    {
      var adjSeam = ClosestOf(mc, seam.Top, seam.Bottom, seam.Left, seam.Right);
      if (adjSeam == null) { L("  extraRect: no adjSeam"); continue; }

      var seamLen = adjSeam.GetLength();
      var rectLen = seamLen + cfg.LengthExtra;
      var seamMid = adjSeam.PointAtNormalizedLength(0.5);

      var tang = adjSeam.TangentAt(adjSeam.Domain.Mid);
      tang.Unitize();

      var toOut = seamMid - centroid;
      var dot   = Vector3d.Multiply(toOut, tang);
      toOut -= dot * tang;
      toOut.Unitize();

      var offset = _pktSeamClearance + _mainPktDepth + 1.0;
      var origin = seamMid + toOut * offset;
      var plane  = new Plane(origin, tang, toOut);
      var rect   = new Rectangle3d(plane,
                     new Interval(-rectLen / 2.0, rectLen / 2.0),
                     new Interval(0.0, cfg.Height));

      doc.Objects.AddCurve(rect.ToNurbsCurve(), MakeAttr(cut1Idx));
      L($"  extraRect: seamLen={seamLen:F3}  rectLen={rectLen:F3}  h={cfg.Height}  offset={offset:F3}");
    }
  }

  /// <summary>
  /// Joins seamSeg → wallR → zipSeg → wallLrev into a closed sharp-cornered outline.
  /// </summary>
  private static Curve? BuildSecondaryOutline(
    Curve seamSeg, Curve wallR, Curve zipSeg, Curve wallLrev, double tol)
  {
    var joinTol = Math.Max(tol * 200, 0.1);
    var joined  = Curve.JoinCurves(
      new Curve[] { seamSeg.DuplicateCurve(), wallR.DuplicateCurve(),
                    zipSeg.DuplicateCurve(),  wallLrev.DuplicateCurve() }, joinTol);
    return joined?.Length == 1 && joined[0].IsClosed ? joined[0] : null;
  }

  /// <summary>
  /// Builds a closed pocket outline. The zipper extends full-width to the seam side curves.
  /// At each zip-seam corner a perpendicular line connects to the offset side wall.
  /// Offset sides run from perpendicular foot up to the mirrored end flares (8-segment outline).
  /// Falls back to a 4-sided rect when mirrored ends are unavailable.
  /// </summary>
  private static Curve? BuildPocketOutline(Curve adjSeam,
                                            Curve? mirLeft,  Curve? mirRight,
                                            Curve  offLeft,  Curve  offRight,
                                            Curve  zipper,
                                            Curve? seamLeft, Curve? seamRight,
                                            double extLen,   double tol)
  {
    var offLExt = offLeft.Extend(CurveEnd.Both, extLen, CurveExtensionStyle.Line) ?? offLeft.DuplicateCurve();
    var offRExt = offRight.Extend(CurveEnd.Both, extLen, CurveExtensionStyle.Line) ?? offRight.DuplicateCurve();
    var zipExt  = zipper.Extend(CurveEnd.Both, extLen, CurveExtensionStyle.Line)  ?? zipper.DuplicateCurve();

    // Bottom corners: zip extends to seam sides; perpendiculars connect zip-seam corners to offset side walls
    Curve?     zipSeg;
    LineCurve? perpL = null, perpR = null;
    double     tBotL_off, tBotR_off;  // parameters on offLExt/offRExt for perpendicular feet

    if (seamLeft != null && seamRight != null)
    {
      var seamLExt = seamLeft.Extend(CurveEnd.Both, extLen, CurveExtensionStyle.Line) ?? seamLeft.DuplicateCurve();
      var seamRExt = seamRight.Extend(CurveEnd.Both, extLen, CurveExtensionStyle.Line) ?? seamRight.DuplicateCurve();
      if (!FindIntersectionParam(seamLExt, zipExt, tol, out _, out var tSL_zip)) { L("  BuildPocketOutline: no seamLeft∩zipper"); return null; }
      if (!FindIntersectionParam(seamRExt, zipExt, tol, out _, out var tSR_zip)) { L("  BuildPocketOutline: no seamRight∩zipper"); return null; }

      zipSeg = zipExt.Trim(Math.Min(tSL_zip, tSR_zip), Math.Max(tSL_zip, tSR_zip));
      if (zipSeg == null) { L("  BuildPocketOutline: zipSeg trim null"); return null; }

      var ptBotL = zipExt.PointAt(tSL_zip);  // zip ∩ seamLeft
      var ptBotR = zipExt.PointAt(tSR_zip);  // zip ∩ seamRight
      if (!offLExt.ClosestPoint(ptBotL, out tBotL_off)) { L("  BuildPocketOutline: perpL closest-point failed"); return null; }
      if (!offRExt.ClosestPoint(ptBotR, out tBotR_off)) { L("  BuildPocketOutline: perpR closest-point failed"); return null; }

      perpL = new LineCurve(ptBotL, offLExt.PointAt(tBotL_off));
      perpR = new LineCurve(ptBotR, offRExt.PointAt(tBotR_off));
      L($"  BuildPocketOutline: seam corners  ptBotL={ptBotL}  ptBotR={ptBotR}");
    }
    else
    {
      // Fallback: bottom corners = offset sides ∩ zipper
      if (!FindIntersectionParam(offLExt, zipExt, tol, out tBotL_off, out var tOL_zip)) { L("  BuildPocketOutline: no offLeft∩zipper"); return null; }
      if (!FindIntersectionParam(offRExt, zipExt, tol, out tBotR_off, out var tOR_zip)) { L("  BuildPocketOutline: no offRight∩zipper"); return null; }
      zipSeg = zipExt.Trim(Math.Min(tOL_zip, tOR_zip), Math.Max(tOL_zip, tOR_zip));
      if (zipSeg == null) { L("  BuildPocketOutline: zipSeg trim null"); return null; }
    }

    Curve[] segments;
    if (mirLeft != null && mirRight != null)
    {
      // Top corners: mirrored flares ∩ offset sides
      if (!FindIntersectionParam(mirLeft,  offLExt, tol, out var tML_mir, out var tML_off)) { L("  BuildPocketOutline: no mirLeft∩offLeft"); return null; }
      if (!FindIntersectionParam(mirRight, offRExt, tol, out var tMR_mir, out var tMR_off)) { L("  BuildPocketOutline: no mirRight∩offRight"); return null; }

      // Trim flares from adjSeam endpoint (T0) to where they meet the offset side
      var mirLeftSeg  = mirLeft.Trim(mirLeft.Domain.T0,   tML_mir)
                      ?? mirLeft.Trim(tML_mir, mirLeft.Domain.T1);
      var mirRightSeg = mirRight.Trim(mirRight.Domain.T0, tMR_mir)
                      ?? mirRight.Trim(tMR_mir, mirRight.Domain.T1);
      if (mirLeftSeg == null || mirRightSeg == null) { L($"  BuildPocketOutline: flare trim null  mirLeftSeg={mirLeftSeg != null}  mirRightSeg={mirRightSeg != null}"); return null; }

      // Offset sides: flare intersection (top) to perpendicular foot (bottom)
      var offLSeg = offLExt.Trim(Math.Min(tML_off, tBotL_off), Math.Max(tML_off, tBotL_off));
      var offRSeg = offRExt.Trim(Math.Min(tMR_off, tBotR_off), Math.Max(tMR_off, tBotR_off));
      if (offLSeg == null || offRSeg == null) { L($"  BuildPocketOutline: offSide trim null  offLSeg={offLSeg != null}  offRSeg={offRSeg != null}"); return null; }

      // 8-seg: adjSeam → mirRight → offRight → perpR → zip → perpL → offLeft → mirLeft
      if (perpL != null && perpR != null)
        segments = new Curve[] { adjSeam.DuplicateCurve(), mirRightSeg, offRSeg, perpR, zipSeg, perpL, offLSeg, mirLeftSeg };
      else
        segments = new Curve[] { adjSeam.DuplicateCurve(), mirRightSeg, offRSeg, zipSeg, offLSeg, mirLeftSeg };
    }
    else
    {
      L("  BuildPocketOutline: fallback 4-sided (no mirLeft/mirRight)");
      var adjExt = adjSeam.Extend(CurveEnd.Both, extLen, CurveExtensionStyle.Line) ?? adjSeam.DuplicateCurve();
      if (!FindIntersectionParam(adjExt, offLExt, tol, out var tAdj_L, out var tOL_adj)) { L("  BuildPocketOutline: no adjSeam∩offLeft"); return null; }
      if (!FindIntersectionParam(adjExt, offRExt, tol, out var tAdj_R, out var tOR_adj)) { L("  BuildPocketOutline: no adjSeam∩offRight"); return null; }

      var topSeg = adjExt.Trim(Math.Min(tAdj_L, tAdj_R), Math.Max(tAdj_L, tAdj_R));
      if (topSeg == null) { L("  BuildPocketOutline: topSeg trim null"); return null; }

      var offLSeg4 = offLExt.Trim(Math.Min(tOL_adj, tBotL_off), Math.Max(tOL_adj, tBotL_off));
      var offRSeg4 = offRExt.Trim(Math.Min(tOR_adj, tBotR_off), Math.Max(tOR_adj, tBotR_off));
      if (offLSeg4 == null || offRSeg4 == null) { L($"  BuildPocketOutline: 4-sided offSide trim null  offLSeg4={offLSeg4 != null}  offRSeg4={offRSeg4 != null}"); return null; }

      if (perpL != null && perpR != null)
        segments = new Curve[] { topSeg, offRSeg4, perpR, zipSeg, perpL, offLSeg4 };
      else
        segments = new Curve[] { topSeg, offRSeg4, zipSeg, offLSeg4 };
    }

    for (int i = 0; i < segments.Length; i++)
    {
      var s = segments[i];
      var sn = segments[(i + 1) % segments.Length];
      double gap = Math.Min(
        Math.Min(s.PointAtStart.DistanceTo(sn.PointAtStart), s.PointAtStart.DistanceTo(sn.PointAtEnd)),
        Math.Min(s.PointAtEnd.DistanceTo(sn.PointAtStart),   s.PointAtEnd.DistanceTo(sn.PointAtEnd)));
      L($"  seg[{i}]→seg[{(i+1)%segments.Length}]  gap={gap:F4}  s0={s.PointAtStart}  s1={s.PointAtEnd}  n0={sn.PointAtStart}  n1={sn.PointAtEnd}");
    }
    var joinTol = Math.Max(tol * 200, 0.1);
    var joined  = Curve.JoinCurves(segments, joinTol);
    L($"  BuildPocketOutline: joinTol={joinTol:F4}  segs={segments.Length}  joined={joined?.Length}  closed={(joined?.Length == 1 ? joined[0].IsClosed.ToString() : "n/a")}");
    return joined?.Length == 1 && joined[0].IsClosed ? joined[0] : null;
  }

  /// <summary>Returns the parameter on each curve at their first intersection, or false if none.</summary>
  private static bool FindIntersectionParam(Curve a, Curve b, double tol,
                                             out double tA, out double tB)
  {
    tA = tB = double.NaN;
    var events = Intersection.CurveCurve(a, b, tol, tol);
    if (events == null || events.Count == 0) return false;
    tA = events[0].ParameterA;
    tB = events[0].ParameterB;
    return true;
  }

  /// <summary>
  /// Mirrors a section of <paramref name="adjSeam"/> (length ≤ <paramref name="depth"/>)
  /// at the end nearest <paramref name="cornerPt"/> across <paramref name="finSide"/> at
  /// <paramref name="cornerPt"/>. Returns the mirrored curve with Start ≈ cornerPt,
  /// extended at the far end by <paramref name="extLen"/> so it overshoots the offset side.
  /// </summary>
  private static Curve? BuildMirroredEnd(Curve adjSeam, Curve finSide, Point3d cornerPt,
                                          double depth, double extLen)
  {
    var totalLen  = adjSeam.GetLength();
    var isAtStart = adjSeam.PointAtStart.DistanceTo(cornerPt)
                    <= adjSeam.PointAtEnd.DistanceTo(cornerPt);
    Curve? section;
    if (isAtStart)
    {
      if (!adjSeam.LengthParameter(Math.Min(depth, totalLen * 0.5), out var tEnd)) return null;
      section = adjSeam.Trim(adjSeam.Domain.T0, tEnd);
    }
    else
    {
      if (!adjSeam.LengthParameter(Math.Max(totalLen - depth, totalLen * 0.5), out var tStart)) return null;
      section = adjSeam.Trim(tStart, adjSeam.Domain.T1);
    }
    if (section == null) return null;

    var sideDir      = finSide.PointAtEnd - finSide.PointAtStart;
    sideDir.Unitize();
    var mirrorNormal = new Vector3d(-sideDir.Y, sideDir.X, 0);
    var mirrorOrigin = isAtStart ? adjSeam.PointAtStart : adjSeam.PointAtEnd;
    var mirrored     = section.DuplicateCurve();
    mirrored.Transform(Transform.Mirror(new Plane(mirrorOrigin, mirrorNormal)));

    // Ensure Start = mirrorOrigin so T0-based trim in BuildPocketOutline connects directly to adjSeam
    if (mirrored.PointAtEnd.DistanceTo(mirrorOrigin) < mirrored.PointAtStart.DistanceTo(mirrorOrigin))
      mirrored.Reverse();

    // Extend the far end (away from cornerPt) so it overshoots the offset side
    var extended = mirrored.Extend(CurveEnd.End, extLen, CurveExtensionStyle.Line);
    return extended ?? mirrored;
  }

  /// <summary>Returns the shared endpoint of two curves (within 0.5"), or <see cref="Point3d.Unset"/>.</summary>
  private static Point3d FindSharedEndpoint(Curve a, Curve b)
  {
    foreach (var pa in new[] { a.PointAtStart, a.PointAtEnd })
      foreach (var pb in new[] { b.PointAtStart, b.PointAtEnd })
        if (pa.DistanceTo(pb) < 0.5)
          return pa;
    return Point3d.Unset;
  }

  private static void DrawPerpLine(RhinoDoc doc, Point3d from, Curve? side, int layerIdx, double tol)
  {
    if (side == null) return;
    side.ClosestPoint(from, out var t);
    var foot = side.PointAt(t);
    if (from.DistanceTo(foot) < tol) return;
    doc.Objects.AddCurve(new Line(from, foot).ToNurbsCurve(), MakeAttr(layerIdx));
  }

  // ── Geometry utilities ──────────────────────────────────────────────────────

  private static int EnsureLayer(RhinoDoc doc, string name, Color color)
  {
    var idx = doc.Layers.FindByFullPath(name, RhinoMath.UnsetIntIndex);
    if (idx >= 0) return idx;
    return doc.Layers.Add(new Layer { Name = name, Color = color });
  }

  private static ObjectAttributes MakeAttr(int layerIdx) =>
    new ObjectAttributes { LayerIndex = layerIdx };

  /// <summary>Returns true if any Point object already exists within <paramref name="tol"/> of <paramref name="pt"/>.</summary>
  private static bool NearbyPointExists(RhinoDoc doc, Point3d pt, double tol)
  {
    foreach (var obj in doc.Objects.GetObjectList(ObjectType.Point))
      if (obj.Geometry is Rhino.Geometry.Point gpt && gpt.Location.DistanceTo(pt) <= tol)
        return true;
    return false;
  }

  /// <summary>Offsets <paramref name="crv"/> by <paramref name="dist"/> toward <paramref name="target"/>.</summary>
  private static Curve? OffsetToward(Curve crv, Point3d target, double dist, double tol)
  {
    var a = TryOffset(crv,  dist, tol);
    var b = TryOffset(crv, -dist, tol);
    return PickByDistance(a, b, target, closer: true);
  }

  /// <summary>Offsets <paramref name="crv"/> by <paramref name="dist"/> away from <paramref name="target"/>.</summary>
  private static Curve? OffsetAway(Curve crv, Point3d target, double dist, double tol)
  {
    var a = TryOffset(crv,  dist, tol);
    var b = TryOffset(crv, -dist, tol);
    return PickByDistance(a, b, target, closer: false);
  }

  private static Curve? TryOffset(Curve crv, double dist, double tol)
  {
    var pieces = crv.Offset(Plane.WorldXY, dist, tol, CurveOffsetCornerStyle.Sharp);
    if (pieces == null || pieces.Length == 0) return null;
    if (pieces.Length == 1) return pieces[0];
    var joined = Curve.JoinCurves(pieces, tol);
    return joined?.Length > 0 ? joined[0] : null;
  }

  private static Curve? PickByDistance(Curve? a, Curve? b, Point3d target, bool closer)
  {
    if (a == null) return b;
    if (b == null) return a;
    // Use minimum distance from target to each curve (ClosestPoint), not area centroid.
    // For a closed curve that encloses the target, the inner offset has a smaller
    // minimum distance and the outer offset has a larger one.
    a.ClosestPoint(target, out var ta);
    b.ClosestPoint(target, out var tb);
    var da = a.PointAt(ta).DistanceTo(target);
    var db = b.PointAt(tb).DistanceTo(target);
    return closer ? (da <= db ? a : b) : (da >= db ? a : b);
  }

  private static Point3d CurveCentroid(Curve c) =>
    AreaMassProperties.Compute(c)?.Centroid ?? c.PointAtNormalizedLength(0.5);

  /// <summary>
  /// Returns the first doc curve (not in <paramref name="excludeIds"/>) whose points
  /// are all within <paramref name="threshold"/> of <paramref name="target"/>.
  /// Used to detect a pre-existing Finished curve inside the selected boundary.
  /// </summary>
  /// Finds all doc curves on <paramref name="layerIdx"/> whose midpoint lies within
  /// <paramref name="threshold"/> of <paramref name="target"/>. Works for open segments too.
  private static List<RhinoObject> FindNearCurves(RhinoDoc doc, Curve target,
                                                   HashSet<Guid> excludeIds, double threshold,
                                                   int layerIdx = -1)
  {
    var result = new List<RhinoObject>();
    foreach (var ro in doc.Objects.GetObjectList(ObjectType.Curve))
    {
      if (ro == null || excludeIds.Contains(ro.Id)) continue;
      if (layerIdx >= 0 && ro.Attributes.LayerIndex != layerIdx) continue;
      var c = ro.Geometry as Curve;
      if (c == null) continue;
      var cMid = c.PointAtNormalizedLength(0.5);
      target.ClosestPoint(cMid, out var t);
      if (cMid.DistanceTo(target.PointAt(t)) < threshold)
        result.Add(ro);
    }
    return result;
  }

  private static Curve? DuplicateJoinedCurveFromObjects(IEnumerable<RhinoObject> objects, double tol)
  {
    var pieces = objects
      .Select(o => (o.Geometry as Curve)?.DuplicateCurve())
      .Where(c => c != null)
      .Cast<Curve>()
      .ToArray();

    if (pieces.Length == 0)
      return null;

    if (pieces.Length == 1)
      return pieces[0];

    var joined = Curve.JoinCurves(pieces, tol);
    if (joined != null && joined.Length > 0)
    {
      var closed = joined.FirstOrDefault(c => c.IsClosed);
      if (closed != null)
        return closed;

      return joined.OrderByDescending(c => c.GetLength()).First();
    }

    return pieces.OrderByDescending(c => c.GetLength()).First();
  }
  private static RhinoObject? FindNearCurve(RhinoDoc doc, Curve target, HashSet<Guid> excludeIds, double threshold)
  {
    const int samples = 20;
    var pts = Enumerable.Range(0, samples)
                        .Select(i => target.PointAtNormalizedLength(i / (double)(samples - 1)))
                        .ToArray();

    foreach (var ro in doc.Objects.GetObjectList(ObjectType.Curve))
    {
      if (ro == null || excludeIds.Contains(ro.Id)) continue;
      var c = ro.Geometry as Curve;
      if (c == null || !c.IsClosed) continue;
      if (pts.All(pt =>
      {
        c.ClosestPoint(pt, out var t);
        return pt.DistanceTo(c.PointAt(t)) < threshold;
      }))
        return ro;
    }
    return null;
  }

  /// <summary>
  /// Splits a closed curve at every G1-discontinuity that exceeds <paramref name="angleDeg"/>.
  /// </summary>
  private static List<Curve> BreakAtCorners(Curve crv, double angleDeg)
  {
    var dom = crv.Domain;
    var eps = Math.Max((dom.T1 - dom.T0) * 1e-6, RhinoMath.ZeroTolerance * 10.0);
    var cps = new List<double>();

    var seek = dom.T0 + eps;
    while (crv.GetNextDiscontinuity(Continuity.G1_continuous, seek, dom.T1, out var t))
    {
      if (IsSharpCorner(crv, t, angleDeg, dom, eps)) cps.Add(t);
      seek = t + eps;
      if (seek >= dom.T1) break;
    }

    // Closed-curve seam is never reported by GetNextDiscontinuity — test explicitly
    if (crv.IsClosed && IsSharpCorner(crv, dom.T0, angleDeg, dom, eps))
      cps.Add(dom.T0);

    if (cps.Count == 0)
      return new List<Curve> { crv.DuplicateCurve() };

    cps = cps.Distinct().OrderBy(v => v).ToList();
    var split = crv.Split(cps.ToArray());
    return split != null && split.Length > 0
           ? new List<Curve>(split)
           : new List<Curve> { crv.DuplicateCurve() };
  }

  private static bool IsSharpCorner(Curve crv, double t, double angleDeg, Interval dom, double eps)
  {
    Vector3d tA, tB;
    if (crv.IsClosed && Math.Abs(t - dom.T0) < eps * 10)
    {
      tA = crv.TangentAt(dom.T1 - eps);
      tB = crv.TangentAt(dom.T0 + eps);
    }
    else
    {
      tA = crv.TangentAt(Math.Max(dom.T0, t - eps));
      tB = crv.TangentAt(Math.Min(dom.T1, t + eps));
    }
    if (!tA.Unitize() || !tB.Unitize()) return false;
    var dot = Math.Max(-1.0, Math.Min(1.0, tA * tB));
    return RhinoMath.ToDegrees(Math.Acos(dot)) >= angleDeg;
  }

  // ── Segment classification ──────────────────────────────────────────────────

  private sealed class Parts
  {
    public Curve? Top, Bottom, Left, Right;
  }

  /// <summary>
  /// Classifies up to 4 segments as Top / Bottom / Left / Right using their
  /// bounding-box orientation relative to <paramref name="centroid"/>.
  /// Horizontal segments (wider than tall) → Top or Bottom.
  /// Vertical segments (taller than wide) → Left or Right.
  /// </summary>
  private static Parts Classify(List<Curve> segs, Point3d centroid)
  {
    var p = new Parts();
    if (segs.Count == 0) return p;

    var items = segs.Select(s =>
    {
      var bb = s.GetBoundingBox(false);
      return (Curve: s, BBox: bb, Mid: s.PointAtNormalizedLength(0.5));
    }).ToList();

    if (segs.Count == 4)
    {
      foreach (var item in items)
      {
        double w = item.BBox.Max.X - item.BBox.Min.X;
        double h = item.BBox.Max.Y - item.BBox.Min.Y;
        if (w >= h)  // horizontal → Top / Bottom
        {
          if (item.Mid.Y >= centroid.Y) p.Top    ??= item.Curve;
          else                          p.Bottom ??= item.Curve;
        }
        else         // vertical → Left / Right
        {
          if (item.Mid.X <= centroid.X) p.Left   ??= item.Curve;
          else                          p.Right  ??= item.Curve;
        }
      }
    }
    else
    {
      // Generic fallback for unexpected segment counts
      var byY = items.OrderByDescending(i => i.Mid.Y).ToList();
      p.Top    = byY[0].Curve;
      p.Bottom = byY[^1].Curve;
      var mid  = byY.Skip(1).Take(byY.Count - 2).OrderBy(i => i.Mid.X).ToList();
      p.Left   = mid.Count > 0 ? mid[0].Curve   : null;
      p.Right  = mid.Count > 1 ? mid[^1].Curve  : null;
    }

    return p;
  }

  /// <summary>Returns the candidate whose midpoint is closest to <paramref name="reference"/>'s midpoint.</summary>
  /// Returns the ID of an existing doc curve on <paramref name="layerIdx"/> whose midpoint
  /// is within <c>tol * 50</c> of <paramref name="target"/>'s midpoint (i.e. the same segment
  /// already exists from a previous run).  Adds a new curve and returns its ID if not found.
  private static Guid FindOrAddCurve(RhinoDoc doc, Curve target, int layerIdx,
                                     ObjectAttributes attr, HashSet<Guid> excludeIds, double tol)
  {
    var mid       = target.PointAtNormalizedLength(0.5);
    var threshold = tol * 50.0;  // generous but well inside SeamAllowance (0.5")
    foreach (var ro in doc.Objects.GetObjectList(ObjectType.Curve))
    {
      if (ro == null || excludeIds.Contains(ro.Id)) continue;
      if (ro.Attributes.LayerIndex != layerIdx) continue;
      var c = ro.Geometry as Curve;
      if (c == null) continue;
      if (c.IsClosed != target.IsClosed) continue;  // open segment must not match closed curve
      c.ClosestPoint(mid, out var t);
      if (mid.DistanceTo(c.PointAt(t)) < threshold)
        return ro.Id;  // reuse existing segment
    }
    return doc.Objects.AddCurve(target, attr);
  }

  private static Curve? ClosestOf(Curve reference, params Curve?[] candidates)
  {
    var mid = reference.PointAtNormalizedLength(0.5);
    return candidates
           .Where(c => c != null)
           .Select(c => c!)
           .OrderBy(c => { c.ClosestPoint(mid, out var t); return mid.DistanceTo(c.PointAt(t)); })
           .FirstOrDefault();
  }

  // ── Interior object collection ──────────────────────────────────────────────

  private static List<(GeometryBase Geom, ObjectAttributes Attr)> CollectInsideObjects(
    RhinoDoc doc, HashSet<Guid> excludeIds, Curve perimeter, Plane plane, double tol)
  {
    var result   = new List<(GeometryBase, ObjectAttributes)>();
    var boundary = new List<Curve> { perimeter };
    int nScanned = 0, nExcluded = 0, nOutside = 0;

    var settings = new ObjectEnumeratorSettings
    {
      ObjectTypeFilter = ObjectType.AnyObject,
      VisibleFilter    = true,
      DeletedObjects   = false,
      IncludeGrips     = false
    };

    foreach (var obj in doc.Objects.GetObjectList(settings))
    {
      nScanned++;
      if (obj.ObjectType == ObjectType.Grip) continue;
      if (excludeIds.Contains(obj.Id)) { nExcluded++; continue; }
      var geom = obj.Geometry;
      if (geom == null) continue;

      var attr = obj.Attributes.Duplicate();

      if (geom is Curve crv)
      {
        var splitParams = new SortedSet<double>();
        var events = Intersection.CurveCurve(crv, perimeter, tol, tol);
        if (events != null)
          foreach (var ev in events)
          {
            if (ev.IsOverlap) { splitParams.Add(ev.OverlapA.T0); splitParams.Add(ev.OverlapA.T1); }
            else               splitParams.Add(ev.ParameterA);
          }

        if (splitParams.Count == 0)
        {
          var inside = IsInsideOrOn(crv.PointAtNormalizedLength(0.5), boundary, plane, tol);
          L($"  CollectInside: crv id={obj.Id}  splitParams=0  inside={inside}  mid={crv.PointAtNormalizedLength(0.5)}");
          if (inside)
            result.Add((crv.DuplicateCurve(), attr));
          else
            nOutside++;
        }
        else
        {
          var segments = crv.Split(splitParams);
          if (segments == null) continue;
          foreach (var seg in segments)
          {
            if (seg.GetLength() < tol) continue;
            var inside = IsInsideOrOn(seg.PointAtNormalizedLength(0.5), boundary, plane, tol);
            L($"  CollectInside: crv id={obj.Id}  seg mid={seg.PointAtNormalizedLength(0.5)}  inside={inside}");
            if (inside)
              result.Add((seg, attr));
            else
              nOutside++;
          }
        }
      }
      else
      {
        var testPt = RepresentativePoint(geom);
        var inside = testPt.IsValid && IsInsideOrOn(testPt, boundary, plane, tol);
        L($"  CollectInside: other id={obj.Id}  type={obj.ObjectType}  inside={inside}  pt={testPt}");
        if (inside)
          result.Add((geom.Duplicate()!, attr));
        else
          nOutside++;
      }
    }

    L($"CollectInsideObjects done: scanned={nScanned}  excluded={nExcluded}  outside={nOutside}  collected={result.Count}");
    return result;
  }

  private static Point3d RepresentativePoint(GeometryBase geom)
  {
    switch (geom)
    {
      case TextEntity te:              return te.Plane.Origin;
      case TextDot td:                 return td.Point;
      case Rhino.Geometry.Point pt:    return pt.Location;
      default:
        var bb = geom.GetBoundingBox(true);
        return bb.IsValid ? bb.Center : Point3d.Unset;
    }
  }

  private static bool IsInsideOrOn(Point3d pt, List<Curve> closed, Plane plane, double tol)
  {
    if (!pt.IsValid) return false;
    foreach (var c in closed)
    {
      var r = c.Contains(pt, plane, tol);
      if (r == PointContainment.Inside || r == PointContainment.Coincident)
        return true;
    }
    return false;
  }

  private static Guid AddObjectToDoc(RhinoDoc doc, GeometryBase geom, ObjectAttributes attr)
  {
    switch (geom)
    {
      case Curve c:                       return doc.Objects.AddCurve(c, attr);
      case TextEntity te:                 return doc.Objects.AddText(te, attr);
      case TextDot td:                    return doc.Objects.AddTextDot(td, attr);
      case Rhino.Geometry.Point pt:       return doc.Objects.AddPoint(pt.Location, attr);
      case Hatch h:                       return doc.Objects.AddHatch(h, attr);
      case Brep b:                        return doc.Objects.AddBrep(b, attr);
      case Mesh m:                        return doc.Objects.AddMesh(m, attr);
      default:                            return doc.Objects.Add(geom, attr);
    }
  }

  // Sets a per-object display color on the doc object corresponding to the given
  // Curve reference within seamSegs / seamDocIds parallel lists.
  private static void SetDocObjectColor(RhinoDoc doc, List<Curve> segs, List<Guid> ids, Curve? c, Color color)
  {
    if (c == null) return;
    var idx = segs.FindIndex(s => ReferenceEquals(s, c));
    if (idx < 0 || idx >= ids.Count || ids[idx] == Guid.Empty) return;
    var obj = doc.Objects.FindId(ids[idx]);
    if (obj == null) return;
    obj.Attributes.ColorSource = ObjectColorSource.ColorFromObject;
    obj.Attributes.ObjectColor = color;
    obj.CommitChanges();
  }

  // Trims a seam side curve to the vertical range of the pocket:
  // from the endpoint adjacent to adjSeam down to the zipper intersection.
  private static Curve? TrimToPocketHeight(Curve seamSide, Curve adjSeam, Curve zipper,
                                            double extLen, double tol)
  {
    // Pocket top: endpoint of seamSide closest to EITHER endpoint of adjSeam
    // (they share a corner; comparing to adjSeam midpoint gave the wrong end when curve is long)
    var adjP0  = adjSeam.PointAtStart;
    var adjP1  = adjSeam.PointAtEnd;
    var dStart = Math.Min(seamSide.PointAtStart.DistanceTo(adjP0), seamSide.PointAtStart.DistanceTo(adjP1));
    var dEnd   = Math.Min(seamSide.PointAtEnd.DistanceTo(adjP0),   seamSide.PointAtEnd.DistanceTo(adjP1));
    var tTop   = dStart < dEnd ? seamSide.Domain.T0 : seamSide.Domain.T1;
    L($"  TrimToPocketHeight: seamStart={seamSide.PointAtStart}  seamEnd={seamSide.PointAtEnd}");
    L($"  adjP0={adjP0}  adjP1={adjP1}  dStart={dStart:F3}  dEnd={dEnd:F3}  tTop={tTop:F4} (={seamSide.PointAt(tTop)})");

    // Pocket bottom: intersection of extended seamSide with extended zipper
    var seamExt = seamSide.Extend(CurveEnd.Both, extLen, CurveExtensionStyle.Line) ?? seamSide.DuplicateCurve();
    var zipExt  = zipper.Extend(CurveEnd.Both, extLen, CurveExtensionStyle.Line)   ?? zipper.DuplicateCurve();
    var xEvents = Intersection.CurveCurve(seamExt, zipExt, tol, tol);
    L($"  xEvents={(xEvents?.Count ?? 0)}  zipMid={zipper.PointAt(zipper.Domain.Mid)}");

    double tBot;
    if (xEvents != null && xEvents.Count > 0)
    {
      var xPt = xEvents[0].PointA;
      L($"  intersection xPt={xPt}");
      if (!seamSide.ClosestPoint(xPt, out tBot)) { L($"  ClosestPoint on seamSide failed — return null"); return null; }
    }
    else
    {
      // Fallback: project zipper midpoint onto seamSide
      L($"  no intersection — fallback: project zipper mid onto seamSide");
      if (!seamSide.ClosestPoint(zipper.PointAt(zipper.Domain.Mid), out tBot)) { L($"  fallback ClosestPoint failed — return null"); return null; }
    }

    L($"  tBot={tBot:F4} (={seamSide.PointAt(tBot)})");
    if (Math.Abs(tTop - tBot) < RhinoMath.ZeroTolerance) { L($"  tTop==tBot — return null"); return null; }
    var trimmed = seamSide.Trim(Math.Min(tTop, tBot), Math.Max(tTop, tBot));
    L($"  trimmed={(trimmed != null ? $"len={trimmed.GetLength():F2}" : "null")}");
    return trimmed;
  }
}
