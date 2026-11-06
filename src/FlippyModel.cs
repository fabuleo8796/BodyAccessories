using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace BodyAccessories;

/// Copies the real plain Flippy (his Cover and his screen, no stickers or accessories) out of the running game into
/// BepInEx\config\BodyAccessories\flippy\flippy.obj (+ .mtl and textures), which the Blender template imports as its
/// reference. It's made on each player's own PC from their own game, so the mod never ships the game's files.
/// The game's meshes aren't CPU-readable, so the geometry and textures are read back from the GPU
/// (Mesh.GetVertexBuffer / GetIndexBuffer, Graphics.Blit + AsyncGPUReadback, all a few frames). Each step is logged
/// into flippy\report.txt, so a failure says where.
internal static class FlippyModel
{
    internal static string Folder => Path.Combine(Plugin.DataFolder, "flippy");
    internal static string ObjPath => Path.Combine(Folder, "flippy.obj");
    internal static bool Busy => jobs != null;
    internal static string Status = "not made yet";
    static List<Job> jobs;
    static StringBuilder report;
    static Action<bool> done;
    static float started;

    sealed class Job
    {
        public string Name = "", Group = "";
        public Mesh Mesh;
        public Material Mat;
        public Matrix4x4 ToBody;
        public GraphicsBuffer VB, IB;
        public AsyncGPUReadbackRequest VReq, IReq, TReq;
        public bool HasV, HasI, HasT, Failed;
        public RenderTexture Rt;
        public int Stride, Pos = -1, Nrm = -1, Uv = -1;
        public VertexAttributeFormat PosF, NrmF, UvF;
        public int Count;
        public byte[] VData, IData, TData;
        public int TexW, TexH;
        public string TexName;
        public Color Tint = Color.white;
    }

    /// Starts copying Flippy (under his visuals root). done(true) once flippy.obj is written.
    internal static void Start(Transform visual, Action<bool> onDone)
    {
        if (Busy) return;
        done = onDone;
        report = new StringBuilder($"Body Accessories: Flippy model copy, {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");
        jobs = new List<Job>();
        started = Time.unscaledTime;
        var toBody = visual.worldToLocalMatrix;
        foreach (var r in visual.GetComponentsInChildren<Renderer>(true))
        {
            string path = PathOf(r.transform, visual);
            string group = path.Contains("/Cover/") ? "cover" : path.Contains("/Face/") ? "screen" : null;
            string name = r.gameObject.name;
            // the full-detail parts only: LOD0, or a part with no LOD levels at all
            if (group == null || name.Contains("Outline") || (name.Contains("_LOD") && !name.EndsWith("_LOD0"))) continue;
            Mesh mesh = null;
            var mf = r.GetComponent<MeshFilter>();
            if (mf != null) mesh = mf.sharedMesh;
            var smr = r.TryCast<SkinnedMeshRenderer>();
            if (mesh == null && smr != null) mesh = smr.sharedMesh;
            if (mesh == null) { report.AppendLine($"{path}: no mesh"); continue; }
            var job = new Job { Name = name, Group = group, Mesh = mesh, Mat = r.sharedMaterial, ToBody = toBody * r.transform.localToWorldMatrix };
            report.AppendLine($"{path}: mesh '{mesh.name}', {mesh.vertexCount} vertices, readable {mesh.isReadable}, {mesh.subMeshCount} submesh(es), index {mesh.indexFormat}, renderer {r.GetIl2CppType().Name}, shader '{(r.sharedMaterial != null ? r.sharedMaterial.shader.name : "-")}'");
            try { Begin(job); }
            catch (Exception e) { job.Failed = true; report.AppendLine($"  couldn't start: {e.Message}"); }
            jobs.Add(job);
        }
        if (jobs.Count == 0) Finish(false, "found no Cover or screen under Flippy");
    }

    static string PathOf(Transform t, Transform root)
    {
        var parts = new List<string>();
        for (var x = t; x != null && x.Pointer != root.Pointer; x = x.parent) parts.Insert(0, x.name);
        return "/" + string.Join("/", parts);
    }

    static void Begin(Job j)
    {
        var m = j.Mesh;
        j.Count = m.vertexCount;
        int stream = -1;
        foreach (var a in m.GetVertexAttributes())
        {
            int off = m.GetVertexAttributeOffset(a.attribute);
            if (a.attribute == VertexAttribute.Position) { j.Pos = off; j.PosF = a.format; stream = a.stream; }
            else if (a.attribute == VertexAttribute.Normal && j.Nrm < 0 && a.stream == Math.Max(0, stream)) { j.Nrm = off; j.NrmF = a.format; }
            else if (a.attribute == VertexAttribute.TexCoord0 && a.stream == Math.Max(0, stream)) { j.Uv = off; j.UvF = a.format; }
            report.AppendLine($"  attribute {a.attribute} {a.format}x{a.dimension} stream {a.stream} offset {off}");
        }
        if (stream < 0) throw new Exception("no positions");
        j.Stride = m.GetVertexBufferStride(stream);
        // Read only: never change the mesh's buffer targets. On the game's non-readable meshes that re-creates the GPU
        // buffers without data (v0.0.2 turned Flippy black that way, until the game was restarted).
        j.VB = m.GetVertexBuffer(stream);
        j.IB = m.GetIndexBuffer();
        report.AppendLine($"  vertex buffer {(j.VB != null ? $"{j.VB.count} x {j.VB.stride} B" : "none")}, index buffer {(j.IB != null ? $"{j.IB.count} x {j.IB.stride} B" : "none")}");
        if (j.VB == null || j.IB == null) throw new Exception("the GPU buffers aren't available");
        j.VReq = AsyncGPUReadback.Request(j.VB);
        j.IReq = AsyncGPUReadback.Request(j.IB);

        // the base colour texture, through a blit (the game's textures aren't readable either)
        j.HasT = true;
        if (j.Mat == null) return;
        Texture tex = null;
        foreach (var p in new[] { "_MainTex", "_BaseTexture", "_BaseMap", "_FaceTexture" })
            if (j.Mat.HasProperty(p) && (tex = j.Mat.GetTexture(p)) != null) { j.TexName = p; break; }
        if (j.Mat.HasProperty("_BaseColor")) j.Tint = j.Mat.GetColor("_BaseColor");
        else if (j.Mat.HasProperty("_Color")) j.Tint = j.Mat.GetColor("_Color");
        if (tex == null) { report.AppendLine("  no colour texture"); return; }
        j.TexW = Math.Min(1024, tex.width);
        j.TexH = Math.Min(1024, tex.height);
        j.Rt = RenderTexture.GetTemporary(j.TexW, j.TexH, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        Graphics.Blit(tex, j.Rt);
        j.TReq = AsyncGPUReadback.Request(j.Rt, 0, TextureFormat.RGBA32);
        j.HasT = false;
        report.AppendLine($"  texture {j.TexName} '{tex.name}' {tex.width}x{tex.height} -> {j.TexW}x{j.TexH}");
    }

    /// Every frame while busy.
    internal static void Tick()
    {
        if (jobs == null) return;
        bool waiting = false;
        foreach (var j in jobs)
        {
            if (j.Failed) continue;
            try
            {
                if (!j.HasV && Ready(j.VReq, "vertices", j)) { j.VData = Bytes(j.VReq); j.HasV = true; }
                if (!j.HasI && Ready(j.IReq, "indices", j)) { j.IData = Bytes(j.IReq); j.HasI = true; }
                if (!j.HasT && Ready(j.TReq, "texture", j)) { j.TData = Bytes(j.TReq); j.HasT = true; }
                if (j.Failed) continue;
                if (!j.HasV || !j.HasI || !j.HasT) waiting = true;
            }
            catch (Exception e) { j.Failed = true; report.AppendLine($"{j.Name}: readback failed: {e.Message}"); }
        }
        if (waiting && Time.unscaledTime - started < 5f) return;
        try { Write(); }
        catch (Exception e) { Finish(false, "couldn't write the model: " + e.Message); }
    }

    static bool Ready(AsyncGPUReadbackRequest r, string what, Job j)
    {
        if (!r.done) return false;
        if (r.hasError) { j.Failed = true; report.AppendLine($"{j.Name}: {what} readback error"); return false; }
        return true;
    }

    static unsafe byte[] Bytes(AsyncGPUReadbackRequest r)
    {
        int size = r.layerDataSize;
        var ptr = r.GetDataRaw(0);
        var b = new byte[size];
        System.Runtime.InteropServices.Marshal.Copy(ptr, b, 0, size);
        return b;
    }

    // ---------------------------------------------------------------- OBJ + MTL + PNG

    static void Write()
    {
        Directory.CreateDirectory(Folder);
        var obj = new StringBuilder("# Plain Flippy, copied from your game by Body Accessories (Blender axes: +Z up the phone, -Y screen)\nmtllib flippy.mtl\n");
        var mtl = new StringBuilder();
        int baseV = 1, made = 0;
        var inv = CultureInfo.InvariantCulture;
        foreach (var j in jobs)
        {
            if (j.Failed || j.VData == null || j.IData == null) continue;
            string id = $"{j.Group}_{j.Name}".Replace(' ', '_');
            obj.AppendLine($"o GameFlippy_{id}");
            for (int v = 0; v < j.Count; v++)
            {
                var p = j.ToBody.MultiplyPoint3x4(Read3(j.VData, v * j.Stride + j.Pos, j.PosF));
                obj.Append("v ").Append(p.x.ToString("0.#####", inv)).Append(' ').Append((-p.y).ToString("0.#####", inv)).Append(' ').Append(p.z.ToString("0.#####", inv)).Append('\n');
            }
            bool uv = j.Uv >= 0, nrm = j.Nrm >= 0;
            if (uv)
                for (int v = 0; v < j.Count; v++)
                {
                    var t = Read2(j.VData, v * j.Stride + j.Uv, j.UvF);
                    obj.Append("vt ").Append(t.x.ToString("0.#####", inv)).Append(' ').Append(t.y.ToString("0.#####", inv)).Append('\n');
                }
            if (nrm)
                for (int v = 0; v < j.Count; v++)
                {
                    var n = j.ToBody.MultiplyVector(Read3(j.VData, v * j.Stride + j.Nrm, j.NrmF)).normalized;
                    obj.Append("vn ").Append(n.x.ToString("0.####", inv)).Append(' ').Append((-n.y).ToString("0.####", inv)).Append(' ').Append(n.z.ToString("0.####", inv)).Append('\n');
                }
            obj.AppendLine($"usemtl {id}");
            bool wide = j.IB.stride == 4;
            int n3 = j.IData.Length / (wide ? 4 : 2) / 3 * 3;
            for (int i = 0; i < n3; i += 3)
            {
                int a = Index(j.IData, i, wide), b = Index(j.IData, i + 1, wide), c = Index(j.IData, i + 2, wide);
                // Unity's clockwise -> Blender's counter-clockwise (the y flip mirrors, so swap two corners)
                obj.Append("f ").Append(Corner(a + baseV, uv, nrm)).Append(' ').Append(Corner(c + baseV, uv, nrm)).Append(' ').Append(Corner(b + baseV, uv, nrm)).Append('\n');
            }
            baseV += j.Count;
            var tint = j.Tint.linear;
            mtl.AppendLine($"newmtl {id}");
            mtl.AppendLine($"Kd {(j.TData != null ? 1f : tint.r).ToString("0.###", inv)} {(j.TData != null ? 1f : tint.g).ToString("0.###", inv)} {(j.TData != null ? 1f : tint.b).ToString("0.###", inv)}");
            if (j.TData != null)
            {
                var png = id + ".png";
                File.WriteAllBytes(Path.Combine(Folder, png), Png(j.TData, j.TexW, j.TexH));
                mtl.AppendLine($"map_Kd {png}");
            }
            mtl.AppendLine();
            made++;
            report.AppendLine($"{j.Name}: written ({j.Count} vertices, {n3 / 3} triangles{(j.TData != null ? ", textured" : "")})");
        }
        if (made == 0) { Finish(false, "no part could be read back"); return; }
        File.WriteAllText(Path.Combine(Folder, "flippy.mtl"), mtl.ToString());
        File.WriteAllText(ObjPath + ".tmp", obj.ToString());
        if (File.Exists(ObjPath)) File.Delete(ObjPath);
        File.Move(ObjPath + ".tmp", ObjPath);
        Finish(true, $"copied from your game ({made} parts)");
    }

    static void Finish(bool ok, string status)
    {
        Status = status;
        report?.AppendLine((ok ? "OK: " : "FAILED: ") + status);
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(Path.Combine(Folder, "report.txt"), report?.ToString() ?? status);
        }
        catch { /* only the report */ }
        Plugin.Logger.LogInfo("Flippy model: " + status + " (details in " + Path.Combine(Folder, "report.txt") + ")");
        if (jobs != null)
            foreach (var j in jobs)
            {
                try { j.VB?.Release(); j.IB?.Release(); } catch { /* best effort */ }
                if (j.Rt != null) RenderTexture.ReleaseTemporary(j.Rt);
            }
        jobs = null;
        var cb = done;
        done = null;
        cb?.Invoke(ok);
    }

    static string Corner(int i, bool uv, bool nrm) => uv && nrm ? $"{i}/{i}/{i}" : uv ? $"{i}/{i}" : nrm ? $"{i}//{i}" : $"{i}";

    static int Index(byte[] d, int i, bool wide) => wide ? (int)BitConverter.ToUInt32(d, i * 4) : BitConverter.ToUInt16(d, i * 2);

    static float Comp(byte[] d, int o, VertexAttributeFormat f, int k) => f switch
    {
        VertexAttributeFormat.Float32 => BitConverter.ToSingle(d, o + k * 4),
        VertexAttributeFormat.Float16 => (float)BitConverter.Int16BitsToHalf(BitConverter.ToInt16(d, o + k * 2)),
        VertexAttributeFormat.SNorm16 => Math.Max(-1f, BitConverter.ToInt16(d, o + k * 2) / 32767f),
        VertexAttributeFormat.UNorm16 => BitConverter.ToUInt16(d, o + k * 2) / 65535f,
        VertexAttributeFormat.SNorm8 => Math.Max(-1f, (sbyte)d[o + k] / 127f),
        VertexAttributeFormat.UNorm8 => d[o + k] / 255f,
        _ => 0f,
    };

    static Vector3 Read3(byte[] d, int o, VertexAttributeFormat f) => new() { x = Comp(d, o, f, 0), y = Comp(d, o, f, 1), z = Comp(d, o, f, 2) };
    static Vector2 Read2(byte[] d, int o, VertexAttributeFormat f) => new() { x = Comp(d, o, f, 0), y = Comp(d, o, f, 1) };

    /// RGBA rows from the GPU (bottom row first) to a PNG (top row first).
    static byte[] Png(byte[] rgba, int w, int h)
    {
        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, System.IO.Compression.CompressionLevel.Optimal, true))
            for (int y = h - 1; y >= 0; y--)
            {
                z.WriteByte(0);
                z.Write(rgba, y * w * 4, w * 4);
            }
        using var png = new MemoryStream();
        png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        Be(ihdr, 0, w); Be(ihdr, 4, h); ihdr[8] = 8; ihdr[9] = 6;
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", raw.ToArray());
        Chunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    static void Be(byte[] b, int o, int v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

    static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4]; Be(len, 0, data.Length);
        s.Write(len);
        var t = Encoding.ASCII.GetBytes(type);
        s.Write(t);
        s.Write(data);
        var crc = new byte[4]; Be(crc, 0, (int)Crc(t, data));
        s.Write(crc);
    }

    static uint[] crcTable;

    static uint Crc(byte[] a, byte[] b)
    {
        if (crcTable == null)
        {
            crcTable = new uint[256];
            for (uint n = 0; n < 256; n++) { uint c = n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; crcTable[n] = c; }
        }
        uint crc = 0xFFFFFFFF;
        foreach (var x in a) crc = crcTable[(crc ^ x) & 0xFF] ^ (crc >> 8);
        foreach (var x in b) crc = crcTable[(crc ^ x) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }
}
