using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace BodyAccessories;

/// One drawable piece of an accessory: already in Flippy's body space (Unity axes), ready for a Mesh.
internal sealed class Part
{
    public string Name = "";
    public float[] Positions = Array.Empty<float>(); // x, y, z per vertex
    public float[] Normals;                           // or null: the mod recalculates them
    public float[] Uvs;                               // u, v per vertex (Unity's v, bottom up), or null
    public int[] Triangles = Array.Empty<int>();      // clockwise for Unity
    public float[] Color = { 1, 1, 1, 1 };            // the material's base colour (linear)
    public float[] Emissive = { 0, 0, 0 };
    public byte[] Image;                              // the base colour texture's PNG/JPG bytes, if any
    public int ImageIndex = -1;                       // which glTF image (parts can share one)
    public byte[] Pixels;                             // Image decoded: RGBA, bottom row first
    public int TexW, TexH;
    public string ImageError;
    public int VertexCount => Positions.Length / 3;
}

/// Reads a binary glTF (.glb) as Blender's exporter writes it: every mesh primitive of the default scene, with its
/// node transforms applied, its base colour, and its texture's bytes. Pure managed code (no Unity), so it can run on a
/// background thread and in an offline test.
/// Axes: glTF is +Y up, right-handed. Blender's exporter turns Blender (x, y, z) into glTF (x, z, -y). The template
/// puts Flippy's up along Blender +Z and his screen toward Blender -Y, and Flippy's body space in the game has +Z up
/// the phone and +Y out of the screen. So game = (gx, gz, gy): a mirror, so every triangle's winding flips too.
internal static class Glb
{
    public const int MaxTriangles = 300_000;

    public static List<Part> Read(string path) => Read(File.ReadAllBytes(path));

    public static List<Part> Read(byte[] data)
    {
        if (data.Length < 20 || BitConverter.ToUInt32(data, 0) != 0x46546C67) throw new InvalidDataException("not a .glb file");
        int pos = 12;
        string json = null;
        byte[] bin = Array.Empty<byte>();
        while (pos + 8 <= data.Length)
        {
            int len = BitConverter.ToInt32(data, pos);
            uint type = BitConverter.ToUInt32(data, pos + 4);
            if (type == 0x4E4F534A) json = Encoding.UTF8.GetString(data, pos + 8, len);
            else if (type == 0x004E4942) { bin = new byte[len]; Buffer.BlockCopy(data, pos + 8, bin, 0, len); }
            pos += 8 + len;
        }
        if (json == null) throw new InvalidDataException("no JSON chunk");
        using var doc = JsonDocument.Parse(json);
        var g = new Gltf(doc.RootElement, bin);
        var parts = new List<Part>();
        var root = doc.RootElement;
        int sceneIndex = root.TryGetProperty("scene", out var sc) ? sc.GetInt32() : 0;
        if (root.TryGetProperty("scenes", out var scenes) && scenes.GetArrayLength() > sceneIndex && scenes[sceneIndex].TryGetProperty("nodes", out var top))
            foreach (var n in top.EnumerateArray()) g.Node(n.GetInt32(), Matrix4x4.Identity, parts);
        else if (root.TryGetProperty("nodes", out var all))
            for (int i = 0; i < all.GetArrayLength(); i++) g.Node(i, Matrix4x4.Identity, parts);
        int tris = 0;
        foreach (var p in parts) tris += p.Triangles.Length / 3;
        if (tris > MaxTriangles) throw new InvalidDataException($"{tris:N0} triangles is too many (the limit is {MaxTriangles:N0})");
        return parts;
    }

    sealed class Gltf
    {
        readonly JsonElement root;
        readonly byte[] bin;

        public Gltf(JsonElement root, byte[] bin) { this.root = root; this.bin = bin; }

        public void Node(int index, Matrix4x4 parent, List<Part> parts)
        {
            var node = root.GetProperty("nodes")[index];
            var world = Local(node) * parent; // System.Numerics uses row vectors: child first, then parent
            if (node.TryGetProperty("mesh", out var m)) Mesh(m.GetInt32(), world, node.TryGetProperty("name", out var nm) ? nm.GetString() : $"node {index}", parts);
            if (node.TryGetProperty("children", out var kids))
                foreach (var k in kids.EnumerateArray()) Node(k.GetInt32(), world, parts);
        }

        static Matrix4x4 Local(JsonElement node)
        {
            if (node.TryGetProperty("matrix", out var mt))
            {
                var a = new float[16];
                int i = 0;
                foreach (var v in mt.EnumerateArray()) a[i++] = v.GetSingle();
                // glTF stores column-major for column vectors; read straight in, that's the row-vector matrix
                return new Matrix4x4(a[0], a[1], a[2], a[3], a[4], a[5], a[6], a[7], a[8], a[9], a[10], a[11], a[12], a[13], a[14], a[15]);
            }
            var t = Vec(node, "translation", 3, 0);
            var r = Vec(node, "rotation", 4, 0, 1);
            var s = Vec(node, "scale", 3, 1);
            return Matrix4x4.CreateScale(s[0], s[1], s[2]) * Matrix4x4.CreateFromQuaternion(new Quaternion(r[0], r[1], r[2], r[3])) * Matrix4x4.CreateTranslation(t[0], t[1], t[2]);
        }

        static float[] Vec(JsonElement e, string key, int n, float fill, float last = float.NaN)
        {
            var v = new float[n];
            for (int i = 0; i < n; i++) v[i] = fill;
            if (!float.IsNaN(last)) v[n - 1] = last;
            if (e.TryGetProperty(key, out var a))
            {
                int i = 0;
                foreach (var x in a.EnumerateArray()) if (i < n) v[i++] = x.GetSingle();
            }
            return v;
        }

        void Mesh(int index, Matrix4x4 world, string name, List<Part> parts)
        {
            var mesh = root.GetProperty("meshes")[index];
            Matrix4x4.Invert(world, out var inv);
            var normalM = Matrix4x4.Transpose(inv);
            bool mirrored = world.GetDeterminant() < 0;
            int pi = 0;
            foreach (var prim in mesh.GetProperty("primitives").EnumerateArray())
            {
                if (prim.TryGetProperty("mode", out var mode) && mode.GetInt32() != 4) continue; // triangles only
                var attrs = prim.GetProperty("attributes");
                if (!attrs.TryGetProperty("POSITION", out var posA)) continue;
                var p = new Part { Name = mesh.GetProperty("primitives").GetArrayLength() > 1 ? $"{name} {++pi}" : name };
                var raw = Floats(posA.GetInt32(), out int count, out int comps);
                p.Positions = new float[count * 3];
                for (int v = 0; v < count; v++)
                {
                    var w = Vector3.Transform(new Vector3(raw[v * comps], raw[v * comps + 1], raw[v * comps + 2]), world);
                    p.Positions[v * 3] = w.X; p.Positions[v * 3 + 1] = w.Z; p.Positions[v * 3 + 2] = w.Y; // glTF -> Flippy's body space
                }
                if (attrs.TryGetProperty("NORMAL", out var nA))
                {
                    var n = Floats(nA.GetInt32(), out _, out int nc);
                    p.Normals = new float[count * 3];
                    for (int v = 0; v < count; v++)
                    {
                        var w = Vector3.Normalize(Vector3.TransformNormal(new Vector3(n[v * nc], n[v * nc + 1], n[v * nc + 2]), normalM));
                        p.Normals[v * 3] = w.X; p.Normals[v * 3 + 1] = w.Z; p.Normals[v * 3 + 2] = w.Y;
                    }
                }
                if (attrs.TryGetProperty("TEXCOORD_0", out var uvA))
                {
                    var uv = Floats(uvA.GetInt32(), out _, out int uc);
                    p.Uvs = new float[count * 2];
                    for (int v = 0; v < count; v++) { p.Uvs[v * 2] = uv[v * uc]; p.Uvs[v * 2 + 1] = 1f - uv[v * uc + 1]; }
                }
                int[] idx;
                if (prim.TryGetProperty("indices", out var iA)) idx = Ints(iA.GetInt32());
                else { idx = new int[count]; for (int i = 0; i < count; i++) idx[i] = i; }
                // the axis swap mirrors; a mirrored node mirrors again
                bool flip = !mirrored;
                p.Triangles = new int[idx.Length - idx.Length % 3];
                for (int i = 0; i + 2 < idx.Length; i += 3)
                {
                    p.Triangles[i] = idx[i];
                    p.Triangles[i + 1] = flip ? idx[i + 2] : idx[i + 1];
                    p.Triangles[i + 2] = flip ? idx[i + 1] : idx[i + 2];
                }
                if (prim.TryGetProperty("material", out var mat)) Material(mat.GetInt32(), p);
                parts.Add(p);
            }
        }

        void Material(int index, Part p)
        {
            if (!root.TryGetProperty("materials", out var mats) || index >= mats.GetArrayLength()) return;
            var m = mats[index];
            if (m.TryGetProperty("emissiveFactor", out _)) p.Emissive = Vec(m, "emissiveFactor", 3, 0);
            if (m.TryGetProperty("extensions", out var ext) && ext.TryGetProperty("KHR_materials_emissive_strength", out var es) && es.TryGetProperty("emissiveStrength", out var str))
                for (int i = 0; i < 3; i++) p.Emissive[i] *= str.GetSingle();
            if (!m.TryGetProperty("pbrMetallicRoughness", out var pbr)) return;
            if (pbr.TryGetProperty("baseColorFactor", out _)) p.Color = Vec(pbr, "baseColorFactor", 4, 1);
            if (pbr.TryGetProperty("baseColorTexture", out var bt) && root.TryGetProperty("textures", out var texs))
            {
                var tex = texs[bt.GetProperty("index").GetInt32()];
                if (tex.TryGetProperty("source", out var src) && root.TryGetProperty("images", out var imgs))
                {
                    var img = imgs[src.GetInt32()];
                    if (img.TryGetProperty("bufferView", out var bv)) { p.Image = View(bv.GetInt32(), out _).ToArray(); p.ImageIndex = src.GetInt32(); }
                }
            }
        }

        ReadOnlySpan<byte> View(int index, out int stride)
        {
            var bv = root.GetProperty("bufferViews")[index];
            int off = bv.TryGetProperty("byteOffset", out var o) ? o.GetInt32() : 0;
            int len = bv.GetProperty("byteLength").GetInt32();
            stride = bv.TryGetProperty("byteStride", out var st) ? st.GetInt32() : 0;
            return new ReadOnlySpan<byte>(bin, off, len);
        }

        static int Comps(string type) => type switch { "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, "MAT4" => 16, _ => 1 };

        float[] Floats(int accessor, out int count, out int comps)
        {
            var a = root.GetProperty("accessors")[accessor];
            count = a.GetProperty("count").GetInt32();
            comps = Comps(a.GetProperty("type").GetString());
            int ct = a.GetProperty("componentType").GetInt32();
            bool norm = a.TryGetProperty("normalized", out var nz) && nz.GetBoolean();
            int size = ct switch { 5126 => 4, 5125 => 4, 5123 => 2, 5122 => 2, _ => 1 };
            var view = View(a.GetProperty("bufferView").GetInt32(), out int stride);
            int off = a.TryGetProperty("byteOffset", out var o) ? o.GetInt32() : 0;
            if (stride == 0) stride = size * comps;
            var outv = new float[count * comps];
            for (int i = 0; i < count; i++)
                for (int c = 0; c < comps; c++)
                {
                    int b = off + i * stride + c * size;
                    outv[i * comps + c] = ct switch
                    {
                        5126 => BitConverter.ToSingle(view.Slice(b, 4)),
                        5123 => norm ? BitConverter.ToUInt16(view.Slice(b, 2)) / 65535f : BitConverter.ToUInt16(view.Slice(b, 2)),
                        5121 => norm ? view[b] / 255f : view[b],
                        5122 => norm ? Math.Max(-1f, BitConverter.ToInt16(view.Slice(b, 2)) / 32767f) : BitConverter.ToInt16(view.Slice(b, 2)),
                        _ => 0f,
                    };
                }
            return outv;
        }

        int[] Ints(int accessor)
        {
            var a = root.GetProperty("accessors")[accessor];
            int count = a.GetProperty("count").GetInt32();
            int ct = a.GetProperty("componentType").GetInt32();
            var view = View(a.GetProperty("bufferView").GetInt32(), out _);
            int off = a.TryGetProperty("byteOffset", out var o) ? o.GetInt32() : 0;
            var outv = new int[count];
            for (int i = 0; i < count; i++)
                outv[i] = ct switch
                {
                    5125 => (int)BitConverter.ToUInt32(view.Slice(off + i * 4, 4)),
                    5123 => BitConverter.ToUInt16(view.Slice(off + i * 2, 2)),
                    _ => view[off + i],
                };
            return outv;
        }
    }
}
