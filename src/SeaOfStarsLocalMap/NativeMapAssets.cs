using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using BepInEx;
using Il2CppInterop.Runtime;
using Sabotage.Localization;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SeaOfStarsLocalMap;

/// <summary>
/// Loads the extracted native UI sprites on the Unity main thread.
/// Owns its sprites/textures; fonts returned by FindFont belong to the game.
/// </summary>
public sealed class NativeMapAssets : IDisposable
{
    private sealed class Entry
    {
        public string File;
        public float PixelsPerUnit;
        public Vector2 Pivot;
        public Vector4 Border;
    }

    private sealed class Loaded
    {
        public Sprite Sprite;
        public Texture2D Texture;
    }

    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Loaded> loaded = new(StringComparer.Ordinal);
    private readonly HashSet<string> failed = new(StringComparer.Ordinal);
    private readonly Action<string> warn;
    private bool disposed;

    public string DirectoryPath { get; }

    public NativeMapAssets(Action<string> warn = null)
    {
        this.warn = warn;
        DirectoryPath = Path.GetFullPath(Path.Combine(Paths.PluginPath, "LocalMap", "UI"));
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(DirectoryPath, "native-ui.json")));
            foreach (var item in document.RootElement.GetProperty("sprites").EnumerateArray())
            {
                string name = item.GetProperty("name").GetString();
                string file = item.GetProperty("png").GetString();
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(file)) continue;
                string fullPath = Path.GetFullPath(Path.Combine(DirectoryPath, file));
                if (!fullPath.StartsWith(DirectoryPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    continue;

                float ppu = item.TryGetProperty("pixels_per_unit", out var value) ? value.GetSingle() : 24f;
                if (!float.IsFinite(ppu) || ppu <= 0) ppu = 24f;
                var pivot = new Vector2(.5f, .5f);
                if (item.TryGetProperty("pivot", out var pv) && pv.GetArrayLength() == 2)
                    pivot = new Vector2(pv[0].GetSingle(), pv[1].GetSingle());
                var border = Vector4.zero;
                if ((item.TryGetProperty("border_lbrt", out var bv) || item.TryGetProperty("borders_lbrt", out bv))
                    && bv.GetArrayLength() == 4)
                    border = new Vector4(bv[0].GetSingle(), bv[1].GetSingle(), bv[2].GetSingle(), bv[3].GetSingle());

                entries[name] = new Entry { File = fullPath, PixelsPerUnit = ppu, Pivot = pivot, Border = border };
            }
        }
        catch (Exception ex)
        {
            this.warn?.Invoke("LocalMap native UI manifest could not be loaded: " + ex.Message);
        }
    }

    /// <summary>Returns a cached owned sprite, or null for an unavailable asset.</summary>
    public Sprite Get(string name)
    {
        if (disposed || string.IsNullOrEmpty(name) || failed.Contains(name)) return null;
        if (loaded.TryGetValue(name, out var existing)) return existing.Sprite;
        if (!entries.TryGetValue(name, out var entry))
        {
            ReportMissing(name, "not present in native-ui.json");
            return null;
        }

        Texture2D texture = null;
        Sprite sprite = null;
        try
        {
            byte[] bytes = File.ReadAllBytes(entry.File);
            texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            texture.name = "LocalMap.UI." + name;
            texture.hideFlags = HideFlags.HideAndDontSave;
            if (!ImageConversion.LoadImage(texture, bytes, true))
                throw new InvalidDataException("PNG decoding failed");
            texture.filterMode = FilterMode.Point;
            texture.wrapMode = TextureWrapMode.Clamp;

            sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), entry.Pivot,
                entry.PixelsPerUnit, 0, SpriteMeshType.FullRect, entry.Border, false);
            if (sprite == null) throw new InvalidDataException("Sprite creation failed");
            sprite.name = name;
            sprite.hideFlags = HideFlags.HideAndDontSave;
            loaded.Add(name, new Loaded { Sprite = sprite, Texture = texture });
            return sprite;
        }
        catch (Exception ex)
        {
            if (sprite != null) Object.Destroy(sprite);
            if (texture != null) Object.Destroy(texture);
            ReportMissing(name, ex.Message);
            return null;
        }
    }

    private void ReportMissing(string name, string reason)
    {
        if (failed.Add(name)) warn?.Invoke("LocalMap UI asset '" + name + "' unavailable: " + reason);
    }

    /// <summary>
    /// Borrows the HUD's font for the currently selected language, then PixelPlay.
    /// Does not alter a font, its material, fallbacks, or the game's localization.
    /// Call again after HUD creation or a language change if this returns null.
    /// </summary>
    public static TMP_FontAsset FindFont(InGameHud hud)
    {
        try
        {
            if (hud != null && hud.zoneNamePanel != null && hud.zoneNamePanel.smallTitleText != null)
            {
                var text = hud.zoneNamePanel.smallTitleText.TextMeshProText;
                var current = text != null ? text.font : null;
                if (current != null) return current;
            }
        }
        catch (Exception) { /* A HUD may be unloading between scene transitions. */ }

        try
        {
            foreach (var resource in Resources.FindObjectsOfTypeAll(Il2CppType.Of<TMP_FontAsset>()))
            {
                var candidate = resource.TryCast<TMP_FontAsset>();
                if (candidate != null && string.Equals(candidate.name, "pixelplay SDF", StringComparison.Ordinal)) return candidate;
            }
        }
        catch (Exception) { /* The font's addressable may not be loaded yet. */ }
        return null;
    }

    /// <summary>The native HUD style source applies the game's per-language font metrics.</summary>
    public static LocalizedFont FindLocalizedFont(InGameHud hud)
    {
        try { return hud?.zoneNamePanel?.smallTitleText?.localizedFont; }
        catch (Exception) { return null; }
    }

    /// <summary>Release after any view using these sprites has been disposed.</summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var value in loaded.Values)
        {
            if (value.Sprite != null) Object.Destroy(value.Sprite);
            if (value.Texture != null) Object.Destroy(value.Texture);
        }
        loaded.Clear();
        entries.Clear();
        failed.Clear();
    }
}
