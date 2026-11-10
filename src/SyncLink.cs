using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using UnityEngine;

namespace BodyAccessories;

/// Other players' accessories through FIH Sync (a separate, optional mod). What you wear goes out as one zip (each
/// worn .glb, its .anim.json, its videos, and its textures as raw pixels), and other modded players' zips come back,
/// get unpacked into FIH Sync's scratch folder for that look, and are built on their Flippy just like yours.
///
/// Safety, because those files come from strangers:
/// - Pictures are never decoded here: the sender's own game turns its textures into raw pixels (.tex), and the
///   receiver only copies pixels. The PNG/JPG inside another player's .glb is thrown away unread.
/// - Videos (the one thing Windows still decodes) only play when FIH Sync's "Videos from" allows that player.
/// - Only known file types are unpacked, inside the folder, with a size cap (no zip bombs, no ../ tricks), and the
///   glTF reader refuses files that lie about their sizes.
/// - Limits per player (accessories, triangles, texture size, animation length), and the frame time goes to FIH Sync's
///   lag shield.
/// Without FIH Sync installed this does nothing; the calls into it sit in NoInlining methods.
internal static class SyncLink
{
    const int MaxItems = 8, MaxTris = 150_000, MaxTex = 1024, MaxFrames = 2400, MaxTracks = 64, MaxVideos = 2, MaxEntries = 64;
    const long MaxUnpacked = 64L * 1024 * 1024;
    static readonly string[] Allowed = { ".glb", ".json", ".tex", ".mp4", ".m4v", ".mov", ".webm", ".avi", ".ogv" };
    static readonly string[] VideoTypes = { ".mp4", ".m4v", ".mov", ".webm", ".avi", ".ogv" };

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
    static readonly Stopwatch watch = new();
    static bool linked;

    internal static void Start()
    {
        try { Link(); linked = true; }
        catch (Exception) { Plugin.Logger.LogInfo("FIH Sync isn't installed: other players won't see your accessories"); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Link() => FIHSync.Share.Register("accessories", Version, Data, Received, Gone);

    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool VideosAllowed(int owner) => FIHSync.Share.VideosAllowed(owner);

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void ReportCost(int owner, double ms) => FIHSync.Share.ReportCost(owner, ms);

    static List<WearDriver.Item> Shared()
    {
        var d = WearDriver.Instance;
        if (d == null || !Plugin.Enabled.Value) return new List<WearDriver.Item>();
        return d.Items.Where(i => i.Parts != null && i.Error == null && WearDriver.IsWorn(i.Name) && File.Exists(i.Glb)).OrderBy(i => i.Name).Take(MaxItems).ToList();
    }

    // which accessories are worn, and when each was last exported (a new export from Blender changes its stamp)
    static string Version()
    {
        var items = Shared();
        if (items.Count == 0) return null;
        return string.Join("|", items.Select(i => $"{i.Name}:{i.Stamp.Ticks}:{i.Size}"));
    }

    // ---------------------------------------------------------------- sending (your own files)

    /// Videos shared in total, at most: FIH Sync players skip anything over 30 MB by default, and a look that big takes a
    /// while to arrive. A video that doesn't fit stays out: the others see that accessory without it.
    const long VideoBudget = 20L * 1024 * 1024;
    static readonly HashSet<string> warned = new();

    static HashSet<string> VideosToShare(List<WearDriver.Item> items)
    {
        var files = new List<string>();
        foreach (var it in items)
            if (it.Anim != null)
                foreach (var v in it.Anim.Videos)
                    if (File.Exists(v.File) && !files.Contains(v.File, StringComparer.OrdinalIgnoreCase)) files.Add(v.File);
        files.Sort(BySize); // the small ones first: as many videos as fit
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var f in files)
        {
            long size = new FileInfo(f).Length;
            if (total + size <= VideoBudget) { keep.Add(f); total += size; continue; }
            if (warned.Add(f + "|" + size))
            {
                Plugin.Logger.LogWarning($"video '{Path.GetFileName(f)}' ({size / 1048576.0:0.#} MB) is too big to share: other players see the accessory without it");
                Ui.Toast($"Your video is too big to share ({size / 1048576.0:0} MB). Other players see the accessory without it: keep videos under 20 MB (shorter or 720p).");
            }
        }
        return keep;
    }

    static int BySize(string a, string b) => new FileInfo(a).Length.CompareTo(new FileInfo(b).Length);

    static byte[] Data()
    {
        var items = Shared();
        if (items.Count == 0) return null;
        var videos = VideosToShare(items);
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var it in items)
            {
                var folder = Path.GetDirectoryName(it.Glb) ?? "";
                var files = new List<string> { it.Glb };
                var anim = Path.ChangeExtension(it.Glb, null) + ".anim.json";
                if (File.Exists(anim)) files.Add(anim);
                if (it.Anim != null) files.AddRange(it.Anim.Videos.Select(v => v.File).Where(videos.Contains));
                foreach (var f in files.Distinct(StringComparer.OrdinalIgnoreCase))
                    zip.CreateEntryFromFile(f, it.Name + "/" + Path.GetRelativePath(folder, f).Replace('\\', '/'), System.IO.Compression.CompressionLevel.Fastest);
                // the textures as raw pixels, already decoded by this game: the receiver never decodes a picture
                foreach (var p in it.Parts.Where(p => p.Pixels != null && p.ImageIndex >= 0).GroupBy(p => p.ImageIndex).Select(g => g.First()))
                {
                    var (px, w, h) = Shrink(p.Pixels, p.TexW, p.TexH, MaxTex);
                    var e = zip.CreateEntry($"{it.Name}/{it.Name}.{p.ImageIndex}.tex", System.IO.Compression.CompressionLevel.Fastest);
                    e.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero); // fixed: same pixels, same hash
                    using var s = e.Open();
                    using var bw = new BinaryWriter(s);
                    bw.Write(0x54484946); // "FIHT"
                    bw.Write(w);
                    bw.Write(h);
                    bw.Write(px);
                }
            }
        return ms.ToArray();
    }

    /// Halves a picture (2x2 average) until it fits the sharing limit.
    static (byte[] px, int w, int h) Shrink(byte[] px, int w, int h, int max)
    {
        while (w > max || h > max)
        {
            int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
            var o = new byte[nw * nh * 4];
            for (int y = 0; y < nh; y++)
                for (int x = 0; x < nw; x++)
                    for (int c = 0; c < 4; c++)
                    {
                        int x0 = Math.Min(w - 1, x * 2), x1 = Math.Min(w - 1, x * 2 + 1), y0 = Math.Min(h - 1, y * 2), y1 = Math.Min(h - 1, y * 2 + 1);
                        o[(y * nw + x) * 4 + c] = (byte)((px[(y0 * w + x0) * 4 + c] + px[(y0 * w + x1) * 4 + c] + px[(y1 * w + x0) * 4 + c] + px[(y1 * w + x1) * 4 + c] + 2) / 4);
                    }
            px = o; w = nw; h = nh;
        }
        return (px, w, h);
    }

    // ---------------------------------------------------------------- receiving (other players' files)

    static void Received(int ownerId, byte[] data, string folder)
    {
        Gone(ownerId);
        bool videos = false;
        try { videos = VideosAllowed(ownerId); } catch { /* no FIH Sync answer: no videos */ }
        var r = new Remote { Owner = ownerId };
        r.Loading = Task.Run(() => Unpack(data, folder, videos));
        remote[ownerId] = r;
    }

    /// On a background thread: unpacks the zip (once per look) and reads each .glb, with the limits above.
    static List<(string, List<Part>, Anim)> Unpack(byte[] data, string folder, bool videos)
    {
        var root = Path.GetFullPath(folder);
        if (!File.Exists(Path.Combine(root, ".done")))
        {
            Directory.CreateDirectory(root);
            using var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
            if (zip.Entries.Count > MaxEntries) throw new InvalidDataException("too many files");
            long total = 0;
            foreach (var e in zip.Entries)
            {
                if (e.FullName.EndsWith("/")) continue;
                var ext = Path.GetExtension(e.FullName).ToLowerInvariant();
                if (!Allowed.Contains(ext)) continue; // only the kinds of files an accessory has
                var path = Path.GetFullPath(Path.Combine(root, e.FullName));
                if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue; // no ../ tricks
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var input = e.Open();
                using var output = File.Create(path);
                var buffer = new byte[81920];
                int n;
                while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += n;
                    if (total > MaxUnpacked) throw new InvalidDataException("unpacks too big");
                    output.Write(buffer, 0, n);
                }
            }
            File.WriteAllText(Path.Combine(root, ".done"), "");
        }
        var list = new List<(string, List<Part>, Anim)>();
        int tris = 0;
        foreach (var glb in Directory.GetFiles(root, "*.glb", SearchOption.AllDirectories).Take(MaxItems))
        {
            try
            {
                var parts = Glb.Read(glb);
                int t = parts.Sum(p => p.Triangles.Length / 3);
                if (tris + t > MaxTris) { Plugin.Logger.LogWarning($"another player's {Path.GetFileName(glb)} skipped: over {MaxTris:N0} triangles"); continue; }
                tris += t;
                Pixels(glb, parts);
                list.Add((Path.GetFileNameWithoutExtension(glb), parts, SafeAnim(glb, root, videos)));
            }
            catch (Exception e) { Plugin.Logger.LogWarning($"another player's {Path.GetFileName(glb)}: {e.Message}"); }
        }
        return list;
    }

    /// Puts the sender's raw pixels on the parts. Their PNG/JPG is dropped without being opened.
    static void Pixels(string glb, List<Part> parts)
    {
        var stem = Path.ChangeExtension(glb, null);
        var cache = new Dictionary<int, (byte[] px, int w, int h)>();
        foreach (var p in parts)
        {
            p.Image = null;
            if (p.ImageIndex < 0) continue;
            if (!cache.TryGetValue(p.ImageIndex, out var t))
            {
                t = (null, 0, 0);
                var path = $"{stem}.{p.ImageIndex}.tex";
                if (File.Exists(path) && new FileInfo(path).Length <= 12 + MaxTex * MaxTex * 4)
                {
                    using var br = new BinaryReader(File.OpenRead(path));
                    int magic = br.ReadInt32(), w = br.ReadInt32(), h = br.ReadInt32();
                    if (magic == 0x54484946 && w > 0 && h > 0 && w <= MaxTex && h <= MaxTex)
                    {
                        var px = br.ReadBytes(w * h * 4);
                        if (px.Length == w * h * 4) t = (px, w, h);
                    }
                }
                cache[p.ImageIndex] = t;
            }
            p.Pixels = t.px; p.TexW = t.w; p.TexH = t.h;
        }
    }

    /// The animation, within limits, and only videos that are real files inside this look's folder (an .anim.json
    /// from someone else could point anywhere, even at a web address).
    static Anim SafeAnim(string glb, string root, bool videos)
    {
        Anim anim;
        try { anim = Anim.Read(glb); }
        catch (Exception e) { Plugin.Logger.LogWarning($"another player's animation skipped: {e.Message}"); return null; }
        if (anim == null) return null;
        if (anim.Frames > MaxFrames || anim.Tracks.Count > MaxTracks || anim.Fps <= 0 || anim.Fps > 240) { anim.Tracks.Clear(); anim.Videos.Clear(); return null; }
        anim.Videos.RemoveAll(v =>
        {
            if (!videos) return true;
            string full;
            try { full = Path.GetFullPath(v.File); } catch { return true; }
            return !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !VideoTypes.Contains(Path.GetExtension(full).ToLowerInvariant()) || !File.Exists(full);
        });
        if (anim.Videos.Count > MaxVideos) anim.Videos.RemoveRange(MaxVideos, anim.Videos.Count - MaxVideos);
        return anim.Tracks.Count > 0 || anim.Videos.Count > 0 ? anim : null;
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
            watch.Restart();
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
            try { ReportCost(r.Owner, watch.Elapsed.TotalMilliseconds); } catch { /* FIH Sync gone */ }
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
