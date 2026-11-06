using System;
using System.Globalization;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace BodyAccessories;

/// The Body Accessories panel (X), on the right like Flippy Auras' so Flippy stays in view: your accessories (wear
/// each one or not, Edit opens it in Blender), New accessory, the look, and the Flippy model Blender uses. Built a few
/// seconds after launch (kit trap 4); while open it frees the cursor and pauses the game's controls (traps 6, 7).
internal static class Panel
{
    const float W = 460f, H = 990f, Pad = 24f, Inner = W - 2 * Pad, Margin = 40f, RowH = 62f, RowGap = 8f;
    const int MaxRows = 6;
    static GameObject root;
    static RectTransform panel, list;
    static Ui.Switch master, outline;
    static Ui.Segmented look;
    static TextMeshProUGUI keyText, flippyText, footText, moreText, blenderText, blenderButton;
    static RectTransform progressFill, progressBack;
    static bool wasBusy;
    static string confirmDelete;
    static float slideStart = -1f, nextRefresh;
    static CursorLockMode oldLock;
    static bool oldVisible;
    static string blenderPath;

    public static bool IsOpen => root != null && root.activeSelf;

    public static void Build()
    {
        if (root != null) return;
        Ui.Build();
        try
        {
            root = new GameObject("BodyAccessories Panel");
            Object.DontDestroyOnLoad(root);
            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 30026;
            var scaler = root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            root.AddComponent<GraphicRaycaster>();
            panel = Ui.Node(root.transform, "Panel");
            Ui.Anchor(panel, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-Margin, -38f), new Vector2(W, H));
            Ui.Fill(panel, Ui.rounded, Ui.Back, true);

            Ui.Label(panel, "Body Accessories", Ui.semiBold, 28f, Color.white, Pad, 16f, 300f, 40f);
            master = new Ui.Switch(panel, W - Pad - 34f - 12f - 54f, 21f, () => { Plugin.Enabled.Value = !Plugin.Enabled.Value; Refresh(); });
            var close = Ui.KeyCap(panel, Ui.KeyName(Plugin.OpenKey.Value), W - Pad - 34f, 21f, out keyText);
            Ui.Click(close.gameObject, close.GetComponent<Image>(), Close);

            Ui.Section(panel, "YOUR ACCESSORIES", Pad, 72f, Inner);
            list = Ui.At(panel, "List", Pad, 98f, Inner, MaxRows * (RowH + RowGap));
            moreText = Ui.Label(panel, "", Ui.medium, 14f, Ui.Dim, Pad, 98f + MaxRows * (RowH + RowGap) - 4f, Inner, 20f);

            float by = 98f + MaxRows * (RowH + RowGap) + 18f;
            Button(Pad, by, Inner * 0.58f, "+ New accessory", true, () => WearDriver.Instance?.NewAccessory());
            Button(Pad + Inner * 0.58f + 10f, by, Inner * 0.42f - 10f, "Open folder", false, OpenFolder);

            Ui.Section(panel, "LOOK", Pad, by + 64f, Inner);
            look = new Ui.Segmented(panel, Pad, by + 88f, Inner, 40f, new[] { "Game", "Lit", "Flat colour" }, i =>
            {
                Plugin.Look.Value = i;
                WearDriver.Instance?.Rebuild();
                Refresh();
            }, 16f);
            Ui.Label(panel, "Toon outline", Ui.medium, 17f, Ui.Ink, Pad, by + 140f, 260f, 30f);
            outline = new Ui.Switch(panel, W - Pad - 54f, by + 140f, () =>
            {
                Plugin.Outline.Value = !Plugin.Outline.Value;
                WearDriver.Instance?.Rebuild();
                Refresh();
            });

            Ui.Section(panel, "FLIPPY IN BLENDER", Pad, by + 192f, Inner);
            flippyText = Ui.Label(panel, "", Ui.medium, 15f, Ui.Ink, Pad, by + 216f, Inner - 150f, 40f);
            flippyText.enableWordWrapping = true;
            Button(W - Pad - 140f, by + 214f, 140f, "Copy again", false, () => WearDriver.Instance?.RefreshFlippy());

            Ui.Section(panel, "BLENDER", Pad, by + 276f, Inner);
            blenderText = Ui.Label(panel, "", Ui.medium, 15f, Ui.Ink, Pad, by + 300f, Inner - 186f, 44f);
            blenderText.enableWordWrapping = true;
            var bb = Button(W - Pad - 176f, by + 298f, 176f, "Download", true, BlenderButton);
            blenderButton = bb.GetComponentInChildren<TextMeshProUGUI>();
            progressBack = Ui.At(panel, "Progress", Pad, by + 352f, Inner, 6f);
            Ui.Fill(progressBack, Ui.small, new Color(1f, 1f, 1f, 0.12f));
            progressFill = Ui.At(progressBack, "Fill", 0f, 0f, 0f, 6f);
            Ui.Fill(progressFill, Ui.small, Ui.Accent);

            footText = Ui.Label(panel, "", Ui.medium, 13.5f, new Color(1f, 1f, 1f, 0.45f), Pad, H - 66f, Inner, 50f);
            footText.enableWordWrapping = true;
            root.SetActive(false);
            Plugin.Logger.LogInfo("Body Accessories panel ready");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError("couldn't build the panel: " + e);
            if (root != null) Object.Destroy(root);
            root = null;
        }
    }

    static RectTransform Button(float x, float y, float w, string text, bool accent, Action click)
    {
        var b = Ui.At(panel, text, x, y, w, 44f);
        var back = Ui.Fill(b, Ui.small, accent ? Ui.Accent : Ui.Glass, true);
        Ui.Label(b, text, Ui.semiBold, 17f, accent ? Ui.AccentText : Color.white, 0f, 0f, w, 44f, TextAlignmentOptions.Center);
        Ui.Click(b.gameObject, back, click);
        return b;
    }

    public static void Open()
    {
        if (root == null) return;
        Ui.EnsureEventSystem();
        Refresh();
        root.SetActive(true);
        panel.anchoredPosition = new Vector2(W + Margin, -38f);
        slideStart = -1f;
        GameControls.Pause();
        oldLock = Cursor.lockState; oldVisible = Cursor.visible;
        Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
    }

    public static void Close()
    {
        if (!IsOpen) return;
        root.SetActive(false);
        GameControls.Resume();
        Cursor.lockState = oldLock; Cursor.visible = oldVisible;
    }

    public static void Tick(Keyboard kb)
    {
        if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None; // the game grabs it back
        if (!Cursor.visible) Cursor.visible = true;
        if (slideStart < 0f) slideStart = Time.unscaledTime;
        float t = Math.Min(1f, (Time.unscaledTime - slideStart) / 0.22f);
        float ease = 1f - (1f - t) * (1f - t) * (1f - t);
        panel.anchoredPosition = new Vector2(W + Margin - (W + 2f * Margin) * ease, -38f);
        if (kb != null && kb.escapeKey.wasPressedThisFrame) { Close(); return; }
        if (Time.unscaledTime >= nextRefresh) Status();
    }

    /// Rebuilds the list and puts every control in line with the settings.
    public static void Refresh()
    {
        if (root == null) return;
        master.Set(Plugin.Enabled.Value);
        keyText.text = Ui.KeyName(Plugin.OpenKey.Value);
        look.Select(Math.Clamp(Plugin.Look.Value, 0, 2));
        outline.Set(Plugin.Outline.Value);
        for (int i = list.childCount - 1; i >= 0; i--) Object.Destroy(list.GetChild(i).gameObject);
        var driver = WearDriver.Instance;
        var names = driver != null ? driver.Names() : new System.Collections.Generic.List<string>();
        for (int i = 0; i < Math.Min(MaxRows, names.Count); i++) Row(i, names[i], driver.Get(names[i]));
        if (names.Count == 0)
            Ui.Label(list, "No accessories yet. Press + New accessory to make one in Blender.", Ui.medium, 15f, Ui.Dim, 4f, 10f, Inner - 8f, 50f).enableWordWrapping = true;
        moreText.text = names.Count > MaxRows ? $"+{names.Count - MaxRows} more in the folder (all worn)" : "";
        Status();
    }

    static void Row(int i, string name, WearDriver.Item item)
    {
        var row = Ui.At(list, name, 0f, i * (RowH + RowGap), Inner, RowH);
        Ui.Fill(row, Ui.small, Ui.Soft);
        var n = Ui.Label(row, name, Ui.semiBold, 18f, Color.white, 16f, 8f, Inner - 230f, 26f);
        n.overflowMode = TextOverflowModes.Ellipsis;
        string sub = item == null ? "not exported yet" : item.Error != null ? "couldn't read: " + item.Error
            : item.Loading != null ? "loading…" : $"{item.Tris.ToString("N0", CultureInfo.InvariantCulture)} triangles";
        Ui.Label(row, sub, Ui.medium, 13.5f, item?.Error != null ? new Color(1f, 0.55f, 0.5f, 1f) : Ui.Dim, 16f, 34f, Inner - 230f, 20f).overflowMode = TextOverflowModes.Ellipsis;
        // delete: the first click asks, the second moves it to the trash folder
        var del = Ui.At(row, "Delete", Inner - 202f, 15f, 32f, 32f);
        bool asking = confirmDelete == name;
        var db = Ui.Fill(del, Ui.circle, asking ? new Color(1f, 0.3f, 0.25f, 0.9f) : new Color(1f, 1f, 1f, 0.1f), true);
        Ui.Label(del, asking ? "?" : "×", Ui.semiBold, 19f, Color.white, 0f, -1f, 32f, 32f, TextAlignmentOptions.Center);
        Ui.Click(del.gameObject, db, () =>
        {
            if (confirmDelete != name) { confirmDelete = name; Ui.Toast($"Click the ? again to delete {name}"); Refresh(); return; }
            confirmDelete = null;
            WearDriver.Instance?.Delete(name);
            Refresh();
        });
        var edit = Ui.At(row, "Edit", Inner - 160f, 11f, 86f, 40f);
        var eb = Ui.Fill(edit, Ui.small, Ui.Glass, true);
        Ui.Label(edit, "Edit", Ui.semiBold, 16f, Color.white, 0f, 0f, 86f, 40f, TextAlignmentOptions.Center);
        Ui.Click(edit.gameObject, eb, () => WearDriver.Instance?.Edit(name));
        var sw = new Ui.Switch(row, Inner - 66f, 16f, () => { WearDriver.Instance?.SetWorn(name, !WearDriver.IsWorn(name)); Refresh(); });
        sw.Set(WearDriver.IsWorn(name));
    }

    static void Status()
    {
        nextRefresh = Time.unscaledTime + 0.5f;
        if (flippyText == null) return;
        flippyText.text = FlippyModel.Busy ? "Copying Flippy from your game…"
            : File.Exists(FlippyModel.ObjPath) ? "<color=#3DFF7A>Real Flippy</color>, copied from your game"
            : "The stand-in (the real one is copied on your first Edit)";
        var driver = WearDriver.Instance;
        if (blenderPath == null || BlenderGet.Busy || wasBusy) blenderPath = WearDriver.FindBlender() ?? "";
        wasBusy = BlenderGet.Busy;
        var blender = blenderPath.Length > 0 ? blenderPath : null;
        bool ours = blender != null && string.Equals(blender, BlenderGet.Exe, StringComparison.OrdinalIgnoreCase);
        if (BlenderGet.Busy)
        {
            blenderText.text = BlenderGet.Status;
            blenderButton.text = "Cancel";
        }
        else if (blender == null)
        {
            blenderText.text = (WearDriver.OldBlender != null ? $"<color=#FF8A80>Your Blender {WearDriver.OldBlender} is too old</color> (4.0+). " : "<color=#FF8A80>No Blender found.</color> ")
                               + (BlenderGet.Status.StartsWith("Download failed") ? BlenderGet.Status : $"Get the official portable Blender {BlenderGet.Version} (about {BlenderGet.Size / 1048576} MB, no installer).");
            blenderButton.text = "Download";
        }
        else
        {
            blenderText.text = ours ? $"<color=#3DFF7A>Blender {BlenderGet.Version}</color>, downloaded by Body Accessories" : "<color=#3DFF7A>Found:</color> " + ShortPath(blender);
            blenderButton.text = ours ? "Remove it" : "Use another…";
        }
        progressBack.gameObject.SetActive(BlenderGet.Busy);
        progressFill.sizeDelta = new Vector2(Inner * (BlenderGet.Busy ? BlenderGet.Progress : 0f), 6f);
        footText.text = "Edit opens Blender. Export to FIH (or Ctrl+S) there puts it on Flippy live."
                        + (driver != null ? $"\n{driver.LastEvent}" : "");
    }

    /// Download (none found), Cancel (downloading), Remove it (ours), or say how to point at another Blender.
    static void BlenderButton()
    {
        if (BlenderGet.Busy) { BlenderGet.Cancel(); return; }
        var blender = WearDriver.FindBlender();
        if (blender == null) { BlenderGet.Start(); Status(); return; }
        if (string.Equals(blender, BlenderGet.Exe, StringComparison.OrdinalIgnoreCase)) { BlenderGet.Remove(); blenderPath = null; Status(); return; }
        Ui.Toast($"To use another Blender, set [Blender] Path in BepInEx\\config\\{Plugin.Guid}.cfg");
    }

    static string ShortPath(string p)
    {
        var parts = p.Split('\\');
        return parts.Length > 3 ? "…\\" + string.Join("\\", parts[^3..]) : p;
    }

    static void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(Plugin.AccessoryFolder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = Plugin.DataFolder, UseShellExecute = true });
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning("couldn't open the folder: " + e.Message);
        }
    }
}
