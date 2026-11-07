using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using UnityEngine;
using UnityEngine.Video;
using NQ = System.Numerics.Quaternion;
using NV3 = System.Numerics.Vector3;
using Object = UnityEngine.Object;

namespace BodyAccessories;

/// An accessory's movement and videos, from the <name>.anim.json Blender writes next to the .glb (see
/// blender/fih_accessory.py): every frame of the scene's Start-End range, each moving object's transform relative to
/// the Start frame (the pose the .glb is in), already in the game's axes; and the video textures with where in the
/// video the range starts. It all loops on one clock, like Blender's playback with the timeline looping.
internal sealed class Anim
{
    public float Fps = 24f;
    public int Frames;
    public readonly Dictionary<string, Pose[]> Tracks = new();
    public readonly List<Video> Videos = new();

    public struct Pose { public NV3 Pos, Scale; public NQ Rot; }

    public sealed class Video { public string Material = "", File = ""; public double Start, Rate = 1; }

    public float Length => Frames / Math.Max(1f, Fps);

    /// Reads <glb minus .glb>.anim.json, or null when there's none (a still accessory). Runs on the loader's thread.
    public static Anim Read(string glbPath)
    {
        var path = Path.ChangeExtension(glbPath, null) + ".anim.json";
        if (!File.Exists(path)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var a = new Anim
        {
            Fps = root.TryGetProperty("fps", out var f) ? f.GetSingle() : 24f,
            Frames = root.TryGetProperty("frames", out var n) ? n.GetInt32() : 0,
        };
        if (root.TryGetProperty("tracks", out var tracks))
            foreach (var t in tracks.EnumerateObject())
            {
                var poses = new List<Pose>();
                foreach (var m in t.Value.EnumerateArray())
                {
                    var v = new float[12];
                    int i = 0;
                    foreach (var x in m.EnumerateArray()) if (i < 12) v[i++] = x.GetSingle();
                    // rows of a column-vector matrix [R|t] -> System.Numerics' row-vector matrix, then T * R * S
                    var mat = new System.Numerics.Matrix4x4(v[0], v[4], v[8], 0, v[1], v[5], v[9], 0, v[2], v[6], v[10], 0, v[3], v[7], v[11], 1);
                    if (!System.Numerics.Matrix4x4.Decompose(mat, out var sc, out var rot, out var pos)) { sc = NV3.One; rot = NQ.Identity; pos = new NV3(v[3], v[7], v[11]); }
                    poses.Add(new Pose { Pos = pos, Rot = rot, Scale = sc });
                }
                if (poses.Count > 0) a.Tracks[t.Name] = poses.ToArray();
            }
        if (root.TryGetProperty("videos", out var videos))
            foreach (var v in videos.EnumerateArray())
                a.Videos.Add(new Video
                {
                    Material = v.GetProperty("material").GetString() ?? "",
                    File = Path.Combine(Path.GetDirectoryName(glbPath) ?? "", v.GetProperty("file").GetString() ?? ""),
                    Start = v.TryGetProperty("start", out var s) ? s.GetDouble() : 0,
                    Rate = v.TryGetProperty("rate", out var r) ? r.GetDouble() : 1,
                });
        if (a.Frames <= 0) a.Frames = 1;
        return a.Tracks.Count > 0 || a.Videos.Count > 0 ? a : null;
    }
}

/// Plays one built accessory's Anim: moves its pieces each frame (smoothly between Blender's frames) and keeps each
/// video texture on the same loop. Videos go through Unity's VideoPlayer into a RenderTexture that replaces the
/// material's colour texture; the sound is off.
internal sealed class AnimPlayer
{
    readonly Anim anim;
    readonly List<(Transform t, Anim.Pose[] poses)> moving = new();
    readonly List<Clip> clips = new();
    readonly float started;

    sealed class Clip
    {
        public Anim.Video Info;
        public VideoPlayer Player;
        public RenderTexture Texture;
        public List<Material> Materials = new();
        public float NextSeek;
    }

    public AnimPlayer(Anim anim, GameObject root, List<(Part part, GameObject go)> pieces)
    {
        this.anim = anim;
        started = Time.time;
        foreach (var (part, go) in pieces)
            if (anim.Tracks.TryGetValue(part.Node, out var poses)) moving.Add((go.transform, poses));
        foreach (var v in anim.Videos)
        {
            var clip = new Clip { Info = v };
            foreach (var (part, go) in pieces)
                if (part.Material == v.Material && !go.name.EndsWith(" outline"))
                {
                    var mr = go.GetComponent<MeshRenderer>();
                    if (mr != null && mr.sharedMaterial != null) clip.Materials.Add(mr.sharedMaterial);
                }
            if (clip.Materials.Count == 0 || !File.Exists(v.File))
            {
                Plugin.Logger.LogWarning($"video '{Path.GetFileName(v.File)}': " + (File.Exists(v.File) ? $"no part uses material '{v.Material}'" : "file missing"));
                continue;
            }
            var host = new GameObject("Video " + v.Material);
            host.transform.SetParent(root.transform, false);
            var vp = host.AddComponent<VideoPlayer>();
            vp.playOnAwake = false;
            vp.source = VideoSource.Url;
            vp.url = v.File;
            vp.audioOutputMode = VideoAudioOutputMode.None;
            vp.isLooping = true;
            vp.skipOnDrop = true;
            vp.renderMode = VideoRenderMode.RenderTexture;
            vp.playbackSpeed = (float)Math.Clamp(v.Rate, 0.05, 10.0); // Blender plays one video frame per scene frame
            vp.Prepare();
            clip.Player = vp;
            clips.Add(clip);
        }
        Plugin.Logger.LogInfo($"animation: {moving.Count} moving piece(s), {clips.Count} video(s), {anim.Frames} frames at {anim.Fps:0.##} fps");
    }

    public bool Empty => moving.Count == 0 && clips.Count == 0;

    /// Every frame while the accessory is shown.
    public void Tick()
    {
        float t = (Time.time - started) % anim.Length;
        float f = t * anim.Fps;
        foreach (var (tr, poses) in moving)
        {
            if (tr == null) continue;
            int n = poses.Length;
            int i = Math.Min(n - 1, (int)f), j = (i + 1) % n;
            float k = Math.Clamp(f - i, 0f, 1f);
            var a = poses[i];
            var b = poses[j];
            var p = NV3.Lerp(a.Pos, b.Pos, k);
            var r = NQ.Slerp(a.Rot, b.Rot, k);
            var s = NV3.Lerp(a.Scale, b.Scale, k);
            tr.localPosition = new Vector3 { x = p.X, y = p.Y, z = p.Z };
            tr.localRotation = new Quaternion { x = r.X, y = r.Y, z = r.Z, w = r.W };
            tr.localScale = new Vector3 { x = s.X, y = s.Y, z = s.Z };
        }
        foreach (var c in clips) Video(c, t);
    }

    void Video(Clip c, float t)
    {
        var vp = c.Player;
        if (vp == null) return;
        if (c.Texture == null)
        {
            if (!vp.isPrepared) return;
            int w = (int)Math.Clamp(vp.width, 2u, 4096u), h = (int)Math.Clamp(vp.height, 2u, 4096u);
            c.Texture = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32) { name = "BodyAccessories video", hideFlags = HideFlags.HideAndDontSave };
            c.Texture.Create();
            vp.targetTexture = c.Texture;
            foreach (var m in c.Materials) Wear.UseTexture(m, c.Texture);
            vp.Play();
            Plugin.Logger.LogInfo($"video '{Path.GetFileName(c.Info.File)}' playing: {w}x{h}, {vp.frameCount} frames at {vp.frameRate:0.##} fps");
        }
        if (!vp.isPlaying) vp.Play(); // the accessory was hidden and shown again
        double length = vp.frameRate > 0 ? vp.frameCount / vp.frameRate : 0;
        if (length <= 0) return;
        // where the video should be: the loop's time, wrapped around the video's own length (it loops by itself too),
        // never on its very last frame (a seek there sticks)
        double frame = 1.0 / Math.Max(1f, vp.frameRate);
        double want = (c.Info.Start + t * c.Info.Rate) % length;
        if (want > length - frame * 2) want = 0;
        // the gap measured around the loop: 8.0 s and 0.1 s of an 8.1 s video are close, not 7.9 s apart
        double gap = (vp.time - want) % length;
        if (gap > length / 2) gap -= length;
        if (gap < -length / 2) gap += length;
        if (Math.Abs(gap) > 0.25 && Time.unscaledTime >= c.NextSeek)
        {
            vp.time = want;
            c.NextSeek = Time.unscaledTime + 0.5f; // let the seek land before judging it again
        }
    }

    public void Destroy()
    {
        foreach (var c in clips)
        {
            if (c.Player != null) c.Player.Stop();
            if (c.Texture != null) { c.Texture.Release(); Object.Destroy(c.Texture); }
        }
        clips.Clear();
        moving.Clear();
    }
}
