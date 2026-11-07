using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;
using UnityEngine.InputSystem;

namespace BodyAccessories;

/// Keys, Blender, and the live link: every half second it looks at the accessories folder; a new or changed .glb is
/// read on a background thread, then built and put on Flippy on the main thread. It also follows Flippy through
/// respawns and level changes, and hides the accessories while the phone is destroyed. The panel drives it.
public class WearDriver : MonoBehaviour
{
    public WearDriver(IntPtr ptr) : base(ptr) { }

    internal static WearDriver Instance;

    internal sealed class Item
    {
        public string Name = "";
        public string Glb = "";
        public DateTime Stamp;
        public long Size;
        public GameObject Go;
        public List<Part> Parts; // kept: a level change destroys Flippy, and his accessories with him
        public Task<(List<Part> parts, Anim anim)> Loading;
        public Anim Anim;          // its movement and videos, if it has any
        public AnimPlayer Player;
        public int Tris;
        public string Error;
    }

    readonly Dictionary<string, Item> items = new(StringComparer.OrdinalIgnoreCase);
    internal IEnumerable<Item> Items => items.Values;
    float nextScan;
    IntPtr player;
    Transform visual;
    bool shown = true, panelBuilt;
    Process blender;
    internal string LastEvent = "Waiting for an export from Blender";

    void Awake()
    {
        Instance = this;
        SyncLink.Start();
    }

    void Update()
    {
        try
        {
            if (!panelBuilt && Time.realtimeSinceStartup > 6f) { panelBuilt = true; Panel.Build(); }
            var kb = Keyboard.current;
            GameControls.Tick(kb, Mouse.current);
            Ui.Tick();
            FlippyModel.Tick();
            if (Panel.IsOpen) Panel.Tick(kb);
            Keys(kb);
            if (Time.unscaledTime >= nextScan) { nextScan = Time.unscaledTime + 0.5f; Scan(); }
            Finish();
            Follow();
            Animate();
            SyncLink.Tick();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError("update: " + e);
        }
    }

    [HideFromIl2Cpp]
    void Keys(Keyboard kb)
    {
        if (kb == null || !kb[Plugin.OpenKey.Value].wasPressedThisFrame) return;
        if (kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed)
        {
            Plugin.Enabled.Value = !Plugin.Enabled.Value;
            Ui.Toast(Plugin.Enabled.Value ? "Accessories on" : "Accessories off");
            Panel.Refresh();
        }
        else if (Panel.IsOpen) Panel.Close();
        else if (Cursor.lockState == CursorLockMode.Locked) // playing, not in a menu or chat
        {
            if (!panelBuilt) { panelBuilt = true; Panel.Build(); }
            Panel.Open();
        }
    }

    // ---------------------------------------------------------------- what the panel asks for

    /// Every accessory there is: a .glb (worn) or a .blend not exported yet.
    [HideFromIl2Cpp]
    internal List<string> Names()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in items.Values) names.Add(it.Name);
        var blendDir = Path.Combine(Plugin.DataFolder, "blender");
        if (Directory.Exists(blendDir))
            foreach (var b in Directory.GetFiles(blendDir, "*.blend")) names.Add(Path.GetFileNameWithoutExtension(b));
        return names.ToList();
    }

    [HideFromIl2Cpp]
    internal Item Get(string name) => items.Values.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));

    [HideFromIl2Cpp]
    internal static bool IsWorn(string name) => !HiddenSet().Contains(name);

    [HideFromIl2Cpp]
    internal void SetWorn(string name, bool on)
    {
        var set = HiddenSet();
        if (on) set.Remove(name); else set.Add(name);
        Plugin.Hidden.Value = string.Join("|", set);
        Apply();
    }

    static HashSet<string> HiddenSet() => new(Plugin.Hidden.Value.Split('|', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);

    /// Rebuilds every accessory (after the look or the outline changed).
    [HideFromIl2Cpp]
    internal void Rebuild()
    {
        foreach (var it in items.Values)
        {
            if (it.Parts == null) continue;
            Make(it);
        }
    }

    /// Takes an accessory off Flippy and moves its files (.glb, .blend, Blender's .blend1 backup) to
    /// config\BodyAccessories\trash\<time>\, so a mis-click can still be undone by hand.
    [HideFromIl2Cpp]
    internal void Delete(string name)
    {
        try
        {
            var trash = Path.Combine(Plugin.DataFolder, "trash", DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
            // name.glb, name.anim.json and its videos (name.<material>.mp4), plus the .blend and its backup
            var files = (Directory.Exists(Plugin.AccessoryFolder) ? Directory.GetFiles(Plugin.AccessoryFolder, name + ".*") : Array.Empty<string>())
                .Concat(new[] { Path.Combine(Plugin.DataFolder, "blender", name + ".blend"), Path.Combine(Plugin.DataFolder, "blender", name + ".blend1") })
                .Where(File.Exists).ToList();
            if (files.Count > 0) Directory.CreateDirectory(trash);
            foreach (var f in files) File.Move(f, Path.Combine(trash, Path.GetFileName(f)));
            var it = Get(name);
            if (it != null)
            {
                Unmake(it);
                items.Remove(it.Glb);
            }
            Ui.Toast($"Deleted {name} (it's in the trash folder, just in case)");
            Plugin.Logger.LogInfo($"deleted {name}: moved {files.Count} file(s) to {trash}");
        }
        catch (Exception e)
        {
            Ui.Toast($"Couldn't delete {name}: {e.Message} (is it open in Blender?)");
        }
    }

    [HideFromIl2Cpp]
    internal void NewAccessory()
    {
        var names = Names();
        int n = 1;
        while (names.Contains($"accessory {n}", StringComparer.OrdinalIgnoreCase)) n++;
        Edit($"accessory {n}");
    }

    [HideFromIl2Cpp]
    internal void RefreshFlippy()
    {
        if (visual == null) { Ui.Toast("Get into a level first"); return; }
        FlippyModel.Start(visual, ok => { Ui.Toast(ok ? "Flippy model copied from your game" : "Couldn't copy Flippy: " + FlippyModel.Status); Panel.Refresh(); });
    }

    // ---------------------------------------------------------------- Blender

    /// Opens an accessory in Blender (made from the template first if it's new). The first time, the real Flippy is
    /// copied out of the game so the template can use him.
    [HideFromIl2Cpp]
    internal void Edit(string name)
    {
        name = SafeName(name);
        if (blender != null && !blender.HasExited) { Ui.Toast("Blender is already open: close it to edit another one"); return; }
        if (!File.Exists(FlippyModel.ObjPath) && visual != null && !FlippyModel.Busy)
        {
            Ui.Toast("Copying Flippy from your game…");
            FlippyModel.Start(visual, _ => Launch(name));
            return;
        }
        Launch(name);
    }

    [HideFromIl2Cpp]
    void Launch(string name)
    {
        var exe = FindBlender();
        if (exe == null)
        {
            Ui.Toast(OldBlender != null ? $"Your Blender {OldBlender} is too old: download Blender {BlenderGet.Version} in the panel"
                                        : $"No Blender found: download Blender {BlenderGet.Version} in the panel (one click)");
            Plugin.Logger.LogWarning("no usable blender.exe found" + (OldBlender != null ? $" (only {OldBlender}, older than 4.0)" : ""));
            Panel.Open();
            return;
        }
        var blendDir = Path.Combine(Plugin.DataFolder, "blender");
        Directory.CreateDirectory(blendDir);
        Directory.CreateDirectory(Plugin.AccessoryFolder);
        var blend = Path.Combine(blendDir, name + ".blend");
        if (!File.Exists(blend)) File.Copy(Path.Combine(Plugin.Folder, "template.blend"), blend);
        var script = Path.Combine(Plugin.Folder, "fih_accessory.py");
        var args = $"\"{blend}\" --python \"{script}\" -- --out \"{Plugin.AccessoryFolder}\" --name \"{name}\" --flippy \"{FlippyModel.ObjPath}\"";
        blender = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, WorkingDirectory = blendDir });
        Plugin.Logger.LogInfo($"opened Blender: {exe} {args}");
        Ui.Toast($"Opening {name} in Blender… Export (or Ctrl+S) puts it on Flippy");
        Panel.Close();
    }

    /// A Blender older than 4.0 that was found (the mod's Blender side is tested on 4.0 to 5.2), or null.
    internal static string OldBlender;

    /// The Blender to use: the one set in the config, else the newest installed one that's 4.0 or newer, else the one
    /// the panel downloaded, else Steam's. Null when there's none (the panel then offers the download).
    [HideFromIl2Cpp]
    internal static string FindBlender()
    {
        OldBlender = null;
        var set = Plugin.BlenderPath.Value.Trim().Trim('"');
        if (set.Length > 0 && File.Exists(set)) return set;
        var found = new List<string>();
        foreach (var root in new[] { @"C:\Program Files\Blender Foundation", @"C:\Program Files (x86)\Blender Foundation" })
            if (Directory.Exists(root))
                found.AddRange(Directory.GetDirectories(root).Select(d => Path.Combine(d, "blender.exe")).Where(File.Exists));
        // the newest version: folders are named "Blender 4.0", "Blender 5.2"... (0 = can't tell)
        var best = found.OrderByDescending(p => Version(Path.GetFileName(Path.GetDirectoryName(p)))).FirstOrDefault();
        double v = best != null ? Version(Path.GetFileName(Path.GetDirectoryName(best))) : 0;
        if (best != null && v >= 4.0) return best;
        if (BlenderGet.Installed) return BlenderGet.Exe;
        var steam = @"C:\Program Files (x86)\Steam\steamapps\common\Blender\blender.exe";
        if (File.Exists(steam)) return steam; // Steam keeps its Blender up to date
        if (best != null && v > 0) { OldBlender = v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture); return null; }
        return best; // a version we can't read from its folder name: try it
    }

    static double Version(string folder)
    {
        var digits = new string(folder.SkipWhile(c => !char.IsDigit(c)).ToArray());
        return double.TryParse(digits, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    static string SafeName(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        var clean = new string((s ?? "").Trim().Select(c => bad.Contains(c) ? '_' : c).ToArray());
        return clean.Length > 0 ? clean : "my_accessory";
    }

    // ---------------------------------------------------------------- the live link

    [HideFromIl2Cpp]
    void Scan()
    {
        if (!Directory.Exists(Plugin.AccessoryFolder)) return;
        var files = Directory.GetFiles(Plugin.AccessoryFolder, "*.glb").Where(f => !f.EndsWith(".tmp.glb", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var f in files)
        {
            var info = new FileInfo(f);
            if (!items.TryGetValue(f, out var it)) items[f] = it = new Item { Glb = f, Name = Path.GetFileNameWithoutExtension(f) };
            if (it.Loading != null || (it.Stamp == info.LastWriteTimeUtc && it.Size == info.Length)) continue;
            it.Stamp = info.LastWriteTimeUtc;
            it.Size = info.Length;
            var path = f;
            it.Loading = Task.Run(() => (Load(path), Anim.Read(path)));
        }
        foreach (var gone in items.Keys.Where(k => !files.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList())
        {
            Unmake(items[gone]);
            items.Remove(gone);
            Panel.Refresh();
        }
    }

    /// On a background thread: the .glb, and its textures decoded (each image once, even when parts share it).
    [HideFromIl2Cpp]
    internal static List<Part> Load(string path)
    {
        var parts = Glb.Read(path);
        var done = new Dictionary<int, (byte[] px, int w, int h, string err)>();
        foreach (var p in parts)
        {
            if (p.Image == null) continue;
            if (!done.TryGetValue(p.ImageIndex, out var d))
            {
                try { d.px = Picture.Decode(p.Image, out d.w, out d.h); }
                catch (Exception e) { d.err = e.Message; }
                done[p.ImageIndex] = d;
            }
            p.Pixels = d.px; p.TexW = d.w; p.TexH = d.h; p.ImageError = d.err;
            p.Image = null; // the decoded pixels are all that's needed now
        }
        return parts;
    }

    [HideFromIl2Cpp]
    void Finish()
    {
        foreach (var it in items.Values)
        {
            if (it.Loading == null || !it.Loading.IsCompleted) continue;
            var task = it.Loading;
            it.Loading = null;
            if (task.IsFaulted)
            {
                it.Error = task.Exception?.GetBaseException().Message ?? "unknown";
                Plugin.Logger.LogWarning($"couldn't read {it.Name}: {it.Error}");
                Ui.Toast($"Couldn't read {it.Name}: {it.Error}");
                continue;
            }
            it.Error = null;
            if (visual != null) Wear.FindGameMaterial(visual);
            it.Parts = task.Result.parts;
            it.Anim = task.Result.anim;
            Make(it);
            LastEvent = $"{it.Name} updated at {DateTime.Now:HH:mm:ss}";
            int textured = it.Parts.Count(x => x.Pixels != null);
            foreach (var bad in it.Parts.Where(x => x.ImageError != null)) Plugin.Logger.LogWarning($"{it.Name}: texture of '{bad.Name}' skipped: {bad.ImageError}");
            Plugin.Logger.LogInfo($"wearing {it.Name}: {it.Parts.Count} part(s) ({textured} textured), {it.Tris:N0} triangles, look '{Wear.LookName}'");
            Ui.Toast($"{it.Name} updated ({it.Tris:N0} triangles)");
            Panel.Refresh();
        }
    }

    [HideFromIl2Cpp]
    void Make(Item it)
    {
        Unmake(it);
        var pieces = it.Anim != null ? new List<(Part part, GameObject go)>() : null;
        it.Go = Wear.Build(it.Name, it.Parts, out it.Tris, pieces);
        if (visual != null) Wear.Attach(it.Go, visual);
        else UnityEngine.Object.DontDestroyOnLoad(it.Go);
        if (pieces != null)
        {
            it.Player = new AnimPlayer(it.Anim, it.Go, pieces);
            if (it.Player.Empty) it.Player = null;
        }
        it.Go.SetActive(Visible(it));
    }

    [HideFromIl2Cpp]
    static void Unmake(Item it)
    {
        it.Player?.Destroy();
        it.Player = null;
        Wear.Destroy(it.Go);
        it.Go = null;
    }

    /// Moves the animated accessories and keeps their videos going (only the ones being worn and shown).
    [HideFromIl2Cpp]
    void Animate()
    {
        foreach (var it in items.Values)
            if (it.Player != null && it.Go != null && it.Go.activeInHierarchy)
            {
                try { it.Player.Tick(); }
                catch (Exception e) { Plugin.Logger.LogWarning($"animation of {it.Name} stopped: {e.Message}"); it.Player.Destroy(); it.Player = null; }
            }
    }

    bool Visible(Item it) => shown && IsWorn(it.Name);

    [HideFromIl2Cpp]
    void Apply()
    {
        foreach (var it in items.Values)
            if (it.Go != null && it.Go.activeSelf != Visible(it)) it.Go.SetActive(Visible(it));
    }

    /// Keeps the accessories on the local Flippy (new one after a respawn or level change), hidden while the phone is
    /// destroyed or the accessories are off.
    [HideFromIl2Cpp]
    void Follow()
    {
        var p = EHS.PlayerRef.LocalPlayer;
        if (p == null)
        {
            if (visual != null)
            {
                visual = null;
                player = IntPtr.Zero;
                foreach (var it in items.Values)
                    if (it.Go != null) { it.Go.transform.SetParent(null, false); UnityEngine.Object.DontDestroyOnLoad(it.Go); it.Go.SetActive(false); }
            }
            return;
        }
        if (p.Pointer != player || visual == null)
        {
            player = p.Pointer;
            var vh = p.playerVisualsHandler;
            visual = vh != null && vh.playerVisualT != null ? vh.playerVisualT : p.transform;
            Wear.FindGameMaterial(visual);
            foreach (var it in items.Values)
            {
                if (it.Go != null) Wear.Attach(it.Go, visual);
                else if (it.Parts != null) Make(it); // destroyed with the old Flippy: build it again
            }
        }
        var phone = p.destroyedPhoneEffectHandler;
        bool want = Plugin.Enabled.Value && !(phone != null && phone.IsDestroyed());
        if (want != shown) { shown = want; Apply(); }
    }
}
