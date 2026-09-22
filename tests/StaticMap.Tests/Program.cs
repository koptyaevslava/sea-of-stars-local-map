using SeaOfStarsLocalMap;
using System.Security.Cryptography;

int passed = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
void Test(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
var bounds = new MapWorldRect(-10, -5, 10, 5);
StaticFog Fog(string id = "cave-floor-01-v1") => new StaticFog(80, 40, bounds, id);
byte At(StaticFog fog, double x, double y)
{
    int px = (int)((x - fog.Bounds.MinX) / fog.Bounds.Width * fog.Width);
    int py = (int)((y - fog.Bounds.MinY) / fog.Bounds.Height * fog.Height);
    return fog.Opacity[py * fog.Width + px];
}
void Rehash(byte[] data) => SHA256.HashData(data.AsSpan(0, data.Length - 32)).CopyTo(data.AsSpan(data.Length - 32));

Test("Fixed bounds and bottom-up orientation remain stable after reveal", () =>
{
    var fog = Fog(); var buffer = fog.Opacity;
    Check(fog.Opacity.All(x => x == fog.HiddenOpacity), "initial fog must be hidden");
    fog.RevealDisc(new MapWorldPoint(-8, -3), 1);
    Check(At(fog, -8, -3) == 0 && At(fog, -8, 3) == fog.HiddenOpacity, "south reveal orientation");
    fog.RevealDisc(new MapWorldPoint(100, 100), 1);
    Check(fog.Width == 80 && fog.Height == 40 && ReferenceEquals(buffer, fog.Opacity), "reveal changed fixed raster");
    Check(fog.Bounds.MinX == -10 && fog.Bounds.MaxY == 5, "reveal changed world bounds");
});
Test("Fog reports only changed pixels after its initial full upload", () =>
{
    var fog = Fog();
    Check(fog.TryConsumeDirtyRect(out int x, out int y, out int width, out int height), "initial dirty rectangle missing");
    Check(x == 0 && y == 0 && width == fog.Width && height == fog.Height, "initial upload must cover the texture");
    Check(!fog.TryConsumeDirtyRect(out _, out _, out _, out _), "clean fog reported another upload");
    fog.RevealDisc(new MapWorldPoint(0, 0), .5);
    Check(fog.TryConsumeDirtyRect(out x, out y, out width, out height), "reveal dirty rectangle missing");
    Check(width > 0 && height > 0 && width < fog.Width && height < fog.Height, "small reveal requested a full texture upload");
    Check(!fog.TryConsumeDirtyRect(out _, out _, out _, out _), "dirty rectangle was not consumed");
    fog.RevealDisc(new MapWorldPoint(0, 0), .5);
    Check(!fog.TryConsumeDirtyRect(out _, out _, out _, out _), "unchanged reveal requested an upload");
});
Test("Narrow feather is smooth and repeated reveal never hides known pixels", () =>
{
    var fog = new StaticFog(80, 40, new MapWorldRect(0, 0, 20, 10), "floor");
    fog.RevealDisc(new MapWorldPoint(10.125, 5.125), 1, 1);
    Check(At(fog, 10.125, 5.125) == 0, "disc center");
    Check(At(fog, 11.125, 5.125) == 0, "solid reveal radius");
    byte partial = At(fog, 11.625, 5.125);
    Check(partial > 0 && partial < fog.HiddenOpacity, "feather must be partial opacity");
    Check(At(fog, 12.375, 5.125) == fog.HiddenOpacity, "outside feather");
    var previous = fog.Opacity.ToArray(); long revision = fog.Revision;
    Check(!fog.RevealDisc(new MapWorldPoint(10.125, 5.125), 1, 1) && fog.Revision == revision, "same reveal should be a no-op");
    fog.RevealDisc(new MapWorldPoint(12, 5), .5, .5);
    Check(previous.Zip(fog.Opacity).All(pair => pair.Second <= pair.First), "reveal darkened previous exploration");
});
Test("Landmark discovery follows persistent fog and rejects points outside the map", () =>
{
    var fog = Fog(); var landmark = new MapWorldPoint(3, 1);
    Check(!fog.IsRevealed(landmark), "hidden landmark was exposed");
    fog.RevealDisc(landmark, .5);
    Check(fog.IsRevealed(landmark), "revealed landmark stayed hidden");
    Check(!fog.IsRevealed(new MapWorldPoint(100, 100)), "outside landmark was exposed");
    var restored = Fog(); restored.Load(fog.Save());
    Check(restored.IsRevealed(landmark), "landmark discovery did not survive fog restore");
});
Test("Fast movement reveals a continuous segment including endpoints", () =>
{
    var fog = Fog();
    fog.RevealSegment(new MapWorldPoint(-8, -3), new MapWorldPoint(8, 3), .5);
    for (int i = 0; i <= 64; i++)
    {
        double t = i / 64d;
        Check(At(fog, -8 + 16 * t, -3 + 6 * t) == 0, "gap in traversed segment");
    }
    Check(At(fog, -8, 3) == fog.HiddenOpacity, "segment revealed unvisited corner");
});
Test("Separate floors keep independent fog and reject the other floor's save", () =>
{
    var lower = Fog("cave-floor-01-v1"); var upper = Fog("cave-floor-02-v1");
    lower.RevealDisc(new MapWorldPoint(0, 0), 2);
    Check(upper.Opacity.All(x => x == upper.HiddenOpacity), "floor instances leaked exploration");
    Throws<InvalidDataException>(() => upper.Load(lower.Save()));
    Check(upper.Revision == 0 && upper.Opacity.All(x => x == upper.HiddenOpacity), "mismatched load partially modified fog");
});
Test("Save round trip and loading an older save only adds known areas", () =>
{
    var original = Fog(); original.RevealDisc(new MapWorldPoint(-5, 0), 2, .5);
    byte[] save = original.Save(); var restored = Fog(); restored.Load(save);
    Check(original.Opacity.SequenceEqual(restored.Opacity), "save round trip changed opacity");
    restored.RevealDisc(new MapWorldPoint(5, 0), 2, .5);
    var latest = restored.Opacity.ToArray(); long revision = restored.Revision;
    restored.Load(save);
    Check(latest.SequenceEqual(restored.Opacity) && revision == restored.Revision, "older save hid recent exploration");
});
Test("Save dimensions, geometry, identity, version and integrity are verified before mutation", () =>
{
    var original = Fog(); original.RevealDisc(new MapWorldPoint(0, 0), 2);
    byte[] save = original.Save();
    Throws<InvalidDataException>(() => new StaticFog(40, 80, bounds, original.BaseId).Load(save));
    Throws<InvalidDataException>(() => new StaticFog(80, 40, new MapWorldRect(-11, -5, 9, 5), original.BaseId).Load(save));
    var invalidVersion = save.ToArray(); BitConverter.GetBytes(2).CopyTo(invalidVersion, 4); Rehash(invalidVersion);
    var fresh = Fog(); Throws<InvalidDataException>(() => fresh.Load(invalidVersion));
    var corrupted = save.ToArray(); corrupted[corrupted.Length - 33] ^= 1;
    Throws<InvalidDataException>(() => fresh.Load(corrupted));
    Throws<InvalidDataException>(() => fresh.Load(save.Take(save.Length - 1).ToArray()));
    Check(fresh.Revision == 0 && fresh.Opacity.All(x => x == fresh.HiddenOpacity), "invalid load partially changed fog");
});
Test("Minimap zoom stays fixed regardless of location and exploration extent", () =>
{
    var a = MapViewport.FixedZoom(new MapWorldPoint(0, 0), 40, 320, 180);
    var b = MapViewport.FixedZoom(new MapWorldPoint(1000, -500), 40, 320, 180);
    Check(a.Width == 40 && b.Width == 40 && a.Height == 22.5 && b.Height == 22.5, "fixed zoom changed");
    Check(b.Center.X == 1000 && b.Center.Y == -500, "minimap did not follow player");
});
Test("Overview fits the base and manual pan clamps without changing zoom", () =>
{
    var full = MapViewport.ForOverview(bounds, 100, 100);
    Check(full.Width == 20 && full.Height == 20 && full.MinY == -10, "overview aspect fit");
    var zoomed = MapViewport.ForOverview(bounds, 100, 100, 4, new MapWorldPoint(100, 100));
    var clamped = MapViewport.ClampToBounds(zoomed, bounds);
    Check(clamped.Width == zoomed.Width && clamped.Height == zoomed.Height, "clamping changed zoom");
    Check(clamped.MaxX == bounds.MaxX && clamped.MaxY == bounds.MaxY, "pan not clamped");
});
Test("Dimension overflow and invalid reveal data reject safely", () =>
{
    Throws<ArgumentOutOfRangeException>(() => new StaticFog(int.MaxValue, int.MaxValue, bounds, "floor"));
    Throws<ArgumentException>(() => new StaticFog(1, 1, new MapWorldRect(0, 0, 0, 1), "floor"));
    var fog = Fog();
    Throws<ArgumentException>(() => fog.RevealDisc(new MapWorldPoint(double.NaN, 0), 1));
    Throws<ArgumentOutOfRangeException>(() => fog.RevealDisc(new MapWorldPoint(0, 0), -1));
    Check(fog.Revision == 0, "invalid reveal changed fog");
});
const string levelGuid = "74b641ff0864f0d4f956c8c0eb3fe905";
StaticMapMetadata Metadata() => new StaticMapMetadata
{
    SchemaVersion = 1, LevelGuid = levelGuid, MapId = "TormentPeak", FloorId = "main", BaseVersion = "1",
    Image = "base.png", ImageSha256 = new string('a', 64), Width = 5706, Height = 2862,
    Bounds = new StaticMapBounds { MinX = -9, MinY = -43, MaxX = 466, MaxY = 195 },
    AxisX = new[] { 1d, 0d, 0d }, AxisY = new[] { 0d, .75, .75 }
};
Test("Authored camera projection preserves elevation and non-unit basis scale", () =>
{
    var map = Metadata(); map.Validate(levelGuid);
    var lower = map.Project(20, 2, 30); var upper = map.Project(20, 12, 30);
    Check(lower.X == 20 && lower.Y == 24 && upper.Y - lower.Y == 7.5, "projection dropped elevation or normalized authored basis");
    Check(map.Width == 5706 && map.Height == 2862, "full-size base rejected or resized");
});
Test("Metadata round trip and floor boundary ownership are explicit", () =>
{
    var map = Metadata(); map.FloorMinWorldY = 0; map.FloorMaxWorldY = 10;
    var decoded = StaticMapMetadata.Parse(System.Text.Json.JsonSerializer.Serialize(map), levelGuid);
    Check(decoded.IncludesHeight(0) && decoded.IncludesHeight(9.999) && !decoded.IncludesHeight(10), "floor boundary must belong to exactly one floor");
    Check(decoded.WorldBounds.Width == 475 && decoded.WorldBounds.Height == 238, "metadata bounds changed");
});
Test("Wrong location, traversal image paths and invalid projection fail closed", () =>
{
    Throws<InvalidDataException>(() => Metadata().Validate("00000000000000000000000000000000"));
    var map = Metadata(); map.Image = "../base.png"; Throws<InvalidDataException>(() => map.Validate(levelGuid));
    map = Metadata(); map.AxisY = new[] { 1d, 0d, 0d }; Throws<InvalidDataException>(() => map.Validate(levelGuid));
    map = Metadata(); map.AxisX[0] = double.NaN; Throws<InvalidDataException>(() => map.Validate(levelGuid));
    map = Metadata(); map.ImageSha256 = new string('z', 64); Throws<InvalidDataException>(() => map.Validate(levelGuid));
});
StaticMapImageVariant HdVariant(StaticMapMetadata map) => new StaticMapImageVariant
{
    SchemaVersion = 1, BaseImageSha256 = map.ImageSha256, Image = "base-hd.png", ImageSha256 = new string('b', 64),
    Width = 7418, Height = 3721, Bounds = map.Bounds, AxisX = map.AxisX.ToArray(), AxisY = map.AxisY.ToArray(),
    OffsetX = map.OffsetX, OffsetY = map.OffsetY
};
Test("HD render preserves the original metadata and saved exploration identity", () =>
{
    var map = Metadata(); string originalJson = System.Text.Json.JsonSerializer.Serialize(map);
    var fog = new StaticFog(80, 40, map.WorldBounds, originalJson);
    fog.RevealDisc(map.WorldBounds.Center, 20);
    byte[] saved = fog.Save();
    var hd = StaticMapImageVariant.Parse(System.Text.Json.JsonSerializer.Serialize(HdVariant(map)), map);
    Check(hd.Width == 7418 && hd.Height == 3721, "HD dimensions changed");
    Check(System.Text.Json.JsonSerializer.Serialize(map) == originalJson, "HD changed original metadata");
    var restored = new StaticFog(80, 40, map.WorldBounds, originalJson); restored.Load(saved);
    Check(restored.Opacity.SequenceEqual(fog.Opacity), "HD changed saved exploration");
});
Test("HD render rejects shifted projection, wrong base, aspect changes and unsafe paths", () =>
{
    var map = Metadata();
    void Reject(Action<StaticMapImageVariant> change)
    {
        var hd = HdVariant(map); change(hd);
        Throws<InvalidDataException>(() => StaticMapImageVariant.Parse(System.Text.Json.JsonSerializer.Serialize(hd), map));
    }
    Reject(hd => hd.BaseImageSha256 = new string('c', 64));
    Reject(hd => hd.Image = "../base-hd.png");
    Reject(hd => hd.Width = 8193);
    Reject(hd => hd.Height = 3000);
    Reject(hd => hd.AxisY[1] = 1);
    Reject(hd => hd.OffsetX = 1);
    Reject(hd => hd.Bounds = new StaticMapBounds { MinX = -8, MinY = -43, MaxX = 466, MaxY = 195 });
    Reject(hd => hd.ImageSha256 = new string('z', 64));
});
StaticMapTileManifest TileManifest(StaticMapMetadata map) => new StaticMapTileManifest
{
    SchemaVersion = 1, BaseImageSha256 = map.ImageSha256, Scale = 5,
    Width = map.Width * 5, Height = map.Height * 5, TileSize = 2048,
    Bounds = map.Bounds, AxisX = map.AxisX.ToArray(), AxisY = map.AxisY.ToArray(),
    OffsetX = map.OffsetX, OffsetY = map.OffsetY,
    Tiles = new List<StaticMapTileInfo> {
        new() { File = "tiles-5x/tile-00-00.png", Sha256 = new string('c', 64), X = 0, Y = 0, Width = 2048, Height = 2048 },
        new() { File = "tiles-5x/tile-13-06.png", Sha256 = new string('d', 64), X = 13*2048, Y = 6*2048, Width = 1906, Height = 2022 }
    }
};
Test("5x tile manifest preserves projection and maps top and bottom rows without inversion", () =>
{
    var map = Metadata();
    var manifest = StaticMapTileManifest.Parse(System.Text.Json.JsonSerializer.Serialize(TileManifest(map)), map);
    MapWorldRect top = manifest.WorldRect(manifest.Tiles[0]);
    MapWorldRect bottom = manifest.WorldRect(manifest.Tiles[1]);
    Check(top.MinX == map.Bounds.MinX && top.MaxY == map.Bounds.MaxY, "top-left tile shifted");
    Check(Math.Abs(bottom.MaxX - map.Bounds.MaxX) < 1e-9 && Math.Abs(bottom.MinY - map.Bounds.MinY) < 1e-9,
        "bottom-right tile shifted or inverted");
});
Test("5x tile manifest rejects traversal, duplicate cells, wrong scale and oversized tiles", () =>
{
    var map = Metadata();
    void Reject(Action<StaticMapTileManifest> change)
    {
        var tiles = TileManifest(map); change(tiles);
        Throws<InvalidDataException>(() => StaticMapTileManifest.Parse(System.Text.Json.JsonSerializer.Serialize(tiles), map));
    }
    Reject(value => value.Scale = 4);
    Reject(value => value.BaseImageSha256 = new string('e', 64));
    Reject(value => value.Tiles[0].File = "../tile.png");
    Reject(value => value.Tiles[0].Width = 4096);
    Reject(value => { value.Tiles[1].X = value.Tiles[0].X; value.Tiles[1].Y = value.Tiles[0].Y; });
    Reject(value => value.Tiles[0].Sha256 = new string('z', 64));
});
Console.WriteLine($"{passed} static fog and viewport tests passed.");
