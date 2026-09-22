using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace SeaOfStarsLocalMap;

public readonly struct MapFooterItem
{
    public readonly string Label;
    public readonly Sprite Icon;
    public MapFooterItem(string label, Sprite icon = null) { Label = label ?? ""; Icon = icon; }
}

public enum MapLandmarkKind
{
    Campfire,
    Savepoint
}

/// <summary>A world-space point of interest whose discovered state follows the persistent fog.</summary>
public sealed class MapLandmark
{
    public MapLandmarkKind Kind { get; }
    public MapWorldPoint Position { get; }
    public bool Discovered { get; set; }

    public MapLandmark(MapLandmarkKind kind, MapWorldPoint position, bool discovered = false)
    {
        Kind = kind; Position = position; Discovered = discovered;
    }
}

/// <summary>One lazily loaded high-resolution atlas tile in map-world coordinates.</summary>
public sealed class MapTextureTile
{
    public string Id { get; }
    public Texture Texture { get; }
    public MapWorldRect Bounds { get; }

    public MapTextureTile(string id, Texture texture, MapWorldRect bounds)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Texture = texture ?? throw new ArgumentNullException(nameof(texture));
        if (!bounds.IsValid) throw new ArgumentException("Tile bounds must be valid.", nameof(bounds));
        Bounds = bounds;
    }
}

/// <summary>
/// Native UGUI presentation of one immutable map and its independent fog texture.
/// Owns only its Canvas and generated mask/rim/marker assets; caller owns supplied assets.
/// All logical Rect coordinates use a top-left origin at the 1280x720 reference scale.
/// No raycasters or input handlers are created. The plugin owns input and visibility policy.
/// </summary>
public sealed class MapCanvasView : IDisposable
{
    private sealed class LandmarkVisual
    {
        public MapLandmark Landmark;
        public Image Image;
        public RectTransform Rect;
    }

    private sealed class TileVisual
    {
        public MapTextureTile Tile;
        public RawImage Image;
    }

    private sealed class Layers
    {
        public RectTransform Root, Viewport, MarkerRect;
        public RawImage Base, Fog;
        public Image Marker;
        public readonly List<TileVisual> DetailTiles = new();
        public readonly List<LandmarkVisual> Landmarks = new();
    }

    private readonly GameObject root;
    private readonly RectTransform canvasRect;
    private readonly RectTransform footerRoot;
    private readonly Layers mini, full;
    private readonly Image miniFrame, fullFrame, footerFrame;
    private readonly Image fullBackground, fullOutline, footerBackground, footerOutline;
    private readonly List<Object> ownedAssets = new();
    private readonly List<GameObject> footerItems = new();
    private readonly List<Image> footerIcons = new();
    private readonly List<TextMeshProUGUI> footerLabels = new();
    private readonly List<Image> ornaments = new();
    private readonly Sprite fallbackRim, fallbackMarker, fallbackCampfire, fallbackSavepoint;
    private Sprite campfireMarker, savepointMarker;
    private TMP_FontAsset font;
    private Material fontSharedMaterial;
    private float footerFontSize = 32f, footerCharacterSpacing, footerWordSpacing, footerLineSpacing;
    private FontStyles footerFontStyle = FontStyles.Normal;
    private Texture baseTexture, fogTexture;
    private MapWorldRect bounds;
    private MapFooterItem[] footerData = Array.Empty<MapFooterItem>();
    private bool disposed;
    private int visibilityMode = -2;
    private int lastScreenWidth, lastScreenHeight;

    public Rect MiniRect { get; private set; }
    /// <summary>The full map's content viewport, inset 20 logical pixels inside its frame.</summary>
    public Rect FullRect { get; private set; }
    public Rect FullFrameRect { get; private set; }
    public Rect FooterRect { get; private set; }
    public float LogicalScale => Math.Max(.01f, Math.Min(Screen.width / 1280f, Screen.height / 720f));
    public Canvas Canvas { get; }

    public MapCanvasView(TMP_FontAsset nativeFont, Sprite nativeMinimapFrame, Sprite nativeFullFrame,
        Sprite nativePlayerMarker = null, int sortingOrder = 30000)
    {
        root = NewObject("LocalMap.NativeCanvas", null);
        Object.DontDestroyOnLoad(root);
        canvasRect = root.GetComponent<RectTransform>();
        Canvas = root.AddComponent<Canvas>();
        Canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        Canvas.sortingOrder = sortingOrder;
        Canvas.pixelPerfect = true;
        // TMP uses UV1 for atlas/SDF scale and native UI materials may read the
        // extra geometry channels. This separate Canvas must carry them itself.
        Canvas.additionalShaderChannels = AdditionalCanvasShaderChannels.TexCoord1 |
            AdditionalCanvasShaderChannels.TexCoord2 | AdditionalCanvasShaderChannels.Normal |
            AdditionalCanvasShaderChannels.Tangent;
        var scaler = root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1280, 720);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
        scaler.referencePixelsPerUnit = 100f;

        Sprite circle = CreateCircleMask();
        fallbackRim = CreateCircleRim();
        fallbackMarker = CreateMarker();
        fallbackCampfire = CreateCampfireMarker();
        fallbackSavepoint = CreateSavepointMarker();
        campfireMarker = fallbackCampfire;
        savepointMarker = fallbackSavepoint;

        mini = CreateLayers("Minimap", canvasRect, true, circle);
        miniFrame = CreateImage("Native round frame", mini.Root);
        miniFrame.preserveAspect = true;

        var fullRoot = NewObject("FullMap", canvasRect).GetComponent<RectTransform>();
        fullBackground = CreateImage("Native map background", fullRoot);
        fullOutline = CreateImage("Native map outline", fullRoot);
        fullFrame = CreateImage("Native map ornaments", fullRoot);
        fullFrame.type = Image.Type.Sliced;
        full = CreateLayers("Map contents", fullRoot, false, null);
        fullOutline.transform.SetAsLastSibling();
        fullFrame.transform.SetAsLastSibling();
        // Full.Root is the viewport owner. Its parent is the separately sized native frame.
        footerRoot = NewObject("Native controls frame", canvasRect).GetComponent<RectTransform>();
        footerBackground = CreateImage("Native controls background", footerRoot);
        footerOutline = CreateImage("Native controls outline", footerRoot);
        footerFrame = CreateImage("Native controls ornaments", footerRoot);
        footerFrame.type = Image.Type.Sliced;
        fullBackground.enabled = fullOutline.enabled = footerBackground.enabled = footerOutline.enabled = false;

        SetNativeAssets(nativeFont, nativeMinimapFrame, nativeFullFrame, nativePlayerMarker);
        RefreshLayout(true);
        Hide();
    }

    public void SetNativeAssets(TMP_FontAsset nativeFont, Sprite nativeMinimapFrame, Sprite nativeFullFrame, Sprite nativePlayerMarker = null)
    {
        ThrowIfDisposed();
        font = nativeFont;
        miniFrame.sprite = nativeMinimapFrame != null ? nativeMinimapFrame : fallbackRim;
        miniFrame.type = Image.Type.Simple;
        SetFrameImage(fullFrame, nativeFullFrame, new Color32(114, 151, 178, 255));
        SetFrameImage(footerFrame, nativeFullFrame, new Color32(114, 151, 178, 255));
        SetPlayerMarker(nativePlayerMarker);
        foreach (var text in footerLabels)
            ApplyFooterTextStyle(text);
    }

    /// <summary>Use the native world-map portrait for the current party leader.</summary>
    public void SetPlayerMarker(Sprite marker)
    {
        ThrowIfDisposed();
        mini.Marker.sprite = full.Marker.sprite = marker != null ? marker : fallbackMarker;
    }

    /// <summary>Use approved high-contrast point-of-interest sprites, with generated fallbacks if files are absent.</summary>
    public void SetLandmarkAssets(Sprite campfire, Sprite savepoint)
    {
        ThrowIfDisposed();
        campfireMarker = campfire != null ? campfire : fallbackCampfire;
        savepointMarker = savepoint != null ? savepoint : fallbackSavepoint;
    }

    /// <summary>Replace the current scene's points of interest on both map views.</summary>
    public void SetLandmarks(IReadOnlyList<MapLandmark> values)
    {
        ThrowIfDisposed();
        if (values == null) throw new ArgumentNullException(nameof(values));
        RebuildLandmarks(mini, values);
        RebuildLandmarks(full, values);
    }

    /// <summary>Replace the visible 5x tile working set while retaining the low-resolution overview below it.</summary>
    public void SetDetailTiles(IReadOnlyList<MapTextureTile> values)
    {
        ThrowIfDisposed();
        if (values == null) throw new ArgumentNullException(nameof(values));
        RebuildDetailTiles(mini, values);
        RebuildDetailTiles(full, values);
    }

    /// <summary>Borrow the native TMP text's shared material, never its stencil-specific materialForRendering.</summary>
    public void SetFontMaterial(Material nativeShared)
    {
        ThrowIfDisposed();
        fontSharedMaterial = nativeShared;
        foreach (var text in footerLabels)
        {
            if (nativeShared != null) text.fontSharedMaterial = nativeShared;
            else if (font != null) text.fontSharedMaterial = font.material;
        }
    }

    /// <summary>
    /// Borrow the currently rendered native title style. The title has already completed
    /// the game's language-specific font and material selection, so no asynchronous
    /// TextLocalizer is needed on this separate overlay canvas.
    /// </summary>
    public void SetFooterTextStyle(TextMeshProUGUI nativeText)
    {
        ThrowIfDisposed();
        if (nativeText == null || nativeText.font == null) return;
        font = nativeText.font;
        fontSharedMaterial = nativeText.fontSharedMaterial;
        footerFontSize = Math.Max(1f, nativeText.fontSize * 2f);
        footerCharacterSpacing = nativeText.characterSpacing;
        footerWordSpacing = nativeText.wordSpacing;
        footerLineSpacing = nativeText.lineSpacing;
        footerFontStyle = nativeText.fontStyle;
        foreach (var text in footerLabels) ApplyFooterTextStyle(text);
    }

    /// <summary>Native frame composition: background first, 1px outline, then corner ornaments.</summary>
    public void SetFrameLayers(Sprite background, Sprite outline, Sprite ornament)
    {
        ThrowIfDisposed();
        var panelColor = new Color32(50, 61, 82, 255);
        var outlineColor = new Color32(198, 222, 239, 255);
        var ornamentColor = new Color32(114, 151, 178, 255);
        SetFrameImage(fullBackground, background, panelColor);
        SetFrameImage(footerBackground, background, panelColor);
        SetFrameImage(fullOutline, outline, outlineColor);
        SetFrameImage(footerOutline, outline, outlineColor);
        SetFrameImage(fullFrame, ornament, ornamentColor);
        SetFrameImage(footerFrame, ornament, ornamentColor);
    }

    private static void SetFrameImage(Image image, Sprite sprite, Color tint)
    {
        image.sprite = sprite;
        image.type = Image.Type.Sliced;
        image.color = UiTint(tint);
        image.enabled = sprite != null;
        // Native sprites were authored for the game's 640px reference canvas.
        // At our 1280px reference, border widths double: 1px -> 2px, 9px -> 18px.
        if (sprite != null && sprite.pixelsPerUnit > 0) image.pixelsPerUnitMultiplier = 50f / sprite.pixelsPerUnit;
    }

    public void SetTextures(Texture immutableBase, Texture changingFog, MapWorldRect worldBounds)
    {
        ThrowIfDisposed();
        if (immutableBase == null) throw new ArgumentNullException(nameof(immutableBase));
        if (!worldBounds.IsValid) throw new ArgumentException("A fixed map rectangle is required.", nameof(worldBounds));
        baseTexture = immutableBase; fogTexture = changingFog; bounds = worldBounds;
        mini.Base.texture = full.Base.texture = immutableBase;
        mini.Fog.texture = full.Fog.texture = changingFog;
        mini.Fog.enabled = full.Fog.enabled = changingFog != null;
    }

    public void SetMiniView(MapWorldRect view, MapWorldPoint player, MapWorldPoint lookDirection)
    {
        ThrowIfDisposed(); RefreshLayout();
        UpdateView(mini, view, player, lookDirection, MiniRect.width, MiniRect.height);
    }

    public void SetFullView(MapWorldRect view, MapWorldPoint player, MapWorldPoint lookDirection)
    {
        ThrowIfDisposed(); RefreshLayout();
        UpdateView(full, view, player, lookDirection, FullRect.width, FullRect.height);
    }

    /// <summary>Update up to eight native glyph / short-label controls without rebuilding stable slots.</summary>
    public void SetFooter(MapFooterItem[] items)
    {
        ThrowIfDisposed();
        if (items == null) throw new ArgumentNullException(nameof(items));
        if (items.Length > 8) throw new ArgumentException("The footer supports at most eight short controls.", nameof(items));
        while (footerItems.Count > items.Length)
        {
            int last = footerItems.Count - 1;
            Object.Destroy(footerItems[last]);
            footerItems.RemoveAt(last); footerIcons.RemoveAt(last);
            footerLabels.RemoveAt(last);
        }
        while (footerItems.Count < items.Length)
        {
            int i = footerItems.Count;
            var holder = NewObject("Control " + i, footerFrame.rectTransform);
            footerItems.Add(holder);
            var holderRect = holder.GetComponent<RectTransform>();
            Image glyph = CreateImage("Native input glyph", holderRect);
            glyph.preserveAspect = true;
            SetRect(glyph.rectTransform, new Rect(0, 5, 40, 36));
            footerIcons.Add(glyph);
            var textObject = NewObject("Localized control label", holderRect);
            var text = textObject.AddComponent<TextMeshProUGUI>();
            text.raycastTarget = false;
            text.enableAutoSizing = false;
            text.color = Color.white;
            text.alignment = TextAlignmentOptions.MidlineLeft;
            text.enableWordWrapping = false;
            text.overflowMode = TextOverflowModes.Ellipsis;
            text.richText = false;
            footerLabels.Add(text);
            ApplyFooterTextStyle(text);
        }
        footerData = (MapFooterItem[])items.Clone();
        for (int i = 0; i < items.Length; i++)
        {
            footerIcons[i].sprite = items[i].Icon;
            footerIcons[i].enabled = items[i].Icon != null;
            ApplyFooterTextStyle(footerLabels[i]);
            footerLabels[i].text = items[i].Label;
        }
        LayoutFooter();
    }

    private void ApplyFooterTextStyle(TextMeshProUGUI text)
    {
        if (text == null) return;
        text.font = font;
        if (fontSharedMaterial != null) text.fontSharedMaterial = fontSharedMaterial;
        text.fontSize = footerFontSize;
        text.characterSpacing = footerCharacterSpacing;
        text.wordSpacing = footerWordSpacing;
        text.lineSpacing = footerLineSpacing;
        text.fontStyle = footerFontStyle;
        text.alignment = TextAlignmentOptions.MidlineLeft;
        text.enabled = text.font != null;
    }

    /// <summary>Optional native corner ornaments placed at N/E/S/W on the pixel rim.</summary>
    public void SetMinimapOrnaments(Sprite[] nativeOrnaments)
    {
        ThrowIfDisposed();
        if (nativeOrnaments == null) throw new ArgumentNullException(nameof(nativeOrnaments));
        if (nativeOrnaments.Length > 4) throw new ArgumentException("Provide at most four cardinal ornaments.", nameof(nativeOrnaments));
        foreach (var image in ornaments) Object.Destroy(image.gameObject);
        ornaments.Clear();
        for (int i = 0; i < nativeOrnaments.Length; i++)
        {
            var ornament = CreateImage("Native round ornament " + i, mini.Root);
            ornament.sprite = nativeOrnaments[i];
            ornament.color = UiTint(new Color32(114, 151, 178, 255));
            ornament.preserveAspect = true;
            ornament.enabled = nativeOrnaments[i] != null;
            ornaments.Add(ornament);
        }
        LayoutOrnaments();
    }

    /// <summary>Refresh reference-space geometry after window or aspect-ratio changes.</summary>
    public void RefreshLayout(bool force = false)
    {
        ThrowIfDisposed();
        if (!force && lastScreenWidth == Screen.width && lastScreenHeight == Screen.height) return;
        lastScreenWidth = Screen.width; lastScreenHeight = Screen.height;
        float sw = Screen.width / LogicalScale, sh = Screen.height / LogicalScale;
        MiniRect = new Rect(sw - 20 - 184, 20, 184, 184);
        FullFrameRect = new Rect(32, 106, sw - 64, Math.Max(100, sh - 308));
        FullRect = new Rect(FullFrameRect.x + 20, FullFrameRect.y + 20, FullFrameRect.width - 40, FullFrameRect.height - 40);
        FooterRect = new Rect(300, sh - 180, Math.Max(280, sw - 332), 142);

        SetRect(mini.Root, MiniRect);
        SetRect(mini.Viewport, new Rect(0, 0, MiniRect.width, MiniRect.height));
        SetRect(miniFrame.rectTransform, new Rect(0, 0, MiniRect.width, MiniRect.height));
        var fullContainer = full.Root.parent.Cast<RectTransform>();
        SetRect(fullContainer, FullFrameRect);
        SetRect(fullBackground.rectTransform, new Rect(0, 0, FullFrameRect.width, FullFrameRect.height));
        SetRect(fullOutline.rectTransform, new Rect(0, 0, FullFrameRect.width, FullFrameRect.height));
        SetRect(fullFrame.rectTransform, new Rect(0, 0, FullFrameRect.width, FullFrameRect.height));
        SetRect(full.Root, new Rect(20, 20, FullRect.width, FullRect.height));
        SetRect(full.Viewport, new Rect(0, 0, FullRect.width, FullRect.height));
        SetRect(footerRoot, FooterRect);
        SetRect(footerBackground.rectTransform, new Rect(0, 0, FooterRect.width, FooterRect.height));
        SetRect(footerOutline.rectTransform, new Rect(0, 0, FooterRect.width, FooterRect.height));
        SetRect(footerFrame.rectTransform, new Rect(0, 0, FooterRect.width, FooterRect.height));
        LayoutFooter(); LayoutOrnaments();
    }

    public void ShowMini()
    {
        ThrowIfDisposed(); RefreshLayout();
        if (visibilityMode == 0) return;
        root.SetActive(true); mini.Root.gameObject.SetActive(true);
        full.Root.parent.gameObject.SetActive(false); footerRoot.gameObject.SetActive(false);
        visibilityMode = 0;
    }
    public void ShowFull()
    {
        ThrowIfDisposed(); RefreshLayout();
        if (visibilityMode == 1) return;
        root.SetActive(true); mini.Root.gameObject.SetActive(false);
        full.Root.parent.gameObject.SetActive(true); footerRoot.gameObject.SetActive(true);
        visibilityMode = 1;
    }
    public void Hide()
    {
        if (disposed || visibilityMode == -1 || root == null) return;
        root.SetActive(false);
        visibilityMode = -1;
    }

    private void UpdateView(Layers layers, MapWorldRect view, MapWorldPoint player, MapWorldPoint look, float width, float height)
    {
        if (!view.IsValid) throw new ArgumentException("View rectangle must be finite and positive.", nameof(view));
        if (baseTexture == null || !bounds.IsValid) { layers.Base.enabled = layers.Fog.enabled = layers.Marker.enabled = false; return; }
        double minX = Math.Max(bounds.MinX, view.MinX), maxX = Math.Min(bounds.MaxX, view.MaxX);
        double minY = Math.Max(bounds.MinY, view.MinY), maxY = Math.Min(bounds.MaxY, view.MaxY);
        bool intersects = maxX > minX && maxY > minY;
        layers.Base.enabled = intersects;
        layers.Fog.enabled = intersects && fogTexture != null;
        if (intersects)
        {
            Rect screen = new Rect((float)((minX - view.MinX) / view.Width * width),
                (float)((view.MaxY - maxY) / view.Height * height),
                (float)((maxX - minX) / view.Width * width), (float)((maxY - minY) / view.Height * height));
            Rect uv = new Rect((float)((minX - bounds.MinX) / bounds.Width), (float)((minY - bounds.MinY) / bounds.Height),
                (float)((maxX - minX) / bounds.Width), (float)((maxY - minY) / bounds.Height));
            SetRect(layers.Base.rectTransform, screen); SetRect(layers.Fog.rectTransform, screen);
            layers.Base.uvRect = layers.Fog.uvRect = uv;
        }
        UpdateDetailTiles(layers, view, width, height);
        UpdateLandmarks(layers, view, width, height);
        double u = (player.X - view.MinX) / view.Width, v = (player.Y - view.MinY) / view.Height;
        bool onMap = player.IsFinite && u >= 0 && u <= 1 && v >= 0 && v <= 1;
        layers.Marker.enabled = onMap;
        if (onMap)
        {
            layers.MarkerRect.anchoredPosition = new Vector2((float)(u * width), (float)(-(1 - v) * height));
            // The map is an isometric plan. Facing does not add useful information here;
            // keep the symmetric position crystal upright at all times.
            layers.MarkerRect.localRotation = Quaternion.identity;
        }
    }

    private void RebuildDetailTiles(Layers layers, IReadOnlyList<MapTextureTile> values)
    {
        for (int i = 0; i < values.Count; i++)
        {
            var tile = values[i];
            if (tile == null || tile.Texture == null || !tile.Bounds.IsValid) continue;
            TileVisual visual;
            if (i < layers.DetailTiles.Count) visual = layers.DetailTiles[i];
            else
            {
                RawImage image = CreateRawImage("Pooled 5x detail tile", layers.Viewport);
                image.uvRect = new Rect(0, 0, 1, 1);
                visual = new TileVisual { Image = image };
                layers.DetailTiles.Add(visual);
            }
            visual.Tile = tile;
            visual.Image.texture = tile.Texture;
            visual.Image.enabled = true;
            visual.Image.transform.SetSiblingIndex(1 + i);
        }
        for (int i = values.Count; i < layers.DetailTiles.Count; i++)
        {
            TileVisual visual = layers.DetailTiles[i];
            visual.Tile = null;
            visual.Image.texture = null;
            visual.Image.enabled = false;
        }
        layers.Fog.transform.SetSiblingIndex(1 + layers.DetailTiles.Count);
        foreach (var landmark in layers.Landmarks) landmark.Image.transform.SetAsLastSibling();
        layers.Marker.transform.SetAsLastSibling();
    }

    private static void UpdateDetailTiles(Layers layers, MapWorldRect view, float width, float height)
    {
        foreach (var visual in layers.DetailTiles)
        {
            if (visual.Tile == null || visual.Image == null) continue;
            MapWorldRect tile = visual.Tile.Bounds;
            bool visible = tile.MaxX > view.MinX && tile.MinX < view.MaxX &&
                tile.MaxY > view.MinY && tile.MinY < view.MaxY;
            visual.Image.enabled = visible;
            if (!visible) continue;
            SetRect(visual.Image.rectTransform, new Rect(
                (float)((tile.MinX - view.MinX) / view.Width * width),
                (float)((view.MaxY - tile.MaxY) / view.Height * height),
                (float)(tile.Width / view.Width * width),
                (float)(tile.Height / view.Height * height)));
        }
    }

    private void RebuildLandmarks(Layers layers, IReadOnlyList<MapLandmark> values)
    {
        foreach (var visual in layers.Landmarks)
            if (visual.Image != null) Object.Destroy(visual.Image.gameObject);
        layers.Landmarks.Clear();
        for (int i = 0; i < values.Count; i++)
        {
            MapLandmark landmark = values[i];
            if (landmark == null || !landmark.Position.IsFinite) continue;
            Sprite sprite = landmark.Kind == MapLandmarkKind.Campfire ? campfireMarker : savepointMarker;
            if (sprite == null) continue;
            Image image = CreateImage(landmark.Kind == MapLandmarkKind.Campfire ? "Discovered campfire" : "Discovered savepoint", layers.Viewport);
            image.sprite = sprite;
            image.preserveAspect = true;
            var rect = image.rectTransform;
            rect.anchorMin = rect.anchorMax = new Vector2(0, 1);
            rect.pivot = new Vector2(.5f, .5f);
            rect.sizeDelta = new Vector2(28, 28);
            layers.Landmarks.Add(new LandmarkVisual { Landmark = landmark, Image = image, Rect = rect });
        }
        // The party marker is always the strongest element when positions overlap.
        layers.Marker.transform.SetAsLastSibling();
    }

    private static void UpdateLandmarks(Layers layers, MapWorldRect view, float width, float height)
    {
        foreach (var visual in layers.Landmarks)
        {
            MapWorldPoint point = visual.Landmark.Position;
            double u = (point.X - view.MinX) / view.Width, v = (point.Y - view.MinY) / view.Height;
            bool visible = visual.Landmark.Discovered && u >= 0d && u <= 1d && v >= 0d && v <= 1d;
            visual.Image.enabled = visible;
            if (visible) visual.Rect.anchoredPosition = new Vector2((float)(u * width), (float)(-(1d - v) * height));
        }
    }

    private Layers CreateLayers(string name, RectTransform parent, bool circular, Sprite circle)
    {
        var result = new Layers();
        result.Root = NewObject(name, parent).GetComponent<RectTransform>();
        result.Viewport = NewObject("Clipped map viewport", result.Root).GetComponent<RectTransform>();
        if (circular)
        {
            var maskImage = result.Viewport.gameObject.AddComponent<Image>();
            maskImage.raycastTarget = false;
            maskImage.sprite = circle;
            maskImage.color = UiTint(new Color32(50, 61, 82, 255));
            var mask = result.Viewport.gameObject.AddComponent<Mask>();
            mask.showMaskGraphic = true;
        }
        else result.Viewport.gameObject.AddComponent<RectMask2D>();
        result.Base = CreateRawImage("Immutable scene map", result.Viewport);
        result.Fog = CreateRawImage("Persistent exploration fog", result.Viewport);
        result.Marker = CreateImage("Player position", result.Viewport);
        result.MarkerRect = result.Marker.rectTransform;
        result.MarkerRect.anchorMin = result.MarkerRect.anchorMax = new Vector2(0, 1);
        result.MarkerRect.pivot = new Vector2(.5f, .5f);
        result.MarkerRect.sizeDelta = new Vector2(28, 28);
        return result;
    }

    private void LayoutFooter()
    {
        if (footerItems.Count == 0) return;
        int columns = footerItems.Count <= 3 ? footerItems.Count : (footerItems.Count + 1) / 2;
        float contentWidth = FooterRect.width - 48;
        for (int i = 0; i < footerItems.Count; i++)
        {
            int row = i / columns, column = i % columns;
            float left = Mathf.Round(24 + column * contentWidth / columns);
            float right = Mathf.Round(24 + (column + 1) * contentWidth / columns);
            float cellWidth = right - left;
            SetRect(footerItems[i].GetComponent<RectTransform>(), new Rect(left, 20 + row * 54, cellWidth - 8, 46));
            float inset = 0;
            Sprite icon = footerData[i].Icon;
            if (icon != null)
            {
                // Native controller glyphs are authored at 20x18 and display at 2x.
                // Wider keyboard clusters remain at their source pixel grid so WASD
                // and M/Esc fit without being squeezed into the controller slot.
                float iconScale = icon.rect.height > 21f ? 1f : 2f;
                iconScale = Math.Min(iconScale, 36f / Math.Max(1f, icon.rect.height));
                iconScale = Math.Min(iconScale, 92f / Math.Max(1f, icon.rect.width));
                float iconWidth = Mathf.Round(icon.rect.width * iconScale);
                float iconHeight = Mathf.Round(icon.rect.height * iconScale);
                SetRect(footerIcons[i].rectTransform, new Rect(0, Mathf.Round((46 - iconHeight) / 2), iconWidth, iconHeight));
                inset = iconWidth + 8;
            }
            SetRect(footerLabels[i].rectTransform, new Rect(inset, 0, Math.Max(20, cellWidth - inset - 8), 46));
        }
    }
    private void LayoutOrnaments()
    {
        float d = MiniRect.width;
        var positions = new[] { new Vector2(d / 2, 6), new Vector2(d - 6, d / 2), new Vector2(d / 2, d - 6), new Vector2(6, d / 2) };
        for (int i = 0; i < ornaments.Count; i++)
        {
            Sprite sprite = ornaments[i].sprite;
            float width = sprite != null ? Mathf.Round(sprite.rect.width * 2) : 0;
            float height = sprite != null ? Mathf.Round(sprite.rect.height * 2) : 0;
            SetRect(ornaments[i].rectTransform, new Rect(Mathf.Round(positions[i].x - width / 2), Mathf.Round(positions[i].y - height / 2), width, height));
        }
    }
    private static GameObject NewObject(string name, RectTransform parent)
    {
        var result = new GameObject(name, new[] { Il2CppType.Of<RectTransform>() });
        result.layer = 5;
        if (parent != null) result.transform.SetParent(parent, false);
        result.transform.localScale = Vector3.one;
        return result;
    }
    private static Image CreateImage(string name, RectTransform parent)
    {
        var image = NewObject(name, parent).AddComponent<Image>();
        image.raycastTarget = false; image.color = Color.white;
        return image;
    }
    private static Color UiTint(Color authoredSrgb) =>
        QualitySettings.activeColorSpace == ColorSpace.Linear ? authoredSrgb.linear : authoredSrgb;
    private static RawImage CreateRawImage(string name, RectTransform parent)
    {
        var image = NewObject(name, parent).AddComponent<RawImage>();
        image.raycastTarget = false; image.color = Color.white;
        return image;
    }
    private static void SetRect(RectTransform target, Rect rect)
    {
        target.anchorMin = target.anchorMax = new Vector2(0, 1);
        target.pivot = new Vector2(0, 1);
        target.anchoredPosition = new Vector2(rect.x, -rect.y);
        target.sizeDelta = new Vector2(rect.width, rect.height);
    }

    private Sprite CreateCircleMask()
    {
        const int side = 92;
        var pixels = new Color32[side * side];
        for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
            {
                double dx = x + .5 - side / 2d, dy = y + .5 - side / 2d;
                if (dx * dx + dy * dy <= 44.5 * 44.5) pixels[y * side + x] = new Color32(255, 255, 255, 255);
            }
        return MakeSprite("LocalMap circular stencil", pixels, side, side);
    }
    private Sprite CreateCircleRim()
    {
        const int side = 92;
        var pixels = new Color32[side * side];
        for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
            {
                double dx = x + .5 - side / 2d, dy = y + .5 - side / 2d;
                double distance = Math.Sqrt(dx * dx + dy * dy);
                Color32 color = default;
                if (distance <= 45.5 && distance > 44.5) color = new Color32(13, 22, 37, 255);
                else if (distance <= 44.5 && distance > 43.5) color = new Color32(237, 245, 249, 255);
                else if (distance <= 43.5 && distance > 42.5) color = new Color32(105, 152, 173, 255);
                else if (distance <= 42.5 && distance > 41.5) color = new Color32(35, 52, 73, 255);
                pixels[y * side + x] = color;
            }
        return MakeSprite("LocalMap pixel rim", pixels, side, side);
    }
    private Sprite CreateMarker()
    {
        const int side = 28, center = 14;
        var pixels = new Color32[side * side];
        FillDiamond(pixels, side, center, center, 13, new Color32(12, 19, 32, 255));
        FillDiamond(pixels, side, center, center, 11, new Color32(230, 242, 249, 255));
        FillDiamond(pixels, side, center, center, 8, new Color32(103, 143, 181, 255));
        FillDiamond(pixels, side, center, center, 5, new Color32(16, 40, 58, 255));
        // Symmetric fallback crystal marks position only and never implies facing.
        FillDiamond(pixels, side, center, center, 4, new Color32(39, 221, 236, 255));
        FillDiamond(pixels, side, center, center, 2, new Color32(230, 242, 249, 255));
        pixels[center * side + center] = new Color32(247, 190, 63, 255);
        return MakeSprite("LocalMap position crystal", pixels, side, side);
    }

    private Sprite CreateCampfireMarker()
    {
        const int side = 28, center = 14;
        var pixels = CreateRoundPlaque(side, new Color32(247, 190, 63, 255));
        FillRect(pixels, side, 8, 17, 20, 20, new Color32(102, 54, 31, 255));
        for (int y = 6; y <= 18; y++)
        {
            int half = y < 13 ? Math.Max(1, (y - 4) / 2) : Math.Max(1, (19 - y) / 2 + 2);
            FillRect(pixels, side, center - half, y, center + half, y, new Color32(239, 102, 40, 255));
        }
        FillRect(pixels, side, 13, 10, 15, 17, new Color32(255, 232, 96, 255));
        return MakeSprite("LocalMap campfire marker", pixels, side, side);
    }

    private Sprite CreateSavepointMarker()
    {
        const int side = 28;
        var pixels = CreateRoundPlaque(side, new Color32(39, 221, 236, 255));
        Color32 paper = new Color32(255, 239, 181, 255), ink = new Color32(37, 28, 37, 255);
        FillRect(pixels, side, 7, 10, 13, 18, paper); FillRect(pixels, side, 15, 10, 21, 18, paper);
        FillRect(pixels, side, 13, 11, 15, 19, ink);
        pixels[9 * side + 8] = paper; pixels[9 * side + 20] = paper;
        return MakeSprite("LocalMap savepoint marker", pixels, side, side);
    }

    private static Color32[] CreateRoundPlaque(int side, Color32 rim)
    {
        var pixels = new Color32[side * side];
        double center = (side - 1) * .5;
        for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
            {
                double dx = x - center, dy = y - center, distance = Math.Sqrt(dx * dx + dy * dy);
                if (distance <= 13) pixels[y * side + x] = new Color32(12, 19, 32, 255);
                if (distance <= 11) pixels[y * side + x] = rim;
                if (distance <= 9) pixels[y * side + x] = new Color32(30, 43, 61, 255);
            }
        return pixels;
    }

    private static void FillDiamond(Color32[] pixels, int side, int centerX, int centerY, int radius, Color32 color)
    {
        for (int y = centerY - radius; y <= centerY + radius; y++)
            for (int x = centerX - radius; x <= centerX + radius; x++)
                if (x >= 0 && x < side && y >= 0 && y < side && Math.Abs(x - centerX) + Math.Abs(y - centerY) <= radius)
                    pixels[y * side + x] = color;
    }

    private static void FillRect(Color32[] pixels, int side, int left, int top, int right, int bottom, Color32 color)
    {
        for (int y = Math.Max(0, top); y <= Math.Min(side - 1, bottom); y++)
            for (int x = Math.Max(0, left); x <= Math.Min(side - 1, right); x++) pixels[y * side + x] = color;
    }
    private Sprite MakeSprite(string name, Color32[] pixels, int width, int height)
    {
        var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
        texture.name = name; texture.filterMode = FilterMode.Point; texture.wrapMode = TextureWrapMode.Clamp;
        texture.SetPixels32(pixels); texture.Apply(false, true);
        var sprite = Sprite.Create(texture, new Rect(0, 0, width, height), new Vector2(.5f, .5f), 100f, 0, SpriteMeshType.FullRect);
        sprite.name = name;
        ownedAssets.Add(sprite); ownedAssets.Add(texture);
        return sprite;
    }
    private void ThrowIfDisposed() { if (disposed) throw new ObjectDisposedException(nameof(MapCanvasView)); }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        visibilityMode = -1;
        Object.Destroy(root);
        foreach (var asset in ownedAssets) if (asset != null) Object.Destroy(asset);
        ownedAssets.Clear();
    }
}
