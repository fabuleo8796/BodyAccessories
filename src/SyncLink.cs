using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using UnityEngine;

namespace BodyAccessories;

/// Other players' accessories through FIH Sync (a separate, optional mod). What you wear goes out as one zip (each
/// worn .glb, its .anim.json and its videos), and other modded players' zips come back, get unpacked into FIH Sync's
/// scratch folder for that look, and are built on their Flippy just like yours. Without FIH Sync installed this does
/// nothing; the calls into it sit in NoInlining methods, so a missing FIHSync.dll is one caught exception.
internal static class SyncLink
{
    sealed class Worn
    {
        public string Name = "";
        public List<Part> Parts;
        public Anim Anim;
        public GameObject Go;
        public AnimPlayer Player;
    }

    sealed class Remote
    {
        public int Owner;
        public Task<List<(string name, List<Part> parts, Anim anim)>> Loading;
        public readonly List<Worn> Items = new();
        public IntPtr Player;
        public Transform Visual;
    }

    static readonly Dictionary<int, Remote> remote = new();
    static bool linked;

    internal static void Start()
    {
        try { Link(); linked = true; }
        catch (Exception) { Plugin.Logger.LogInfo("FIH Sync isn't installed: other players won't see your accessories"); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Link() => FIHSync.Share.Register("accessories", Version, Data, Received, Gone);

    static List<WearDriver.Item> Shared()
    {
        var d = WearDriver.Instance;
        if (d == null || !Plugin.Enabled.Value) return new List<WearDriver.Item>();
        return d.Items.Where(i => i.Parts != null && i.Error == null && WearDriver.IsWorn(i.Name) && File.Exists(i.Glb)).OrderBy(i => i.Name).ToList();
    }

    // which accessories are worn, and when each was last exported (a new export from Blender changes its stamp)
    static string Version()
    {
        var items = Shared();
        if (items.Count == 0) return null;
        return string.Join("|", items.Select(i => $"{i.Name}:{i.Stamp.Ticks}:{i.Size}"));
    }

    static byte[] Data()
    {
        var items = Shared();
        if (items.Count == 0) return null;
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var it in items)
            {
                var folder = Path.GetDirectoryName(it.Glb) ?? "";
                var files = new List<string> { it.Glb };
                var anim = Path.ChangeExtension(it.Glb, null) + ".anim.json";
                if (File.Exists(anim)) files.Add(anim);
                if (it.Anim != null) files.AddRange(it.Anim.Videos.Select(v => v.File).Where(File.Exists));
                foreach (var f in files.Distinct(StringComparer.OrdinalIgnoreCase))
                    zip.CreateEntryFromFile(f, it.Name + "/" + Path.GetRelativePath(folder, f).Replace('\\', '/'), System.IO.Compression.CompressionLevel.Fastest);
            }
        return ms.ToArray();
    }

    static void Received(int ownerId, byte[] data, string folder)
    {
        Gone(ownerId);
        var r = new Remote { Owner = ownerId };
        r.Loading = Task.Run(() => Unpack(data, folder));
        remote[ownerId] = r;
    }

    /// On a background thread: unpacks the zip (once per look; only plain paths inside the folder) and reads each .glb.
    static List<(string, List<Part>, Anim)> Unpack(byte[] data, string folder)
    {
        var root = Path.GetFullPath(folder);
        if (!File.Exists(Path.Combine(root, ".done")))
        {
            Directory.CreateDirectory(root);
            using var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
            foreach (var e in zip.Entries)
            {
                if (e.FullName.EndsWith("/")) continue;
                var path = Path.GetFullPath(Path.Combine(root, e.FullName));
                if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue; // no ../ tricks
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                e.ExtractToFile(path, true);
            }
            File.WriteAllText(Path.Combine(root, ".done"), "");
        }
        var list = new List<(string, List<Part>, Anim)>();
        foreach (var glb in Directory.GetFiles(root, "*.glb", SearchOption.AllDirectories))
        {
            try { list.Add((Path.GetFileNameWithoutExtension(glb), WearDriver.Load(glb), Anim.Read(glb))); }
            catch (Exception e) { Plugin.Logger.LogWarning($"another player's {Path.GetFileName(glb)}: {e.Message}"); }
        }
        return list;
    }

    static void Gone(int ownerId)
    {
        if (!remote.TryGetValue(ownerId, out var r)) return;
        foreach (var w in r.Items) Unmake(w);
        remote.Remove(ownerId);
    }

    static void Unmake(Worn w)
    {
        w.Player?.Destroy();
        w.Player = null;
        Wear.Destroy(w.Go);
        w.Go = null;
    }

    /// Every frame: finishes loading, keeps each player's accessories on their current Flippy (rebuilt after a respawn
    /// or level change), hides them while that phone is destroyed or your accessories are off, and plays animations.
    internal static void Tick()
    {
        if (!linked || remote.Count == 0) return;
        foreach (var r in remote.Values)
        {
            if (r.Loading != null && r.Loading.IsCompleted)
            {
                var task = r.Loading;
                r.Loading = null;
                if (task.IsFaulted) Plugin.Logger.LogWarning($"player {r.Owner}'s accessories: {task.Exception?.GetBaseException().Message}");
                else
                {
                    foreach (var (name, parts, anim) in task.Result) r.Items.Add(new Worn { Name = name, Parts = parts, Anim = anim });
                    Plugin.Logger.LogInfo($"showing player {r.Owner}'s accessories: {string.Join(", ", r.Items.Select(w => w.Name))}");
                }
                r.Player = IntPtr.Zero; // attach below
            }
            Follow(r);
        }
    }

    static void Follow(Remote r)
    {
        EHS.PlayerRef p = null;
        try { if (!EHS.PlayerRef.TryGetConnectedPlayerByClientId(r.Owner, out p)) p = null; }
        catch { p = null; }
        if (p != null && EHS.PlayerRef.IsLocalPlayer(p)) p = null;
        if (p == null)
        {
            foreach (var w in r.Items) if (w.Go != null && w.Go.activeSelf) w.Go.SetActive(false);
            r.Visual = null;
            return;
        }
        if (p.Pointer != r.Player || r.Visual == null)
        {
            r.Player = p.Pointer;
            var vh = p.playerVisualsHandler;
            r.Visual = vh != null && vh.playerVisualT != null ? vh.playerVisualT : p.transform;
            foreach (var w in r.Items)
            {
                if (w.Go == null) Make(w);
                Wear.Attach(w.Go, r.Visual);
            }
        }
        var phone = p.destroyedPhoneEffectHandler;
        bool want = Plugin.Enabled.Value && !(phone != null && phone.IsDestroyed());
        foreach (var w in r.Items)
        {
            if (w.Go == null) continue;
            if (w.Go.activeSelf != want) w.Go.SetActive(want);
            if (want && w.Player != null)
            {
                try { w.Player.Tick(); }
                catch (Exception e) { Plugin.Logger.LogWarning($"animation of {w.Name} stopped: {e.Message}"); w.Player.Destroy(); w.Player = null; }
            }
        }
    }

    static void Make(Worn w)
    {
        Unmake(w);
        var pieces = w.Anim != null ? new List<(Part part, GameObject go)>() : null;
        w.Go = Wear.Build(w.Name, w.Parts, out _, pieces);
        if (pieces != null)
        {
            w.Player = new AnimPlayer(w.Anim, w.Go, pieces);
            if (w.Player.Empty) w.Player = null;
        }
    }
}
