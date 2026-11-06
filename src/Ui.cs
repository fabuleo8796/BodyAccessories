using System;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace BodyAccessories;

/// The panel's building blocks, from Tripod Cam (same glass look, Geist font, accent #3DFF7A): fonts, rounded sprites,
/// labels, buttons, sliders, segmented bars, switches, and a toast for messages while the panel is closed.
internal static class Ui
{
    internal static readonly Color Accent = new(0.239f, 1f, 0.478f, 1f);
    internal static readonly Color AccentText = new(0.024f, 0.17f, 0.08f, 1f);
    internal static readonly Color Back = new(0.05f, 0.11f, 0.12f, 0.88f);
    internal static readonly Color Soft = new(1f, 1f, 1f, 0.09f);
    internal static readonly Color Glass = new(1f, 1f, 1f, 0.14f);
    internal static readonly Color Dim = new(1f, 1f, 1f, 0.5f);
    internal static readonly Color Ink = new(1f, 1f, 1f, 0.9f);
    internal static TMP_FontAsset semiBold, medium;
    internal static Sprite rounded, small, circle;
    static bool built;
    static GameObject toast;
    static RectTransform toastChip;
    static TextMeshProUGUI toastText;
    static CanvasGroup toastGroup;
    static float toastUntil;

    public static void Build()
    {
        if (built) return;
        built = true;
        semiBold = Font("SemiBold");
        medium = Font("Medium");
        rounded = MakeRounded(64, 24);
        small = MakeRounded(40, 12);
        circle = MakeRounded(64, 32);

        toast = new GameObject("BodyAccessories Toast");
        Object.DontDestroyOnLoad(toast);
        var canvas = toast.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 30030;
        var scaler = toast.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;
        toastChip = Node(toast.transform, "Toast");
        Anchor(toastChip, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -96f), new Vector2(200f, 46f));
        Fill(toastChip, rounded, new Color(0.06f, 0.07f, 0.1f, 0.8f));
        toastText = Label(toastChip, "", medium, 19f, Color.white, 0f, 0f, 200f, 46f, TextAlignmentOptions.Center);
        toastGroup = toast.AddComponent<CanvasGroup>();
        toastGroup.blocksRaycasts = false;
        toastGroup.interactable = false;
        toast.SetActive(false);
    }

    public static void Toast(string text)
    {
        Build();
        toastText.text = text;
        float w = toastText.GetPreferredValues(text).x + 44f;
        toastChip.sizeDelta = new Vector2(w, 46f);
        toastText.rectTransform.sizeDelta = new Vector2(w, 46f);
        toastUntil = Time.unscaledTime + 2.2f;
        toastGroup.alpha = 1f;
        toast.SetActive(true);
    }

    public static void Tick()
    {
        if (toast == null || !toast.activeSelf) return;
        float left = toastUntil - Time.unscaledTime;
        if (left <= 0f) { toast.SetActive(false); return; }
        toastGroup.alpha = Mathf.Clamp01(left / 0.4f);
    }

    // ----- layout -----

    internal static RectTransform Node(Transform parent, string name)
    {
        var go = new GameObject(name);
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(parent, false);
        return rt;
    }

    internal static void Anchor(RectTransform rt, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size)
    {
        rt.anchorMin = rt.anchorMax = anchor;
        rt.pivot = pivot;
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
    }

    /// A child laid out from the parent's top-left corner, y going down (like the mockup).
    internal static RectTransform At(Transform parent, string name, float x, float y, float w, float h)
    {
        var rt = Node(parent, name);
        Anchor(rt, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(x, -y), new Vector2(w, h));
        return rt;
    }

    internal static Image Fill(RectTransform rt, Sprite sprite, Color color, bool raycast = false)
    {
        var image = rt.gameObject.AddComponent<Image>();
        if (sprite != null) { image.sprite = sprite; image.type = Image.Type.Sliced; }
        image.color = color;
        image.raycastTarget = raycast;
        return image;
    }

    internal static TextMeshProUGUI Label(Transform parent, string text, TMP_FontAsset font, float size, Color color, float x, float y, float w, float h,
                                          TextAlignmentOptions align = TextAlignmentOptions.MidlineLeft)
    {
        var rt = At(parent, "Text", x, y, w, h);
        var label = rt.gameObject.AddComponent<TextMeshProUGUI>();
        if (font != null) label.font = font;
        label.fontSize = size;
        label.color = color;
        label.alignment = align;
        label.enableWordWrapping = false;
        label.overflowMode = TextOverflowModes.Overflow;
        label.raycastTarget = false;
        label.text = text;
        return label;
    }

    internal static void Click(GameObject go, Graphic target, Action click)
    {
        var button = go.AddComponent<Button>();
        button.transition = Selectable.Transition.None;
        button.navigation = new Navigation { mode = Navigation.Mode.None };
        button.targetGraphic = target;
        target.raycastTarget = true;
        button.onClick.AddListener((UnityAction)new Action(() => Safe(click)));
    }

    internal static void Section(Transform parent, string text, float x, float y, float w)
    {
        var label = Label(parent, text, semiBold, 14f, new Color(1f, 1f, 1f, 0.55f), x, y, w, 20f);
        label.characterSpacing = 5f;
    }

    /// A key cap ("J") on a glass chip, sized to its text.
    internal static RectTransform KeyCap(Transform parent, string text, float x, float y, out TextMeshProUGUI label)
    {
        var chip = At(parent, "Key", x, y, 34f, 30f);
        Fill(chip, small, new Color(1f, 1f, 1f, 0.2f), true);
        label = Label(chip, text, semiBold, 16f, Color.white, 0f, 0f, 34f, 30f, TextAlignmentOptions.Center);
        float w = Mathf.Max(34f, label.GetPreferredValues(text).x + 20f);
        chip.sizeDelta = new Vector2(w, 30f);
        label.rectTransform.sizeDelta = new Vector2(w, 30f);
        return chip;
    }

    /// A slider on a rounded track; its knob is white. track = an optional picture for the track (hue, saturation).
    internal static Slider MakeSlider(Transform parent, float x, float y, float w, float min, float max, Action<float> changed, out Image track)
    {
        var area = At(parent, "Slider", x, y, w, 28f);
        Fill(area, null, Color.clear, true);
        track = Fill(At(area, "Track", 0f, 9f, w, 10f), small, new Color(1f, 1f, 1f, 0.16f));
        var fillArea = At(area, "Fill Area", 0f, 9f, w, 10f);
        var fill = Node(fillArea, "Fill");
        Fill(fill, small, Accent);
        fill.anchorMin = Vector2.zero; fill.anchorMax = new Vector2(0f, 1f); fill.offsetMin = Vector2.zero; fill.offsetMax = Vector2.zero;
        var handleArea = At(area, "Handle Area", 11f, 0f, w - 22f, 28f);
        var knob = Node(handleArea, "Knob");
        var knobImage = Fill(knob, circle, Color.white);

        var slider = area.gameObject.AddComponent<Slider>();
        slider.transition = Selectable.Transition.None;
        slider.navigation = new Navigation { mode = Navigation.Mode.None };
        slider.fillRect = fill;
        slider.handleRect = knob;
        slider.targetGraphic = knobImage;
        slider.direction = Slider.Direction.LeftToRight;
        slider.minValue = min;
        slider.maxValue = max;
        knob.sizeDelta = new Vector2(22f, -6f); // the slider stretches the knob over the area's height: 28 - 6 = 22
        knob.anchoredPosition = Vector2.zero;
        slider.onValueChanged.AddListener((UnityAction<float>)new Action<float>(v => Safe(() => changed(v))));
        return slider;
    }

    internal static void EnsureEventSystem()
    {
        if (EventSystem.current != null) return;
        var go = new GameObject("BodyAccessories Events");
        Object.DontDestroyOnLoad(go);
        go.AddComponent<EventSystem>();
        go.AddComponent<InputSystemUIInputModule>().AssignDefaultActions();
        Plugin.Logger.LogInfo("No EventSystem in this scene: added one for the Body Accessories panel");
    }

    internal static void Safe(Action action)
    {
        try { action(); }
        catch (Exception e) { Plugin.Logger.LogError($"panel: {e}"); }
    }

    internal static string KeyName(Key key)
    {
        string s = key.ToString();
        if (s.StartsWith("Digit")) return s.Substring(5);
        if (s.StartsWith("Numpad")) return "Num " + s.Substring(6);
        return s;
    }

    // ----- fonts and sprites (from StickerSwap and Tripod Cam) -----

    /// Geist as a dynamic TMP font; falls back to the game's own font.
    static TMP_FontAsset Font(string weight)
    {
        TMP_FontAsset game = null;
        try
        {
            foreach (var f in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
                if (f != null && (game == null || f.name.StartsWith("Jersey10"))) game = f;
            var folder = Path.Combine(Plugin.ConfigFolder, "fonts");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"Geist-{weight}.ttf");
            if (!File.Exists(path)) File.WriteAllBytes(path, Resource($"Geist-{weight}.ttf"));
            var license = Path.Combine(folder, "OFL-Geist.txt");
            if (!File.Exists(license)) File.WriteAllBytes(license, Resource("OFL-Geist.txt"));
            var font = TMP_FontAsset.CreateFontAsset(path, 0, 72, 6, GlyphRenderMode.SDFAA, 1024, 1024, AtlasPopulationMode.Dynamic, true);
            if (font == null) return game;
            font.name = $"Geist-{weight} (BodyAccessories)";
            font.hideFlags = HideFlags.HideAndDontSave;
            var shader = font.material != null ? font.material.shader : null;
            if ((shader == null || !shader.isSupported) && game != null && game.material != null) font.material.shader = game.material.shader;
            if (game != null) font.fallbackFontAssetTable?.Add(game);
            return font;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Couldn't load Geist {weight}, using the game's font: {e.Message}");
            return game;
        }
    }

    static byte[] Resource(string name)
    {
        using var stream = typeof(Ui).Assembly.GetManifestResourceStream(name);
        using var ms = new MemoryStream();
        stream!.CopyTo(ms);
        return ms.ToArray();
    }

    internal static Sprite MakeRounded(int size, int radius)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        var pixels = new Color32[size * size];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            float cx = Mathf.Clamp(x + 0.5f, radius, size - radius), cy = Mathf.Clamp(y + 0.5f, radius, size - radius);
            var d = Mathf.Sqrt((x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy));
            pixels[y * size + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(radius - d + 0.5f) * 255));
        }
        tex.SetPixels32(pixels);
        tex.Apply(false, true);
        tex.hideFlags = HideFlags.HideAndDontSave;
        var border = new Vector4(radius, radius, radius, radius);
        var sprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, border);
        sprite.hideFlags = HideFlags.HideAndDontSave;
        return sprite;
    }

    /// A 1-pixel-high picture for a gradient (the colour sliders' tracks, the gradient bar).
    internal static Texture2D Strip(int width)
    {
        var tex = new Texture2D(width, 1, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        tex.hideFlags = HideFlags.HideAndDontSave;
        return tex;
    }

    // ----- pieces -----

    internal sealed class Segmented
    {
        readonly Image[] backs;
        readonly TextMeshProUGUI[] labels;

        public Segmented(Transform parent, float x, float y, float w, float h, string[] names, Action<int> pick, float fontSize = 17f)
        {
            var bar = At(parent, "Segmented", x, y, w, h);
            Fill(bar, small, Soft);
            backs = new Image[names.Length];
            labels = new TextMeshProUGUI[names.Length];
            float sw = (w - 8f) / names.Length;
            for (int i = 0; i < names.Length; i++)
            {
                int index = i;
                var seg = At(bar, names[i], 4f + i * sw, 4f, sw, h - 8f);
                backs[i] = Fill(seg, small, Color.clear, true);
                labels[i] = Label(seg, names[i], semiBold, fontSize, Ink, 0f, 0f, sw, h - 8f, TextAlignmentOptions.Center);
                Click(seg.gameObject, backs[i], () => pick(index));
            }
        }

        public void Select(int index)
        {
            for (int i = 0; i < backs.Length; i++) Paint(i, i == index);
        }

        public void Toggles(params bool[] on)
        {
            for (int i = 0; i < backs.Length; i++) Paint(i, on[i]);
        }

        void Paint(int i, bool on)
        {
            backs[i].color = on ? Accent : Color.clear;
            labels[i].color = on ? AccentText : Ink;
        }
    }

    internal sealed class Switch
    {
        readonly Image track;
        readonly RectTransform knob;

        public Switch(Transform parent, float x, float y, Action click)
        {
            var row = At(parent, "Switch", x, y, 54f, 30f);
            track = Fill(row, small, Accent, true);
            knob = At(row, "Knob", 3f, 3f, 24f, 24f);
            Fill(knob, circle, Color.white);
            Click(row.gameObject, track, click);
        }

        public void Set(bool on)
        {
            track.color = on ? Accent : new Color(1f, 1f, 1f, 0.2f);
            knob.anchoredPosition = new Vector2(on ? 27f : 3f, -3f);
        }
    }
}
