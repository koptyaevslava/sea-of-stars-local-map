using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
#if !STATIC_MAP_TESTS
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Attributes;
using Sabotage.Localization;
using UnityEngine;
using Object = UnityEngine.Object;
#endif

namespace SeaOfStarsLocalMap;

// This metadata is also compiled by the pure managed tests. Axis vectors deliberately
// retain their authored scale: normalizing a sheared render basis misplaces elevated floors.
public sealed class StaticMapMetadata
{
    public int SchemaVersion { get; set; }
    public string LevelGuid { get; set; }
    public string MapId { get; set; }
    public string FloorId { get; set; }
    public string Title { get; set; } = "Local Map";
    public string BaseVersion { get; set; }
    public string Image { get; set; } = "base.png";
    public string ImageSha256 { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public StaticMapBounds Bounds { get; set; }
    public double[] AxisX { get; set; }
    public double[] AxisY { get; set; }
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
    public double MinimapWorldWidth { get; set; } = 45d;
    public double RevealRadius { get; set; } = 9d;
    public double RevealSoftEdge { get; set; } = 2d;
    public double? FloorMinWorldY { get; set; }
    public double? FloorMaxWorldY { get; set; }

    public MapWorldRect WorldBounds => new MapWorldRect(Bounds.MinX, Bounds.MinY, Bounds.MaxX, Bounds.MaxY);

    public static StaticMapMetadata Parse(string json, string expectedLevelGuid)
    {
        var value = JsonSerializer.Deserialize<StaticMapMetadata>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (value == null) throw new InvalidDataException("Empty map metadata.");
        value.Validate(expectedLevelGuid);
        return value;
    }

    public void Validate(string expectedLevelGuid)
    {
        if (SchemaVersion != 1 || LevelGuid != expectedLevelGuid || !IsGuid(LevelGuid)) throw new InvalidDataException("Map version or level identity does not match.");
        if (string.IsNullOrWhiteSpace(MapId) || string.IsNullOrWhiteSpace(FloorId) || string.IsNullOrWhiteSpace(BaseVersion) || MapId.Length > 128 || FloorId.Length > 128 || BaseVersion.Length > 128)
            throw new InvalidDataException("Map, floor and base version identities are required.");
        if (Image != "base.png") throw new InvalidDataException("Map image must be the adjacent base.png.");
        if (ImageSha256 == null || ImageSha256.Length != 64) throw new InvalidDataException("Map image checksum is required.");
        try { Convert.FromHexString(ImageSha256); } catch (Exception e) { throw new InvalidDataException("Invalid map image checksum.", e); }
        if (Width <= 0 || Height <= 0 || Width > 8192 || Height > 8192 || (long)Width * Height > 67108864)
            throw new InvalidDataException("Map image dimensions exceed the supported limit.");
        if (Bounds == null || !WorldBounds.IsValid) throw new InvalidDataException("Map bounds are invalid.");
        if (!ValidAxis(AxisX) || !ValidAxis(AxisY) || !double.IsFinite(OffsetX) || !double.IsFinite(OffsetY)) throw new InvalidDataException("Map projection is invalid.");
        double crossX = AxisX[1] * AxisY[2] - AxisX[2] * AxisY[1];
        double crossY = AxisX[2] * AxisY[0] - AxisX[0] * AxisY[2];
        double crossZ = AxisX[0] * AxisY[1] - AxisX[1] * AxisY[0];
        if (crossX * crossX + crossY * crossY + crossZ * crossZ < 1e-12) throw new InvalidDataException("Map axes must be independent.");
        if (!double.IsFinite(MinimapWorldWidth) || MinimapWorldWidth <= 0d || !double.IsFinite(RevealRadius) || RevealRadius <= 0d || !double.IsFinite(RevealSoftEdge) || RevealSoftEdge < 0d)
            throw new InvalidDataException("Map scale or reveal radius is invalid.");
        if ((FloorMinWorldY.HasValue && !double.IsFinite(FloorMinWorldY.Value)) || (FloorMaxWorldY.HasValue && !double.IsFinite(FloorMaxWorldY.Value)) ||
            (FloorMinWorldY.HasValue && FloorMaxWorldY.HasValue && FloorMinWorldY.Value >= FloorMaxWorldY.Value)) throw new InvalidDataException("Floor height interval is invalid.");
        if (string.IsNullOrWhiteSpace(Title)) Title = "Local Map";
        if (Title.Length > 80) Title = Title.Substring(0, 80);
    }

    public MapWorldPoint Project(double x, double y, double z) => new MapWorldPoint(
        AxisX[0] * x + AxisX[1] * y + AxisX[2] * z + OffsetX,
        AxisY[0] * x + AxisY[1] * y + AxisY[2] * z + OffsetY);
    public bool IncludesHeight(double y) => (!FloorMinWorldY.HasValue || y >= FloorMinWorldY.Value) && (!FloorMaxWorldY.HasValue || y < FloorMaxWorldY.Value);
    public static bool IsGuid(string value) => value != null && value.Length == 32 && Guid.TryParseExact(value, "N", out _);
    private static bool ValidAxis(double[] axis)
    {
        if (axis == null || axis.Length != 3) return false;
        double squared = 0;
        foreach (double component in axis) { if (!double.IsFinite(component) || Math.Abs(component) > 1000) return false; squared += component * component; }
        return squared > 1e-12;
    }
}

public sealed class StaticMapBounds
{
    public double MinX { get; set; }
    public double MinY { get; set; }
    public double MaxX { get; set; }
    public double MaxY { get; set; }
}

// A higher-resolution render changes only the displayed pixels. The original
// map.json/base.png pair remains the identity of saved exploration.
public sealed class StaticMapImageVariant
{
    public int SchemaVersion { get; set; }
    public string BaseImageSha256 { get; set; }
    public string Image { get; set; }
    public string ImageSha256 { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public StaticMapBounds Bounds { get; set; }
    public double[] AxisX { get; set; }
    public double[] AxisY { get; set; }
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }

    public static StaticMapImageVariant Parse(string json, StaticMapMetadata original)
    {
        var value = JsonSerializer.Deserialize<StaticMapImageVariant>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (value == null || value.SchemaVersion != 1 || value.Image != "base-hd.png" ||
            !string.Equals(value.BaseImageSha256, original.ImageSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("HD render does not reference this map base.");
        if (value.ImageSha256 == null || value.ImageSha256.Length != 64) throw new InvalidDataException("HD checksum is required.");
        try { Convert.FromHexString(value.ImageSha256); } catch (Exception e) { throw new InvalidDataException("Invalid HD checksum.", e); }
        if (value.Width < original.Width || value.Height < original.Height || value.Width > 8192 || value.Height > 8192 ||
            Math.Abs((long)value.Width * original.Height - (long)value.Height * original.Width) > original.Width + original.Height)
            throw new InvalidDataException("HD render dimensions or aspect ratio do not match the map.");
        if (value.Bounds == null || value.Bounds.MinX != original.Bounds.MinX || value.Bounds.MinY != original.Bounds.MinY ||
            value.Bounds.MaxX != original.Bounds.MaxX || value.Bounds.MaxY != original.Bounds.MaxY ||
            !SameAxis(value.AxisX, original.AxisX) || !SameAxis(value.AxisY, original.AxisY) ||
            value.OffsetX != original.OffsetX || value.OffsetY != original.OffsetY)
            throw new InvalidDataException("HD render changes the map projection or bounds.");
        return value;
    }
    private static bool SameAxis(double[] first, double[] second) => first != null && first.Length == 3 &&
        first[0] == second[0] && first[1] == second[1] && first[2] == second[2];
}

public sealed class StaticMapTileInfo
{
    public string File { get; set; }
    public string Sha256 { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

/// <summary>A sparse 5x atlas split into GPU-safe tiles and tied to the verified base image.</summary>
public sealed class StaticMapTileManifest
{
    public int SchemaVersion { get; set; }
    public string BaseImageSha256 { get; set; }
    public int Scale { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int TileSize { get; set; }
    public StaticMapBounds Bounds { get; set; }
    public double[] AxisX { get; set; }
    public double[] AxisY { get; set; }
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
    public List<StaticMapTileInfo> Tiles { get; set; }

    public static StaticMapTileManifest Parse(string json, StaticMapMetadata original)
    {
        var value = JsonSerializer.Deserialize<StaticMapTileManifest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (value == null || value.SchemaVersion != 1 || value.Scale != 5 ||
            !string.Equals(value.BaseImageSha256, original.ImageSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("5x tiles do not reference this map base.");
        if (value.Width != original.Width * value.Scale || value.Height != original.Height * value.Scale ||
            value.TileSize < 256 || value.TileSize > 2048 || value.Tiles == null || value.Tiles.Count > 4096)
            throw new InvalidDataException("5x atlas dimensions or tile count are invalid.");
        if (value.Bounds == null || value.Bounds.MinX != original.Bounds.MinX || value.Bounds.MinY != original.Bounds.MinY ||
            value.Bounds.MaxX != original.Bounds.MaxX || value.Bounds.MaxY != original.Bounds.MaxY ||
            !SameAxis(value.AxisX, original.AxisX) || !SameAxis(value.AxisY, original.AxisY) ||
            value.OffsetX != original.OffsetX || value.OffsetY != original.OffsetY)
            throw new InvalidDataException("5x atlas changes the map projection or bounds.");
        var occupied = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tile in value.Tiles)
        {
            if (tile == null || string.IsNullOrWhiteSpace(tile.File) || tile.File.Length > 128 ||
                tile.File.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(tile.File) ||
                tile.Sha256 == null || tile.Sha256.Length != 64 || tile.X < 0 || tile.Y < 0 ||
                tile.Width <= 0 || tile.Height <= 0 || tile.Width > value.TileSize || tile.Height > value.TileSize ||
                tile.X + tile.Width > value.Width || tile.Y + tile.Height > value.Height ||
                !occupied.Add(tile.X + ":" + tile.Y))
                throw new InvalidDataException("5x tile entry is invalid.");
            try { Convert.FromHexString(tile.Sha256); } catch (Exception e) { throw new InvalidDataException("Invalid 5x tile checksum.", e); }
        }
        return value;
    }

    public MapWorldRect WorldRect(StaticMapTileInfo tile)
    {
        double worldWidth = Bounds.MaxX - Bounds.MinX, worldHeight = Bounds.MaxY - Bounds.MinY;
        double minX = Bounds.MinX + (double)tile.X / Width * worldWidth;
        double maxX = Bounds.MinX + (double)(tile.X + tile.Width) / Width * worldWidth;
        double maxY = Bounds.MaxY - (double)tile.Y / Height * worldHeight;
        double minY = Bounds.MaxY - (double)(tile.Y + tile.Height) / Height * worldHeight;
        return new MapWorldRect(minX, minY, maxX, maxY);
    }

    private static bool SameAxis(double[] first, double[] second) => first != null && second != null &&
        first.Length == 3 && second.Length == 3 && first[0] == second[0] && first[1] == second[1] && first[2] == second[2];
}

#if !STATIC_MAP_TESTS
[BepInPlugin("local.seaofstars.localmap", "Local Map", "0.9.3")]
public sealed class StaticMapPlugin : BasePlugin
{
    internal static ManualLogSource Logger;
    internal static ConfigEntry<bool> ShowMini;
    public override void Load()
    {
        Logger = Log;
        ShowMini = Config.Bind("StaticMap", "ShowMinimap", true, "Show the round minimap while exploring a supported location.");
        AddComponent<StaticMapOverlay>();
        new Harmony("local.seaofstars.localmap.nativeui").PatchAll(typeof(StaticMapPlugin).Assembly);
        Log.LogInfo("Static Local Map 0.9.3 test build ready with memory-budgeted detail tiles and performance diagnostics.");
    }
}

public sealed class StaticMapOverlay : MonoBehaviour
{
    private struct TilePerfSample
    {
        public string File;
        public int EncodedBytes, DecodedBytes;
        public double ReadMs, HashMs, DecodeUploadMs, TotalMs;
        public int Gc0, Gc1, Gc2;
    }

    public StaticMapOverlay(IntPtr pointer) : base(pointer) { }
    private StaticMapMetadata metadata;
    private StaticMapTileManifest tileManifest;
    private StaticFog fog;
    private Texture2D baseTexture, fogTexture;
    private string mapFolder = "", detailTileSetKey = "";
    private readonly Dictionary<string, Texture2D> detailTextures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> detailLastUse = new(StringComparer.Ordinal);
    private readonly List<StaticMapTileInfo> detailDesiredTiles = new();
    private readonly List<StaticMapTileInfo> detailScratchTiles = new();
    private readonly List<MapTextureTile> detailTileValues = new();
    private readonly HashSet<string> detailKeepFiles = new(StringComparer.Ordinal);
    private string detailDesiredKey = "";
    private float nextDetailSelection;
    private int detailUseCounter;
    private long detailResidentBytes;
    private const long MaxResidentDetailBytes = 192L * 1024L * 1024L;
    private const int MaxVisibleDetailTiles = 512;
    private Color32[] fogColors, fogUploadColors;
    private readonly List<MapLandmark> landmarks = new();
    internal static StaticMapOverlay Current;
    private MapCanvasView canvas;
    private NativeMapAssets assets;
    private NativeMapHud hudLease;
    private bool firstFullOpen = true, footerInitialized, footerController;
    private ELanguage footerLanguage = (ELanguage)(-1);
    private string identity = "", fogPath = "";
    private Vector3 playerWorld, lookDirection, lastRevealWorld;
    private MapWorldPoint playerMap, lastRevealMap, fullCenter;
    private double fullZoom = 1d;
    private bool available, fullMap, hasRevealPoint, dirty, loggedError, pendingRelease;
    private float nextPoll, nextReveal, nextSave, lastRevealTime;
    private float nextLandmarkScan;
    private int landmarkScansRemaining;
    private float hudDiagnosticAt = -1f;
    private int releaseFrame;
    private string playerMarkerName = "";
    private PauseManager pauseOwner;
    private UIManager uiOwner;
    private bool ownsPause, ownsPauseDisable, ownsMenuDisable, ownsHudDisable;
    private CanvasUpscaleViewport postUpscaleOwner;
    private Vector2 savedCanvasPos, savedCanvasSize;
    private bool savedUseCustomCanvasSize, postUpscaleActive;
    private int postUpscaleWidth, postUpscaleHeight;
    private readonly TilePerfSample[] perfTiles = new TilePerfSample[64];
    private bool perfActive;
    private int perfSession, perfFrames, perfFrame16, perfFrame25, perfFrame33, perfTileCount;
    private int perfSelections, perfSelectionMaxTiles, perfUiRebuilds, perfUiMaxTiles, perfEvictions;
    private int perfGc0Start, perfGc1Start, perfGc2Start, perfWorstFrame;
    private long perfManagedStart;
    private double perfFrameTotalMs, perfFrameMaxMs, perfLateTotalMs, perfLateMaxMs;
    private double perfSelectionTotalMs, perfSelectionMaxMs, perfRefreshTotalMs, perfRefreshMaxMs;
    private double perfUiTotalMs, perfUiMaxMs, perfViewTotalMs, perfViewMaxMs, perfEvictTotalMs, perfEvictMaxMs;

    public void Awake() { Current = this; }
    public void OnEnable() { Current = this; }

    private static long PerfNow() => System.Diagnostics.Stopwatch.GetTimestamp();
    private static double PerfMs(long started) =>
        (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000d / System.Diagnostics.Stopwatch.Frequency;

    [HideFromIl2Cpp]
    private void BeginPerfSession()
    {
        perfSession++;
        perfActive = true;
        perfFrames = perfFrame16 = perfFrame25 = perfFrame33 = perfTileCount = 0;
        perfSelections = perfSelectionMaxTiles = perfUiRebuilds = perfUiMaxTiles = perfEvictions = 0;
        perfWorstFrame = 0;
        perfFrameTotalMs = perfFrameMaxMs = perfLateTotalMs = perfLateMaxMs = 0d;
        perfSelectionTotalMs = perfSelectionMaxMs = perfRefreshTotalMs = perfRefreshMaxMs = 0d;
        perfUiTotalMs = perfUiMaxMs = perfViewTotalMs = perfViewMaxMs = perfEvictTotalMs = perfEvictMaxMs = 0d;
        perfManagedStart = GC.GetTotalMemory(false);
        perfGc0Start = GC.CollectionCount(0); perfGc1Start = GC.CollectionCount(1); perfGc2Start = GC.CollectionCount(2);
        Array.Clear(perfTiles, 0, perfTiles.Length);
    }

    [HideFromIl2Cpp]
    private void RecordPerfFrame(double lateMs)
    {
        if (!perfActive) return;
        double frameMs = Time.unscaledDeltaTime * 1000d;
        perfFrames++; perfFrameTotalMs += frameMs; perfLateTotalMs += lateMs;
        if (frameMs > perfFrameMaxMs) { perfFrameMaxMs = frameMs; perfWorstFrame = Time.frameCount; }
        if (lateMs > perfLateMaxMs) perfLateMaxMs = lateMs;
        if (frameMs >= 16.67d) perfFrame16++;
        if (frameMs >= 25d) perfFrame25++;
        if (frameMs >= 33.33d) perfFrame33++;
    }

    [HideFromIl2Cpp]
    private void LogPerfSession(string reason)
    {
        if (!perfActive) return;
        perfActive = false;
        long managedEnd = GC.GetTotalMemory(false);
        double tileRead = 0d, tileHash = 0d, tileDecode = 0d, tileTotal = 0d;
        for (int i = 0; i < perfTileCount; i++)
        {
            TilePerfSample sample = perfTiles[i];
            tileRead += sample.ReadMs; tileHash += sample.HashMs; tileDecode += sample.DecodeUploadMs; tileTotal += sample.TotalMs;
        }
        StaticMapPlugin.Logger.LogInfo(
            $"[LocalMap PERF] session={perfSession} reason={reason}; frames={perfFrames}, frame avg/max=" +
            $"{(perfFrames == 0 ? 0d : perfFrameTotalMs / perfFrames):F2}/{perfFrameMaxMs:F2} ms (worst frame {perfWorstFrame}), " +
            $">=16.7/25/33.3ms={perfFrame16}/{perfFrame25}/{perfFrame33}; LocalMap LateUpdate avg/max=" +
            $"{(perfFrames == 0 ? 0d : perfLateTotalMs / perfFrames):F3}/{perfLateMaxMs:F3} ms.");
        StaticMapPlugin.Logger.LogInfo(
            $"[LocalMap PERF] tileLoads={perfTileCount}; read/hash/LoadImage+GPU/total=" +
            $"{tileRead:F2}/{tileHash:F2}/{tileDecode:F2}/{tileTotal:F2} ms; selections={perfSelections}, " +
            $"selection avg/max={Avg(perfSelectionTotalMs, perfSelections):F3}/{perfSelectionMaxMs:F3} ms, maxVisible={perfSelectionMaxTiles}; " +
            $"refresh avg/max={Avg(perfRefreshTotalMs, perfFrames):F3}/{perfRefreshMaxMs:F3} ms.");
        StaticMapPlugin.Logger.LogInfo(
            $"[LocalMap PERF] UI rebuilds={perfUiRebuilds}, avg/max={Avg(perfUiTotalMs, perfUiRebuilds):F3}/{perfUiMaxMs:F3} ms, maxTiles={perfUiMaxTiles}; " +
            $"SetFullView avg/max={Avg(perfViewTotalMs, perfFrames):F3}/{perfViewMaxMs:F3} ms; evictions={perfEvictions}, " +
            $"total/max={perfEvictTotalMs:F3}/{perfEvictMaxMs:F3} ms; managedDelta={(managedEnd - perfManagedStart) / 1048576d:F2} MiB; " +
            $"GC0/1/2={GC.CollectionCount(0) - perfGc0Start}/{GC.CollectionCount(1) - perfGc1Start}/{GC.CollectionCount(2) - perfGc2Start}.");
        for (int i = 0; i < perfTileCount; i++)
        {
            TilePerfSample sample = perfTiles[i];
            StaticMapPlugin.Logger.LogInfo(
                $"[LocalMap PERF tile] {sample.File}; png={sample.EncodedBytes / 1024d:F1} KiB rgba={sample.DecodedBytes / 1048576d:F1} MiB; " +
                $"read={sample.ReadMs:F2} hash={sample.HashMs:F2} LoadImage+GPU={sample.DecodeUploadMs:F2} total={sample.TotalMs:F2} ms; " +
                $"GC={sample.Gc0}/{sample.Gc1}/{sample.Gc2}.");
        }
    }

    private static double Avg(double total, int count) => count == 0 ? 0d : total / count;

    public void Update()
    {
        try
        {
            var input = NativeMapInput.Read();
            if (pendingRelease && Time.frameCount > releaseFrame && !Input.GetKey(KeyCode.Escape) && !Input.GetKey(KeyCode.M) && !input.ToggleHeld && !input.CloseHeld) ReleaseControls();
            if (Time.unscaledTime >= nextPoll) { nextPoll = Time.unscaledTime + .25f; PollLocation(); }
            if (!available || metadata == null) { CloseFullMap(false); canvas?.Hide(); return; }
            var leader = PlayerPartyManager.Instance?.Leader;
            if (leader == null) { available = false; CloseFullMap(false); canvas?.Hide(); return; }
            playerWorld = leader.transform.position;
            lookDirection = leader.lookDirectionController?.CurrentLookDirection ?? Vector3.forward;
            playerMap = metadata.Project(playerWorld.x, playerWorld.y, playerWorld.z);
            if (landmarkScansRemaining > 0 && Time.unscaledTime >= nextLandmarkScan)
            {
                nextLandmarkScan = Time.unscaledTime + 1f;
                landmarkScansRemaining--;
                ScanLandmarks();
            }
            if (!metadata.IncludesHeight(playerWorld.y) || HiddenByGame())
            {
                hasRevealPoint = false;
                CloseFullMap(false);
                canvas?.Hide();
                return;
            }
            EnsureCanvas();
            if (canvas == null) return;
            MaintainNativeResolutionOverlay();
            UpdatePlayerMarker(leader.gameObject != null ? leader.gameObject.name : leader.name);
            if (Input.GetKeyDown(KeyCode.M) || input.ToggleRequested)
            {
                if (fullMap) CloseFullMap(true, "toggle");
                else if (!pendingRelease)
                {
                    bool keyboard = Input.GetKeyDown(KeyCode.M);
                    if (keyboard) NativeMapInput.ResetPlayer();
                    UpdateFooter(!keyboard && input.ControllerActive);
                    OpenFullMap();
                }
            }
            if (fullMap)
            {
                // Preserve the current footer mode until the player actually uses the
                // other device. Last-used-controller polling can fluctuate while paused.
                UpdateFooter(footerInitialized && footerController);
                if (hudLease?.KeepAlive() != true) { CloseFullMap(false, "native HUD no longer available"); return; }
                HandleFullMapInput(input);
                return;
            }
            if (pendingRelease) return;
            if (Time.unscaledTime >= nextReveal)
            {
                nextReveal = Time.unscaledTime + .12f;
                long revision = fog.Revision;
                bool continuous = hasRevealPoint && Time.unscaledTime - lastRevealTime < 1f && (playerWorld - lastRevealWorld).sqrMagnitude < 400f;
                if (continuous) fog.RevealSegment(lastRevealMap, playerMap, metadata.RevealRadius, metadata.RevealSoftEdge);
                else fog.RevealDisc(playerMap, metadata.RevealRadius, metadata.RevealSoftEdge);
                lastRevealMap = playerMap; lastRevealWorld = playerWorld; lastRevealTime = Time.unscaledTime; hasRevealPoint = true;
                if (fog.Revision != revision) { UploadFog(); dirty = true; }
            }
            UpdateLandmarkDiscovery();
            if (dirty && Time.unscaledTime >= nextSave) { SaveFog(); nextSave = Time.unscaledTime + 8f; }
        }
        catch (Exception error)
        {
            CloseFullMap(false); canvas?.Hide();
            if (!loggedError) { StaticMapPlugin.Logger.LogError(error); loggedError = true; }
        }
    }

    [HideFromIl2Cpp]
    private void PollLocation()
    {
        var level = LevelManager.Instance;
        string guid = level != null && !level.LoadingLevel ? level.CurrentLevel.levelDefinitionGuid : "";
        string saveId = SaveManager.Instance?.LoadedSaveGameSlot?.saveId;
        bool valid = StaticMapMetadata.IsGuid(guid) && !string.IsNullOrWhiteSpace(saveId) && PlayerPartyManager.Instance?.Leader != null;
        string nextIdentity = valid ? saveId + "|" + guid : "";
        if (nextIdentity != identity)
        {
            CloseFullMap(false); SaveFog(); ClearMap(); NativeMapInput.ResetPlayer(); identity = nextIdentity; loggedError = false;
            if (valid) LoadMap(guid, saveId);
        }
        available = valid && metadata != null && baseTexture != null && fog != null;
    }

    [HideFromIl2Cpp]
    private void LoadMap(string guid, string saveId)
    {
        string folder = Path.Combine(Paths.PluginPath, "LocalMap", "Maps", guid);
        string jsonPath = Path.Combine(folder, "map.json");
        if (!File.Exists(jsonPath)) return;
        if (new FileInfo(jsonPath).Length > 65536) throw new InvalidDataException("Map metadata is too large.");
        string json = File.ReadAllText(jsonPath, Encoding.UTF8);
        var info = StaticMapMetadata.Parse(json, guid);
        string imagePath = Path.Combine(folder, info.Image);
        if (!File.Exists(imagePath) || new FileInfo(imagePath).Length > 134217728) throw new InvalidDataException("Missing or oversized base image.");
        byte[] imageBytes = File.ReadAllBytes(imagePath);
        string imageHash = Convert.ToHexString(SHA256.HashData(imageBytes));
        if (!string.Equals(imageHash, info.ImageSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Base image checksum does not match metadata.");
        StaticMapTileManifest tiles = TryLoadTileManifest(folder, info);
        Texture2D image = TryLoadHdImage(folder, info) ??
            DecodeMapImage(imageBytes, info.Width, info.Height);
        metadata = info; baseTexture = image; tileManifest = tiles; mapFolder = folder;
        detailTileSetKey = detailDesiredKey = ""; nextDetailSelection = 0f;
        detailDesiredTiles.Clear(); detailScratchTiles.Clear(); detailTileValues.Clear();
        int fogWidth = Math.Min(768, info.Width);
        int fogHeight = Math.Max(1, (int)Math.Round((double)info.Height * fogWidth / info.Width));
        if (fogHeight > 768) { fogWidth = Math.Max(1, (int)Math.Round((double)fogWidth * 768 / fogHeight)); fogHeight = 768; }
        string baseIdentity = guid + "|" + info.MapId + "|" + info.FloorId + "|" + Hash(json) + "|" + imageHash;
        fog = new StaticFog(fogWidth, fogHeight, info.WorldBounds, baseIdentity, 255);
        fogPath = Path.Combine(Paths.ConfigPath, "LocalMap-v3", Hash(saveId), Hash(baseIdentity) + ".fog");
        if (File.Exists(fogPath))
        {
            try
            {
                if (new FileInfo(fogPath).Length > 4196608) throw new InvalidDataException("Fog save exceeds its size limit.");
                fog.Load(File.ReadAllBytes(fogPath));
            }
            catch (Exception error) { StaticMapPlugin.Logger.LogWarning("Static fog restore rejected: " + error.Message); }
        }
        fogTexture = new Texture2D(fogWidth, fogHeight, TextureFormat.RGBA32, false);
        fogTexture.filterMode = FilterMode.Bilinear; fogTexture.wrapMode = TextureWrapMode.Clamp;
        fogColors = new Color32[fogWidth * fogHeight];
        UploadFog(); fullCenter = info.WorldBounds.Center; fullZoom = 1; firstFullOpen = true; hasRevealPoint = false;
        landmarkScansRemaining = 4; nextLandmarkScan = 0f;
        ScanLandmarks();
        canvas?.SetTextures(baseTexture, fogTexture, info.WorldBounds);
        canvas?.SetLandmarks(landmarks);
        nextSave = Time.unscaledTime + 8f;
        StaticMapPlugin.Logger.LogInfo($"Static map loaded: {info.MapId}/{info.FloorId}, base={baseTexture.width}x{baseTexture.height}, 5xTiles={tileManifest?.Tiles.Count ?? 0}, fog={fogWidth}x{fogHeight}, projection Y=({info.AxisY[0]},{info.AxisY[1]},{info.AxisY[2]}).");
        var position = PlayerPartyManager.Instance.Leader.transform.position;
        MapWorldPoint projected = info.Project(position.x, position.y, position.z);
        StaticMapPlugin.Logger.LogInfo($"Static map alignment: world=({position.x:F3},{position.y:F3},{position.z:F3}), map=({projected.X:F3},{projected.Y:F3}), bounds=({info.Bounds.MinX:F2},{info.Bounds.MinY:F2})..({info.Bounds.MaxX:F2},{info.Bounds.MaxY:F2}).");
    }

    [HideFromIl2Cpp]
    private static Texture2D TryLoadHdImage(string folder, StaticMapMetadata original)
    {
        string variantPath = Path.Combine(folder, "image-hd.json");
        if (!File.Exists(variantPath)) return null;
        try
        {
            if (new FileInfo(variantPath).Length > 65536) throw new InvalidDataException("HD metadata is too large.");
            var variant = StaticMapImageVariant.Parse(File.ReadAllText(variantPath, Encoding.UTF8), original);
            if (variant.Width > SystemInfo.maxTextureSize || variant.Height > SystemInfo.maxTextureSize)
                throw new InvalidDataException("HD dimensions exceed the GPU texture limit.");
            string imagePath = Path.Combine(folder, variant.Image);
            if (!File.Exists(imagePath) || new FileInfo(imagePath).Length > 134217728) throw new InvalidDataException("Missing or oversized HD image.");
            byte[] bytes = File.ReadAllBytes(imagePath);
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), variant.ImageSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("HD image checksum does not match metadata.");
            return DecodeMapImage(bytes, variant.Width, variant.Height);
        }
        catch (Exception error)
        {
            StaticMapPlugin.Logger.LogWarning("Using original map image: " + error.Message);
            return null;
        }
    }

    [HideFromIl2Cpp]
    private static StaticMapTileManifest TryLoadTileManifest(string folder, StaticMapMetadata original)
    {
        string path = Path.Combine(folder, "tiles-5x.json");
        if (!File.Exists(path)) return null;
        try
        {
            if (new FileInfo(path).Length > 1048576) throw new InvalidDataException("5x tile manifest is too large.");
            return StaticMapTileManifest.Parse(File.ReadAllText(path, Encoding.UTF8), original);
        }
        catch (Exception error)
        {
            StaticMapPlugin.Logger.LogWarning("Ignoring 5x tile atlas: " + error.Message);
            return null;
        }
    }

    [HideFromIl2Cpp]
    private static Texture2D DecodeMapImage(byte[] bytes, int width, int height)
    {
        var image = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        try
        {
            if (!ImageConversion.LoadImage(image, bytes, true) || image.width != width || image.height != height)
                throw new InvalidDataException("Decoded map dimensions do not match metadata.");
            image.filterMode = FilterMode.Bilinear; image.wrapMode = TextureWrapMode.Clamp;
            return image;
        }
        catch { Object.Destroy(image); throw; }
    }

    [HideFromIl2Cpp]
    private void RefreshDetailTiles(MapWorldRect view)
    {
        if (canvas == null) return;
        if (tileManifest == null || string.IsNullOrEmpty(mapFolder))
        {
            if (detailTileSetKey.Length != 0) { canvas.SetDetailTiles(Array.Empty<MapTextureTile>()); detailTileSetKey = ""; }
            detailDesiredKey = ""; detailDesiredTiles.Clear();
            return;
        }

        // The minimap view moves every frame, but its tile set changes only at tile
        // boundaries. Re-evaluate at 10 Hz with reusable lists instead of allocating
        // a list and joined key every rendered frame.
        if (Time.unscaledTime >= nextDetailSelection || detailDesiredKey.Length == 0)
        {
            long selectionStarted = PerfNow();
            nextDetailSelection = Time.unscaledTime + .1f;
            detailScratchTiles.Clear();
            foreach (var tile in tileManifest.Tiles)
            {
                MapWorldRect bounds = tileManifest.WorldRect(tile);
                if (bounds.MaxX > view.MinX && bounds.MinX < view.MaxX && bounds.MaxY > view.MinY && bounds.MinY < view.MaxY)
                    detailScratchTiles.Add(tile);
            }
            detailScratchTiles.Sort((a, b) => a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X));
            long selectedBytes = 0;
            foreach (var tile in detailScratchTiles) selectedBytes += (long)tile.Width * tile.Height * 4L;
            string selectedKey = detailScratchTiles.Count == 0 ? "empty" :
                detailScratchTiles.Count > MaxVisibleDetailTiles || selectedBytes > MaxResidentDetailBytes ? "overview" :
                string.Join("|", detailScratchTiles.ConvertAll(tile => tile.File));
            if (!string.Equals(selectedKey, detailDesiredKey, StringComparison.Ordinal))
            {
                detailDesiredKey = selectedKey;
                detailDesiredTiles.Clear();
                if (selectedKey != "empty" && selectedKey != "overview") detailDesiredTiles.AddRange(detailScratchTiles);
            }
            if (perfActive)
            {
                double elapsed = PerfMs(selectionStarted);
                perfSelections++; perfSelectionTotalMs += elapsed;
                if (elapsed > perfSelectionMaxMs) perfSelectionMaxMs = elapsed;
                if (detailScratchTiles.Count > perfSelectionMaxTiles) perfSelectionMaxTiles = detailScratchTiles.Count;
            }
        }

        // At overview scale the verified base image already has more source pixels
        // than the screen can display. Avoid decoding an entire 1.6 GB RGBA atlas.
        if (detailDesiredTiles.Count == 0)
        {
            if (detailTileSetKey != detailDesiredKey)
            {
                long uiStarted = PerfNow();
                canvas.SetDetailTiles(Array.Empty<MapTextureTile>());
                if (perfActive)
                {
                    double elapsed = PerfMs(uiStarted);
                    perfUiRebuilds++; perfUiTotalMs += elapsed;
                    if (elapsed > perfUiMaxMs) perfUiMaxMs = elapsed;
                }
                detailTileSetKey = detailDesiredKey;
            }
            if (detailTextures.Count > 0)
            {
                detailKeepFiles.Clear();
                EvictDetailTextures(detailKeepFiles);
            }
            return;
        }

        if (detailTileSetKey == detailDesiredKey)
        {
            foreach (var tile in detailDesiredTiles) detailLastUse[tile.File] = ++detailUseCounter;
            return;
        }

        // Decode at most one missing tile in a frame. The previous set stays visible
        // over the overview until the next set is complete, avoiding a multi-PNG stall.
        foreach (var tile in detailDesiredTiles)
            if (!detailTextures.TryGetValue(tile.File, out var cached) || cached == null)
            {
                LoadDetailTexture(tile);
                return;
            }

        detailKeepFiles.Clear();
        detailTileValues.Clear();
        foreach (var tile in detailDesiredTiles)
        {
            Texture2D texture = LoadDetailTexture(tile);
            detailKeepFiles.Add(tile.File);
            detailLastUse[tile.File] = ++detailUseCounter;
            detailTileValues.Add(new MapTextureTile(tile.File, texture, tileManifest.WorldRect(tile)));
        }
        long uiRebuildStarted = PerfNow();
        canvas.SetDetailTiles(detailTileValues);
        if (perfActive)
        {
            double elapsed = PerfMs(uiRebuildStarted);
            perfUiRebuilds++; perfUiTotalMs += elapsed;
            if (elapsed > perfUiMaxMs) perfUiMaxMs = elapsed;
            if (detailTileValues.Count > perfUiMaxTiles) perfUiMaxTiles = detailTileValues.Count;
        }
        detailTileSetKey = detailDesiredKey;
        EvictDetailTextures(detailKeepFiles);
    }

    [HideFromIl2Cpp]
    private void UseOverviewForMinimap()
    {
        // The clean HD overview already has far more pixels than the 184x184 logical
        // minimap can display. Loading 5x tiles while walking only adds decode stalls.
        if (detailDesiredKey == "minimap" && detailTileSetKey == "minimap") return;
        detailDesiredKey = "minimap";
        detailDesiredTiles.Clear();
        nextDetailSelection = 0f;
        if (detailTileSetKey != "minimap")
        {
            canvas.SetDetailTiles(Array.Empty<MapTextureTile>());
            detailTileSetKey = "minimap";
        }
    }

    [HideFromIl2Cpp]
    private Texture2D LoadDetailTexture(StaticMapTileInfo tile)
    {
        if (detailTextures.TryGetValue(tile.File, out var cached) && cached != null) return cached;
        long totalStarted = PerfNow();
        int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
        string root = Path.GetFullPath(mapFolder) + Path.DirectorySeparatorChar;
        string relative = tile.File.Replace('/', Path.DirectorySeparatorChar);
        string path = Path.GetFullPath(Path.Combine(mapFolder, relative));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("5x tile escapes the map folder.");
        if (!File.Exists(path) || new FileInfo(path).Length > 33554432) throw new InvalidDataException("Missing or oversized 5x tile: " + tile.File);
        long readStarted = PerfNow();
        byte[] bytes = File.ReadAllBytes(path);
        double readMs = PerfMs(readStarted);
        long hashStarted = PerfNow();
        string actualHash = Convert.ToHexString(SHA256.HashData(bytes));
        double hashMs = PerfMs(hashStarted);
        if (!string.Equals(actualHash, tile.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("5x tile checksum mismatch: " + tile.File);
        long decodeStarted = PerfNow();
        Texture2D texture = DecodeMapImage(bytes, tile.Width, tile.Height);
        double decodeMs = PerfMs(decodeStarted);
        texture.name = "LocalMap.5x." + tile.X + "." + tile.Y;
        detailTextures[tile.File] = texture;
        detailResidentBytes += (long)tile.Width * tile.Height * 4L;
        if (perfActive && perfTileCount < perfTiles.Length)
        {
            perfTiles[perfTileCount++] = new TilePerfSample {
                File = tile.File,
                EncodedBytes = bytes.Length,
                DecodedBytes = checked(tile.Width * tile.Height * 4),
                ReadMs = readMs,
                HashMs = hashMs,
                DecodeUploadMs = decodeMs,
                TotalMs = PerfMs(totalStarted),
                Gc0 = GC.CollectionCount(0) - gc0,
                Gc1 = GC.CollectionCount(1) - gc1,
                Gc2 = GC.CollectionCount(2) - gc2
            };
        }
        return texture;
    }

    [HideFromIl2Cpp]
    private void EvictDetailTextures(HashSet<string> keep)
    {
        long started = PerfNow();
        int removed = 0;
        while (detailResidentBytes > MaxResidentDetailBytes || (keep.Count == 0 && detailTextures.Count > 0))
        {
            string victim = null;
            int oldest = int.MaxValue;
            foreach (var pair in detailTextures)
            {
                if (keep.Contains(pair.Key)) continue;
                int used = detailLastUse.TryGetValue(pair.Key, out int value) ? value : 0;
                if (used < oldest) { oldest = used; victim = pair.Key; }
            }
            if (victim == null) break;
            Texture2D texture = detailTextures[victim];
            if (texture != null) detailResidentBytes -= (long)texture.width * texture.height * 4L;
            Object.Destroy(texture);
            detailTextures.Remove(victim);
            detailLastUse.Remove(victim);
            removed++;
        }
        if (perfActive && removed > 0)
        {
            double elapsed = PerfMs(started);
            perfEvictions += removed; perfEvictTotalMs += elapsed;
            if (elapsed > perfEvictMaxMs) perfEvictMaxMs = elapsed;
        }
    }

    [HideFromIl2Cpp]
    private void ScanLandmarks()
    {
        if (metadata == null || fog == null) return;
        var found = new List<MapLandmark>();
        try
        {
            foreach (var resource in Resources.FindObjectsOfTypeAll(Il2CppType.Of<CampingFireSpot>()))
            {
                var fire = resource.TryCast<CampingFireSpot>();
                if (fire == null || !IsLoadedSceneObject(fire.gameObject)) continue;
                Vector3 position = fire.fireVisual != null ? fire.fireVisual.transform.position : fire.transform.position;
                AddLandmark(found, MapLandmarkKind.Campfire, position);
            }
            foreach (var resource in Resources.FindObjectsOfTypeAll(Il2CppType.Of<Savepoint>()))
            {
                var savepoint = resource.TryCast<Savepoint>();
                if (savepoint == null || !IsLoadedSceneObject(savepoint.gameObject)) continue;
                AddLandmark(found, MapLandmarkKind.Savepoint, savepoint.transform.position);
            }
        }
        catch (Exception error)
        {
            StaticMapPlugin.Logger.LogWarning("Landmark scan failed: " + error.Message);
            return;
        }
        // Keep points already observed during this level even if story logic disables
        // their GameObject before a later scan. New scans only add late-loaded points.
        foreach (var previous in landmarks)
        {
            bool present = false;
            foreach (var current in found)
                if (current.Kind == previous.Kind && DistanceSquared(current.Position, previous.Position) < .25d)
                { current.Discovered |= previous.Discovered; present = true; break; }
            if (!present) found.Add(previous);
        }
        found.Sort((a, b) =>
        {
            int kind = a.Kind.CompareTo(b.Kind);
            if (kind != 0) return kind;
            int x = a.Position.X.CompareTo(b.Position.X);
            return x != 0 ? x : a.Position.Y.CompareTo(b.Position.Y);
        });
        bool changed = found.Count != landmarks.Count;
        if (!changed)
            for (int i = 0; i < found.Count; i++)
                if (found[i].Kind != landmarks[i].Kind || DistanceSquared(found[i].Position, landmarks[i].Position) > .01d)
                { changed = true; break; }
        if (!changed) return;
        landmarks.Clear(); landmarks.AddRange(found);
        UpdateLandmarkDiscovery();
        canvas?.SetLandmarks(landmarks);
        StaticMapPlugin.Logger.LogInfo($"Local map landmarks: {CountLandmarks(MapLandmarkKind.Campfire)} campfire(s), {CountLandmarks(MapLandmarkKind.Savepoint)} savepoint(s).");
    }

    [HideFromIl2Cpp]
    private void AddLandmark(List<MapLandmark> target, MapLandmarkKind kind, Vector3 world)
    {
        if (!metadata.IncludesHeight(world.y)) return;
        MapWorldPoint point = metadata.Project(world.x, world.y, world.z);
        if (!point.IsFinite || point.X < metadata.Bounds.MinX || point.X > metadata.Bounds.MaxX ||
            point.Y < metadata.Bounds.MinY || point.Y > metadata.Bounds.MaxY) return;
        foreach (var existing in target)
            if (existing.Kind == kind && DistanceSquared(existing.Position, point) < .25d) return;
        bool discovered = fog.IsRevealed(point);
        foreach (var existing in landmarks)
            if (existing.Kind == kind && DistanceSquared(existing.Position, point) < .25d)
            { discovered |= existing.Discovered; break; }
        target.Add(new MapLandmark(kind, point, discovered));
    }

    [HideFromIl2Cpp]
    private void UpdateLandmarkDiscovery()
    {
        if (fog == null) return;
        foreach (var landmark in landmarks)
            if (!landmark.Discovered && fog.IsRevealed(landmark.Position)) landmark.Discovered = true;
    }

    [HideFromIl2Cpp]
    private int CountLandmarks(MapLandmarkKind kind)
    {
        int count = 0;
        foreach (var landmark in landmarks) if (landmark.Kind == kind) count++;
        return count;
    }

    private static double DistanceSquared(MapWorldPoint first, MapWorldPoint second)
    {
        double x = first.X - second.X, y = first.Y - second.Y;
        return x * x + y * y;
    }

    private static bool IsLoadedSceneObject(GameObject gameObject)
    {
        if (gameObject == null) return false;
        var scene = gameObject.scene;
        return scene.IsValid() && scene.isLoaded;
    }

    [HideFromIl2Cpp]
    private bool HiddenByGame()
    {
        var level = LevelManager.Instance;
        if (!available || level == null || level.LoadingLevel || metadata == null || level.CurrentLevel.levelDefinitionGuid != metadata.LevelGuid) return true;
        if (CombatManager.Instance?.CurrentEncounter != null || CutsceneManager.Instance?.IsInCutscene == true || Teleporter.IsTeleporting) return true;
        if (PauseManager.Instance?.IsPaused == true && !ownsPause) return true;
        var ui = UIManager.Instance;
        return Active(ui?.GetView<GameMenu>()) || Active(ui?.GetView<PauseMenu>()) || Active(ui?.GetView<LoadingScreen>());
    }

    [HideFromIl2Cpp]
    private static bool Active(View view) => view != null && view.gameObject.activeInHierarchy;

    [HideFromIl2Cpp]
    private void OpenFullMap()
    {
        var pause = PauseManager.Instance;
        if (pause == null || pause.IsPaused || HiddenByGame()) return;
        var lease = new NativeMapHud();
        if (!lease.TryAcquire()) { lease.Dispose(); return; }
        hudLease = lease;
        pauseOwner = pause;
        ownsPause = true; pause.PauseIncremental();
        if (!pause.IsPaused) { ReleaseControls(); return; }
        pause.DisablePause(); ownsPauseDisable = true;
        uiOwner = UIManager.Instance;
        if (uiOwner != null)
        {
            uiOwner.DisableGameMenuStackable(); ownsMenuDisable = true;
            uiOwner.DisableInGameHudPanelsOpening(); ownsHudDisable = true;
        }
        fullMap = true;
        BeginPerfSession();
        hudLease.DumpState("opened");
        hudDiagnosticAt = Time.unscaledTime + 1f;
        if (firstFullOpen)
        {
            Rect area = FullMapArea();
            MapWorldRect overview = MapViewport.ForOverview(metadata.WorldBounds, (int)area.width, (int)area.height);
            fullZoom = Math.Clamp(overview.Height / 80d, 1d, 32d);
            firstFullOpen = false;
        }
        // A reopened map must start from the party's current position. Keeping
        // the previous zoom preserves the player's scale preference while
        // discarding a stale pan position from an earlier inspection.
        Recenter();
        SaveFog();
        MapWorldRect view = FullView(FullMapArea());
        string saveHash = Path.GetFileName(Path.GetDirectoryName(fogPath));
        StaticMapPlugin.Logger.LogInfo($"Static full map opened: zoom={fullZoom:F2}, span={view.Width:F2}x{view.Height:F2}, center=({view.Center.X:F2},{view.Center.Y:F2}), paused={pause.IsPaused}, fog=LocalMap-v3/{saveHash.Substring(0, 12)}/{Path.GetFileName(fogPath).Substring(0, 12)}.");
    }

    [HideFromIl2Cpp]
    private void CloseFullMap(bool deferInputRelease, string reason = "world/UI state")
    {
        if (fullMap)
        {
            LogPerfSession(reason);
            StaticMapPlugin.Logger.LogInfo("Static full map closed: " + reason + ".");
        }
        fullMap = false;
        canvas?.Hide();
        if (deferInputRelease && ownsPause) { pendingRelease = true; releaseFrame = Time.frameCount; }
        else ReleaseControls();
    }

    [HideFromIl2Cpp]
    private void ReleaseControls()
    {
        pendingRelease = false;
        try { if (ownsHudDisable && uiOwner != null) uiOwner.EnableInGameHudPanelsOpening(); }
        catch (Exception e) { StaticMapPlugin.Logger.LogWarning("Release map HUD lock: " + e.Message); }
        finally { ownsHudDisable = false; }
        try { if (ownsMenuDisable && uiOwner != null) uiOwner.EnableGameMenuStackable(false); }
        catch (Exception e) { StaticMapPlugin.Logger.LogWarning("Release map menu lock: " + e.Message); }
        finally { ownsMenuDisable = false; uiOwner = null; }
        try { if (ownsPauseDisable && pauseOwner != null) pauseOwner.EnablePause(false); }
        catch (Exception e) { StaticMapPlugin.Logger.LogWarning("Release pause menu lock: " + e.Message); }
        finally { ownsPauseDisable = false; }
        try { if (ownsPause && pauseOwner != null) pauseOwner.ResumeIncremental(); }
        catch (Exception e) { StaticMapPlugin.Logger.LogWarning("Release map pause: " + e.Message); }
        finally { ownsPause = false; pauseOwner = null; }
        try { hudLease?.Dispose(); }
        catch (Exception e) { StaticMapPlugin.Logger.LogWarning("Release native HUD: " + e.Message); }
        finally { hudLease = null; }
    }

    [HideFromIl2Cpp]
    private void HandleFullMapInput(NativeMapInputFrame input)
    {
        if (Input.GetKeyDown(KeyCode.Escape) || input.CloseRequested) { CloseFullMap(true, "back input"); return; }
        if (Input.GetKeyDown(KeyCode.Home)) { fullZoom = 1; fullCenter = metadata.WorldBounds.Center; }
        if (Input.GetKeyDown(KeyCode.Space) || input.RecenterRequested) Recenter();
        Rect area = FullMapArea();
        float scale = UiScale();
        var mouse = new Vector2(Input.mousePosition.x / scale, (Screen.height - Input.mousePosition.y) / scale);
        double zoomAxis = input.ZoomAxis + (Input.GetKey(KeyCode.UpArrow) ? 1 : 0) - (Input.GetKey(KeyCode.DownArrow) ? 1 : 0);
        float delta = Math.Min(Time.unscaledDeltaTime, .05f);
        if (zoomAxis != 0) Zoom(Math.Exp(Math.Clamp(zoomAxis, -1d, 1d) * delta * 1.4), area, area.center);
        float wheel = area.Contains(mouse) ? Input.mouseScrollDelta.y : 0;
        if (wheel != 0) Zoom(Math.Pow(1.15, wheel), area, mouse);
        MapWorldRect view = FullView(area);
        Vector2 keyboardPan = new(
            (Input.GetKey(KeyCode.D) ? 1f : 0f) - (Input.GetKey(KeyCode.A) ? 1f : 0f),
            (Input.GetKey(KeyCode.W) ? 1f : 0f) - (Input.GetKey(KeyCode.S) ? 1f : 0f));
        if (keyboardPan.sqrMagnitude > 1f) keyboardPan.Normalize();
        Vector2 pan = input.PanVector + keyboardPan;
        if (pan.sqrMagnitude > 1f) pan.Normalize();
        if (pan.sqrMagnitude > 0)
            fullCenter = new MapWorldPoint(fullCenter.X + pan.x * view.Width * delta * .65,
                fullCenter.Y + pan.y * view.Height * delta * .65);
        fullCenter = FullView(area).Center;
        bool keyboardOrMouse = wheel != 0 || Input.GetKey(KeyCode.UpArrow) || Input.GetKey(KeyCode.DownArrow)
            || keyboardPan.sqrMagnitude > 0 || Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.Home);
        if (keyboardOrMouse) UpdateFooter(false);
        else if (input.ZoomAxis != 0 || input.PanVector.sqrMagnitude > 0 || input.RecenterRequested) UpdateFooter(true);
    }

    [HideFromIl2Cpp]
    private void Zoom(double factor, Rect area, Vector2 anchor)
    {
        MapWorldRect before = FullView(area);
        double u = (anchor.x - area.x) / area.width, v = 1d - (anchor.y - area.y) / area.height;
        double worldX = before.MinX + u * before.Width, worldY = before.MinY + v * before.Height;
        fullZoom = Math.Clamp(fullZoom * factor, 1d, 32d);
        MapWorldRect after = MapViewport.ForOverview(metadata.WorldBounds, (int)area.width, (int)area.height, fullZoom, fullCenter);
        fullCenter = new MapWorldPoint(worldX + (.5 - u) * after.Width, worldY + (.5 - v) * after.Height);
        fullCenter = FullView(area).Center;
    }

    [HideFromIl2Cpp]
    private void Recenter()
    {
        Rect area = FullMapArea();
        fullCenter = playerMap; fullCenter = FullView(area).Center;
    }
    [HideFromIl2Cpp]
    private MapWorldRect FullView(Rect area) => MapViewport.ClampToBounds(MapViewport.ForOverview(metadata.WorldBounds, (int)area.width, (int)area.height, fullZoom, fullCenter), metadata.WorldBounds);

    public void LateUpdate()
    {
        if (canvas == null) return;
        long lateStarted = PerfNow();
        bool measure = perfActive;
        try
        {
            if (!available || metadata == null || pendingRelease || HiddenByGame() || !metadata.IncludesHeight(playerWorld.y)) { canvas.Hide(); return; }
            canvas.RefreshLayout();
            var facing = new MapWorldPoint(
                metadata.AxisX[0] * lookDirection.x + metadata.AxisX[1] * lookDirection.y + metadata.AxisX[2] * lookDirection.z,
                metadata.AxisY[0] * lookDirection.x + metadata.AxisY[1] * lookDirection.y + metadata.AxisY[2] * lookDirection.z);
            if (fullMap)
            {
                if (hudLease?.KeepAlive() != true) { CloseFullMap(false, "native HUD no longer available"); return; }
                if (hudDiagnosticAt > 0 && Time.unscaledTime >= hudDiagnosticAt)
                {
                    hudDiagnosticAt = -1f;
                    hudLease.DumpState("after 1 second");
                }
                MapWorldRect view = FullView(FullMapArea());
                long refreshStarted = PerfNow();
                RefreshDetailTiles(view);
                if (perfActive)
                {
                    double elapsed = PerfMs(refreshStarted);
                    perfRefreshTotalMs += elapsed;
                    if (elapsed > perfRefreshMaxMs) perfRefreshMaxMs = elapsed;
                }
                long viewStarted = PerfNow();
                canvas.SetFullView(view, playerMap, facing); canvas.ShowFull();
                if (perfActive)
                {
                    double elapsed = PerfMs(viewStarted);
                    perfViewTotalMs += elapsed;
                    if (elapsed > perfViewMaxMs) perfViewMaxMs = elapsed;
                }
            }
            else if (StaticMapPlugin.ShowMini.Value)
            {
                MapWorldRect view = MapViewport.FixedZoom(playerMap, metadata.MinimapWorldWidth, (int)canvas.MiniRect.width, (int)canvas.MiniRect.height);
                UseOverviewForMinimap();
                canvas.SetMiniView(view, playerMap, facing);
                canvas.ShowMini();
            }
            else canvas.Hide();
        }
        catch (Exception e)
        {
            canvas.Hide(); CloseFullMap(false);
            if (!loggedError) { StaticMapPlugin.Logger.LogError(e); loggedError = true; }
        }
        finally
        {
            if (measure) RecordPerfFrame(PerfMs(lateStarted));
        }
    }

    [HideFromIl2Cpp]
    private void EnsureCanvas()
    {
        if (canvas != null) return;
        var nativeFont = NativeMapAssets.FindFont(UIManager.Instance?.GetView<InGameHud>());
        if (nativeFont == null) return;
        assets ??= new NativeMapAssets(message => StaticMapPlugin.Logger.LogWarning(message));
        canvas = new MapCanvasView(nativeFont, null, assets.Get("panel-ornament-1-full-t1"), assets.Get("map-icn-zale"));
        var nativeText = UIManager.Instance?.GetView<InGameHud>()?.zoneNamePanel?.smallTitleText?.TextMeshProText;
        if (nativeText != null) canvas.SetFooterTextStyle(nativeText);
        canvas.SetFrameLayers(assets.Get("panel-background-t1"), assets.Get("panel-rounded-outline-1px"), assets.Get("panel-ornament-1-full-t1"));
        canvas.SetLandmarkAssets(assets.Get("local-map-campfire-marker"), assets.Get("local-map-savepoint-marker"));
        canvas.SetTextures(baseTexture, fogTexture, metadata.WorldBounds);
        canvas.SetDetailTiles(Array.Empty<MapTextureTile>());
        canvas.SetLandmarks(landmarks);
        UpdateFooter(NativeMapInput.Read().ControllerActive);
        StaticMapPlugin.Logger.LogInfo("Native map canvas ready; font=" + nativeFont.name + ".");
        LogRenderPipeline();
    }

    [HideFromIl2Cpp]
    private void UpdatePlayerMarker(string leaderName)
    {
        if (canvas == null || assets == null) return;
        string value = (leaderName ?? "").ToLowerInvariant();
        string marker = value.Contains("moongirl") || value.Contains("valere") ? "map-icn-valere" :
            value.Contains("garl") ? "map-icn-garl" :
            value.Contains("serai") ? "map-icn-serai" :
            value.Contains("reshan") ? "map-icn-reshan" :
            value.Contains("bst") ? "map-icn-bst" :
            value.Contains("artificer") ? "map-icn-artificer" : "map-icn-zale";
        if (string.Equals(marker, playerMarkerName, StringComparison.Ordinal)) return;
        Sprite sprite = assets.Get(marker);
        if (sprite == null) return;
        canvas.SetPlayerMarker(sprite);
        playerMarkerName = marker;
    }

    [HideFromIl2Cpp]
    private void LogRenderPipeline()
    {
        try
        {
            Canvas nativeCanvas = UIManager.Instance?.CanvasInstance;
            CanvasUpscaleViewport upscale = UIManager.Instance?.CanvasUpscaleViewportInstance;
            string native = nativeCanvas == null ? "missing" :
                $"mode={nativeCanvas.renderMode}, scale={nativeCanvas.scaleFactor:0.###}, pixelRect={nativeCanvas.pixelRect}";
            string viewport = upscale == null ? "missing" :
                $"mode={upscale.renderMode}, srpOverlay={upscale.srpOverlay}, renderPassUpscale={upscale.renderPassUpscale}, " +
                $"useCustom={upscale.useCustomCanvasSize}, customPos={upscale.customCanvasPos}, customSize={upscale.customCanvasSize}, " +
                $"scaler={(upscale.canvasScaler == null ? -1f : upscale.canvasScaler.scaleFactor):0.###}";
            StaticMapPlugin.Logger.LogInfo(
                $"Local map raster path: screen={Screen.width}x{Screen.height}; mapCanvasScale={canvas.Canvas.scaleFactor:0.###}; " +
                $"mapPixelRect={canvas.Canvas.pixelRect}; nativeCanvas=[{native}]; upscaler=[{viewport}].");
        }
        catch (Exception error)
        {
            StaticMapPlugin.Logger.LogWarning("Could not inspect UI upscale path: " + error.Message);
        }
    }

    // Sea of Stars normally draws every ScreenSpaceOverlay Canvas into its 640x360
    // UI viewport, then point-upscales that buffer with the game image.  Supplying a
    // larger sprite alone therefore cannot add detail.  CanvasUpscaleViewport already
    // exposes the game's supported alternate ordering: final-blit the world first,
    // then draw overlay canvases.  Use a physical-screen viewport for that pass so
    // native HUD geometry keeps its CanvasScaler size while map textures and markers
    // retain the pixels available at the output resolution.
    [HideFromIl2Cpp]
    private void MaintainNativeResolutionOverlay()
    {
        CanvasUpscaleViewport current = UIManager.Instance?.CanvasUpscaleViewportInstance;
        if (current == null) return;
        if (postUpscaleOwner != current)
        {
            RestoreUpscalePipeline();
            postUpscaleOwner = current;
            savedCanvasPos = current.customCanvasPos;
            savedCanvasSize = current.customCanvasSize;
            savedUseCustomCanvasSize = current.useCustomCanvasSize;
            current.useCustomCanvasSize = true;
            current.customCanvasPos = Vector2.zero;
            current.customCanvasSize = new Vector2(Screen.width, Screen.height);
            current.SetRenderOverlayUIPassAfterFinalBlit(true);
            postUpscaleWidth = Screen.width;
            postUpscaleHeight = Screen.height;
            postUpscaleActive = true;
            StaticMapPlugin.Logger.LogInfo($"Post-upscale overlay enabled at {Screen.width}x{Screen.height}; native HUD and local map now draw after the 640x360 world blit.");
            return;
        }
        if (!postUpscaleActive) return;
        if (postUpscaleWidth == Screen.width && postUpscaleHeight == Screen.height) return;
        current.customCanvasPos = Vector2.zero;
        current.customCanvasSize = new Vector2(Screen.width, Screen.height);
        postUpscaleWidth = Screen.width;
        postUpscaleHeight = Screen.height;
        StaticMapPlugin.Logger.LogInfo($"Post-upscale overlay viewport resized to {Screen.width}x{Screen.height}.");
    }

    [HideFromIl2Cpp]
    private void RestoreUpscalePipeline()
    {
        if (postUpscaleOwner != null && postUpscaleActive)
        {
            try
            {
                postUpscaleOwner.SetRenderOverlayUIPassAfterFinalBlit(false);
                postUpscaleOwner.customCanvasPos = savedCanvasPos;
                postUpscaleOwner.customCanvasSize = savedCanvasSize;
                postUpscaleOwner.useCustomCanvasSize = savedUseCustomCanvasSize;
            }
            catch (Exception error)
            {
                StaticMapPlugin.Logger.LogWarning("Could not restore the original UI upscale path: " + error.Message);
            }
        }
        postUpscaleOwner = null;
        postUpscaleActive = false;
        postUpscaleWidth = postUpscaleHeight = 0;
    }

    [HideFromIl2Cpp]
    private void UpdateFooter(bool controller)
    {
        ELanguage language = LocalizationManager.Instance?.CurrentLanguage ?? ELanguage.EN;
        if (footerInitialized && footerController == controller && footerLanguage == language) return;
        var nativeText = UIManager.Instance?.GetView<InGameHud>()?.zoneNamePanel?.smallTitleText?.TextMeshProText;
        if (nativeText != null) canvas.SetFooterTextStyle(nativeText);
        string[] labels = LocalMapLabels.For(language, controller);
        MapFooterItem[] footer = controller ? new[] {
            new MapFooterItem(labels[0], assets.Get("buttons-controller-xbox_10")),
            new MapFooterItem(labels[1], assets.Get("buttons-controller-xbox_11")),
            new MapFooterItem(labels[2], assets.Get("dialog-generic-inputs-small_1")),
            new MapFooterItem(labels[3], assets.Get("buttons-controller-xbox_17")),
            new MapFooterItem(labels[4], assets.Get("buttons-controller-xbox_12"))
        } : new[] {
            new MapFooterItem(labels[0], assets.Get("map-icn-wheel")),
            new MapFooterItem(labels[1], assets.Get("KeyboardButtons_UpDown")),
            new MapFooterItem(labels[2], assets.Get("KeyboardButtons_WASD")),
            new MapFooterItem(labels[3], assets.Get("KeyboardButtons_Space")),
            new MapFooterItem(labels[4], assets.Get("KeyboardButtons_home")),
            new MapFooterItem(labels[5], assets.Get("KeyboardButtons_MEsc"))
        };
        canvas.SetFooter(footer);
        // Commit the cache key only after the complete footer has been updated. If a
        // Unity object is unloading and throws, the next frame will retry every slot.
        footerInitialized = true; footerController = controller; footerLanguage = language;
    }

    private static class LocalMapLabels
    {
        public static string[] For(ELanguage language, bool controller)
        {
            if (controller) return language switch {
                ELanguage.JP => new[] { "縮小", "拡大", "移動", "プレイヤーへ", "閉じる" },
                ELanguage.RU => new[] { "\u0423\u043c\u0435\u043d\u044c\u0448\u0438\u0442\u044c", "\u0423\u0432\u0435\u043b\u0438\u0447\u0438\u0442\u044c", "\u041f\u0435\u0440\u0435\u043c\u0435\u0449\u0435\u043d\u0438\u0435", "\u041a \u0438\u0433\u0440\u043e\u043a\u0443", "\u0417\u0430\u043a\u0440\u044b\u0442\u044c" },
                ELanguage.KO => new[] { "축소", "확대", "이동", "플레이어 위치", "닫기" },
                ELanguage.QC => new[] { "Réduire", "Agrandir", "Déplacer", "Au joueur", "Fermer" },
                ELanguage.FR => new[] { "Dézoomer", "Zoomer", "Déplacer", "Au joueur", "Fermer" },
                ELanguage.DE => new[] { "Verkleinern", "Vergrößern", "Verschieben", "Zum Spieler", "Schließen" },
                ELanguage.ES => new[] { "Alejar", "Acercar", "Mover", "Al jugador", "Cerrar" },
                ELanguage.ptBR => new[] { "Diminuir", "Aumentar", "Mover", "Ao jogador", "Fechar" },
                ELanguage.zhCN => new[] { "缩小", "放大", "移动", "玩家位置", "关闭" },
                ELanguage.zhHK => new[] { "縮小", "放大", "移動", "玩家位置", "關閉" },
                ELanguage.IT => new[] { "Riduci", "Ingrandisci", "Sposta", "Al giocatore", "Chiudi" },
                _ => new[] { "Zoom out", "Zoom in", "Move", "To player", "Close" },
            };
            return language switch {
                ELanguage.JP => new[] { "ズーム", "ズーム", "移動", "プレイヤーへ", "全体表示", "閉じる" },
                ELanguage.RU => new[] { "\u041c\u0430\u0441\u0448\u0442\u0430\u0431", "\u041c\u0430\u0441\u0448\u0442\u0430\u0431", "\u041f\u0435\u0440\u0435\u043c\u0435\u0449\u0435\u043d\u0438\u0435", "\u041a \u0438\u0433\u0440\u043e\u043a\u0443", "\u0412\u0441\u044f \u043a\u0430\u0440\u0442\u0430", "\u0417\u0430\u043a\u0440\u044b\u0442\u044c" },
                ELanguage.KO => new[] { "확대/축소", "확대/축소", "이동", "플레이어 위치", "전체 지도", "닫기" },
                ELanguage.QC => new[] { "Zoom", "Zoom", "Déplacer", "Au joueur", "Carte entière", "Fermer" },
                ELanguage.FR => new[] { "Zoom", "Zoom", "Déplacer", "Au joueur", "Carte entière", "Fermer" },
                ELanguage.DE => new[] { "Zoom", "Zoom", "Verschieben", "Zum Spieler", "Ganze Karte", "Schließen" },
                ELanguage.ES => new[] { "Zoom", "Zoom", "Mover", "Al jugador", "Mapa completo", "Cerrar" },
                ELanguage.ptBR => new[] { "Zoom", "Zoom", "Mover", "Ao jogador", "Mapa inteiro", "Fechar" },
                ELanguage.zhCN => new[] { "缩放", "缩放", "移动", "玩家位置", "完整地图", "关闭" },
                ELanguage.zhHK => new[] { "縮放", "縮放", "移動", "玩家位置", "完整地圖", "關閉" },
                ELanguage.IT => new[] { "Zoom", "Zoom", "Sposta", "Al giocatore", "Mappa intera", "Chiudi" },
                _ => new[] { "Zoom", "Zoom", "Move", "To player", "Full map", "Close" },
            };
        }
    }

    [HideFromIl2Cpp]
    internal bool BlockNativeHudInput()
    {
        if (fullMap || pendingRelease) return true;
        if (!available || canvas == null || metadata == null || HiddenByGame() || !metadata.IncludesHeight(playerWorld.y)) return false;
        return (Input.GetKeyDown(KeyCode.M) || NativeMapInput.Read().ToggleRequested) && UIManager.Instance?.GetView<InGameHud>()?.CanOpenAnyPanel() == true;
    }
    [HideFromIl2Cpp]
    internal bool HoldsNativeHud(InGameHud hud) => fullMap && hudLease != null && hudLease.Hud == hud;

    [HideFromIl2Cpp]
    private static float UiScale() => Math.Max(.2f, Math.Min(Screen.width / 1280f, Screen.height / 720f));
    [HideFromIl2Cpp]
    private Rect FullMapArea() { canvas.RefreshLayout(); return canvas.FullRect; }

    [HideFromIl2Cpp]
    private void UploadFog()
    {
        if (fogTexture == null || fog == null) return;
        if (!fog.TryConsumeDirtyRect(out int minX, out int minY, out int width, out int height)) return;
        int required = width * height;
        bool wholeTexture = minX == 0 && minY == 0 && width == fog.Width && height == fog.Height;
        if (!wholeTexture && (fogUploadColors == null || fogUploadColors.Length != required))
            fogUploadColors = new Color32[required];
        Color32[] upload = wholeTexture ? fogColors : fogUploadColors;
        int output = 0;
        for (int y = minY; y < minY + height; y++)
        {
            int row = y * fog.Width + minX;
            for (int x = 0; x < width; x++)
            {
                int index = row + x;
                Color32 color = new Color32(6, 10, 16, fog.Opacity[index]);
                fogColors[index] = color;
                upload[output++] = color;
            }
        }
        fogTexture.SetPixels32(minX, minY, width, height, upload);
        fogTexture.Apply(false, false);
    }
    [HideFromIl2Cpp]
    private void SaveFog()
    {
        if (!dirty || fog == null || fogPath.Length == 0) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fogPath));
            File.WriteAllBytes(fogPath + ".tmp", fog.Save());
            File.Move(fogPath + ".tmp", fogPath, true); dirty = false;
        }
        catch (Exception error) { StaticMapPlugin.Logger.LogWarning("Static fog save failed: " + error.Message); }
    }
    [HideFromIl2Cpp]
    private void ClearMap()
    {
        RestoreUpscalePipeline();
        canvas?.Hide();
        canvas?.SetDetailTiles(Array.Empty<MapTextureTile>());
        foreach (var texture in detailTextures.Values) if (texture != null) Object.Destroy(texture);
        detailTextures.Clear(); detailLastUse.Clear(); detailTileSetKey = detailDesiredKey = ""; detailUseCounter = 0; detailResidentBytes = 0;
        detailDesiredTiles.Clear(); detailScratchTiles.Clear(); detailTileValues.Clear(); detailKeepFiles.Clear(); nextDetailSelection = 0f;
        if (baseTexture != null) Object.Destroy(baseTexture);
        if (fogTexture != null) Object.Destroy(fogTexture);
        baseTexture = null; fogTexture = null; fogColors = fogUploadColors = null; fog = null; metadata = null; tileManifest = null; mapFolder = "";
        landmarks.Clear(); canvas?.SetLandmarks(landmarks);
        available = false; hasRevealPoint = false; dirty = false; fogPath = "";
    }
    [HideFromIl2Cpp]
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    public void OnApplicationQuit() { SaveFog(); CloseFullMap(false); RestoreUpscalePipeline(); }
    public void OnDisable() { SaveFog(); CloseFullMap(false); RestoreUpscalePipeline(); Current = null; }
    public void OnDestroy()
    {
        SaveFog(); CloseFullMap(false); ClearMap(); RestoreUpscalePipeline();
        canvas?.Dispose(); assets?.Dispose(); Current = null;
    }
}

[HarmonyPatch(typeof(InGameHud), nameof(InGameHud.ProcessInputs))]
internal static class NativeMapHudInputPatch
{
    private static bool Prefix()
    {
        try { return StaticMapOverlay.Current?.BlockNativeHudInput() != true; }
        catch { return true; }
    }
}

[HarmonyPatch(typeof(InGameHud), nameof(InGameHud.RunAutoClosers))]
internal static class NativeMapHudAutoClosePatch
{
    private static bool Prefix(InGameHud __instance)
    {
        try { return StaticMapOverlay.Current?.HoldsNativeHud(__instance) != true; }
        catch { return true; }
    }
}

#endif
