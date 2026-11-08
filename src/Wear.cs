using System;
using System.Collections.Generic;
using System.Text;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace BodyAccessories;

/// Turns parsed parts into meshes and materials, and hangs them on Flippy's visuals root, so they move and tumble
/// with him exactly (his body space: +Z up the phone, +Y out of the screen, 1 unit = 1 game unit).
internal static class Wear
{
    static readonly string[] LookNames = { "game shading", "Unity lit", "flat colour" };
    static Material gameTemplate, outlineTemplate;
    static string gameReport;
    static Texture2D flatNormal, msao;
    static readonly Dictionary<IntPtr, List<Object>> owned = new();
    static List<Object> building;

    internal static string LookName => LookNames[Math.Clamp(Plugin.Look.Value, 0, 2)];

    /// Builds one accessory's object (not attached yet). tris = its triangle count.
    internal static GameObject Build(string name, List<Part> parts, out int tris) => Build(name, parts, out tris, null);

    /// pieces (if given) gets each part with its objects (the part and its outline), for animation and video.
    internal static GameObject Build(string name, List<Part> parts, out int tris, List<(Part part, GameObject go)> pieces)
    {
        tris = 0;
        var go = new GameObject("Accessory " + name);
        building = new List<Object>();
        owned[go.Pointer] = building;
        foreach (var p in parts)
        {
            if (p.VertexCount == 0 || p.Triangles.Length == 0) continue;
            var child = new GameObject(p.Name);
            pieces?.Add((p, child));
            child.transform.SetParent(go.transform, false);
            var mesh = new Mesh { name = "BodyAccessories " + p.Name };
            building.Add(mesh);
            if (p.VertexCount > 65000) mesh.indexFormat = IndexFormat.UInt32;
            var v = new Il2CppStructArray<Vector3>(p.VertexCount);
            for (int i = 0; i < p.VertexCount; i++) v[i] = new Vector3 { x = p.Positions[i * 3], y = p.Positions[i * 3 + 1], z = p.Positions[i * 3 + 2] };
            mesh.vertices = v;
            if (p.Uvs != null)
            {
                var uv = new Il2CppStructArray<Vector2>(p.VertexCount);
                for (int i = 0; i < p.VertexCount; i++) uv[i] = new Vector2 { x = p.Uvs[i * 2], y = p.Uvs[i * 2 + 1] };
                mesh.uv = uv;
            }
            var t = new Il2CppStructArray<int>(p.Triangles.Length);
            for (int i = 0; i < p.Triangles.Length; i++) t[i] = p.Triangles[i];
            mesh.triangles = t;
            if (p.Normals != null)
            {
                var n = new Il2CppStructArray<Vector3>(p.VertexCount);
                for (int i = 0; i < p.VertexCount; i++) n[i] = new Vector3 { x = p.Normals[i * 3], y = p.Normals[i * 3 + 1], z = p.Normals[i * 3 + 2] };
                mesh.normals = n;
            }
            else mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            child.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = child.AddComponent<MeshRenderer>();
            mr.sharedMaterial = Material(p);
            mr.shadowCastingMode = ShadowCastingMode.On;
            mr.receiveShadows = true;
            if (Plugin.Outline.Value && outlineTemplate != null)
            {
                // the game's toon outline shader on a copy of the same mesh, like Flippy's own outline parts
                var o = new GameObject(p.Name + " outline");
                pieces?.Add((p, o));
                o.transform.SetParent(go.transform, false);
                o.AddComponent<MeshFilter>().sharedMesh = mesh;
                var or = o.AddComponent<MeshRenderer>();
                var om = new Material(outlineTemplate) { name = "BodyAccessories outline", hideFlags = HideFlags.HideAndDontSave };
                building.Add(om);
                or.sharedMaterial = om;
                or.shadowCastingMode = ShadowCastingMode.Off;
            }
            tris += p.Triangles.Length / 3;
        }
        return go;
    }

    /// Puts an accessory on Flippy (or moves it to the new Flippy after a respawn).
    internal static void Attach(GameObject accessory, Transform visual)
    {
        accessory.transform.SetParent(visual, false);
        accessory.transform.localPosition = Vector3.zero;
        accessory.transform.localRotation = Quaternion.identity;
        accessory.transform.localScale = Vector3.one;
        SetLayer(accessory.transform, visual.gameObject.layer);
    }

    static void SetLayer(Transform t, int layer)
    {
        t.gameObject.layer = layer;
        for (int i = 0; i < t.childCount; i++) SetLayer(t.GetChild(i), layer);
    }

    internal static void Destroy(GameObject accessory)
    {
        if (accessory == null) return;
        if (owned.TryGetValue(accessory.Pointer, out var list))
        {
            foreach (var o in list) if (o != null) Object.Destroy(o);
            owned.Remove(accessory.Pointer);
        }
        Object.Destroy(accessory);
    }

    // ---------------------------------------------------------------- materials

    /// Looks for the material Flippy's own body uses (the game's player shader), to copy it for accessories, and logs
    /// every shader under him once (prototype recon).
    internal static void FindGameMaterial(Transform visual)
    {
        if (gameTemplate != null) return;
        outlineTemplate = null;
        var seen = new HashSet<string>();
        var r = new StringBuilder();
        Material best = null;
        foreach (var rend in visual.GetComponentsInChildren<Renderer>(true))
        {
            var mats = rend.sharedMaterials;
            if (mats == null) continue;
            foreach (var m in mats)
            {
                if (m == null || m.shader == null) continue;
                var sn = m.shader.name;
                if (seen.Add(sn)) r.Append($"\n  '{sn}' on {rend.gameObject.name} ({rend.GetIl2CppType().Name})");
                // Flippy's body shader exactly (not the screen, outline or sticker ones), preferably from his Cover
                if (sn == "Elegant Horse Studios/SG_Player_MSAO" && (best == null || rend.gameObject.name.StartsWith("Cover"))) best = m;
                if (outlineTemplate == null && sn == "Elegant Horse Studios/SG_Outline") outlineTemplate = m;
            }
        }
        gameTemplate = best;
        gameReport = r.ToString();
        Plugin.Logger.LogInfo($"shaders on Flippy:{gameReport}\n  using for accessories: {(best != null ? $"'{best.shader.name}' from '{best.name}'" : "none found (falls back to Unity lit)")}");
        if (best != null)
        {
            var names = best.GetTexturePropertyNames();
            var list = new StringBuilder();
            foreach (var n in names) list.Append(n).Append(' ');
            Plugin.Logger.LogInfo($"  its textures: {list}");
        }
    }

    static Material Material(Part p)
    {
        int look = Math.Clamp(Plugin.Look.Value, 0, 2);
        var color = Srgb(p.Color, out float alpha);
        // a picture (with the base colour multiplied in, as glTF does), or a plain colour
        bool cut = false;
        var tex = p.Pixels != null ? Picture(p, color, alpha, out cut) : Solid(color, alpha);
        if (p.Pixels != null) { color = new Color32 { r = 255, g = 255, b = 255, a = 255 }; alpha = 1f; }
        Material m = null;
        if (look == 0 && gameTemplate != null)
        {
            m = new Material(gameTemplate) { name = "BodyAccessories game " + p.Name };
            flatNormal ??= SolidRaw(new Color32 { r = 128, g = 128, b = 255, a = 128 }, true);
            msao ??= SolidRaw(new Color32 { r = 0, g = 90, b = 255, a = 255 }, true);
            foreach (var n in new[] { "_MainTex", "_BaseTexture", "_BaseMap" }) Tex(m, n, tex);
            foreach (var n in new[] { "_NormalTex", "_NormalTexture", "_BumpMap" }) Tex(m, n, flatNormal);
            foreach (var n in new[] { "_MSAOTex", "_MSAOTexture" }) Tex(m, n, msao);
            var emit = Srgb(new[] { p.Emissive[0], p.Emissive[1], p.Emissive[2], 1f }, out _);
            var etex = Solid(emit, 1f);
            foreach (var n in new[] { "_EmissiveTex", "_EmissiveTexture", "_EmissionMap" }) Tex(m, n, etex);
            if (m.HasProperty("_Color")) m.SetColor("_Color", Color.white);
        }
        else
        {
            var shader = look == 2 ? Shader.Find("Universal Render Pipeline/Unlit") : Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Universal Render Pipeline/Simple Lit");
            shader ??= Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Sprites/Default");
            m = new Material(shader) { name = $"BodyAccessories {LookName} {p.Name}" };
            var c = new Color { r = color.r / 255f, g = color.g / 255f, b = color.b / 255f, a = alpha };
            if (p.Pixels != null) { m.mainTexture = tex; if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", tex); }
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            if (m.HasProperty("_Color")) m.SetColor("_Color", c);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.3f);
            if (p.Emissive[0] + p.Emissive[1] + p.Emissive[2] > 0.01f && m.HasProperty("_EmissionColor"))
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", new Color { r = p.Emissive[0], g = p.Emissive[1], b = p.Emissive[2], a = 1f });
            }
        }
        if (cut) Cutout(m, p.AlphaMode == "MASK" ? p.AlphaCutoff : 0.5f);
        if (p.DoubleSided && m.HasProperty("_Cull")) m.SetFloat("_Cull", 0f); // seen from both sides, like in Blender (a picture on a plane)
        m.hideFlags = HideFlags.HideAndDontSave;
        building?.Add(m);
        return m;
    }

    /// See-through pictures (a PNG with no background): the see-through parts are cut out, the way the game's own capes
    /// do it on the same player shader (_ALPHATEST_ON, _AlphaClip, _Cutoff, the alpha-test queue). URP's Lit and Unlit
    /// use the same names.
    static void Cutout(Material m, float cutoff)
    {
        m.EnableKeyword("_ALPHATEST_ON");
        if (m.HasProperty("_AlphaClip")) m.SetFloat("_AlphaClip", 1f);
        if (m.HasProperty("_Cutoff")) m.SetFloat("_Cutoff", cutoff);
        if (m.HasProperty("_AlphaToMask")) m.SetFloat("_AlphaToMask", 1f);
        m.renderQueue = 2450;
    }

    /// Puts a texture (a video's RenderTexture) on a material as its colour, whichever shader it uses.
    internal static void UseTexture(Material m, Texture t)
    {
        foreach (var n in new[] { "_MainTex", "_BaseTexture", "_BaseMap" }) Tex(m, n, t);
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", Color.white);
    }

    static void Tex(Material m, string prop, Texture t)
    {
        if (m.HasProperty(prop)) m.SetTexture(prop, t);
    }

    /// glTF colours are linear; textures and SetColor take sRGB.
    static Color32 Srgb(float[] c, out float alpha)
    {
        alpha = c.Length > 3 ? c[3] : 1f;
        byte S(float v)
        {
            v = Math.Clamp(v, 0f, 1f);
            v = v <= 0.0031308f ? v * 12.92f : 1.055f * MathF.Pow(v, 1f / 2.4f) - 0.055f;
            return (byte)(v * 255f + 0.5f);
        }
        return new Color32 { r = S(c[0]), g = S(c[1]), b = S(c[2]), a = 255 };
    }

    /// The part's decoded picture as a texture with mipmaps, tinted by the material's base colour. cut: it has see-through
    /// parts (some pixels under half opacity).
    static Texture2D Picture(Part p, Color32 tint, float alpha, out bool cut)
    {
        cut = false;
        int n = p.TexW * p.TexH;
        var px = new Color32[n];
        var src = p.Pixels;
        bool plain = tint.r == 255 && tint.g == 255 && tint.b == 255 && alpha >= 0.999f;
        for (int i = 0, k = 0; i < n; i++, k += 4)
            px[i] = plain
                ? new Color32 { r = src[k], g = src[k + 1], b = src[k + 2], a = src[k + 3] }
                : new Color32 { r = (byte)(src[k] * tint.r / 255), g = (byte)(src[k + 1] * tint.g / 255), b = (byte)(src[k + 2] * tint.b / 255), a = (byte)(src[k + 3] * alpha) };
        for (int i = 0; i < n && !cut; i++) cut = px[i].a < 128;
        var t = new Texture2D(p.TexW, p.TexH, TextureFormat.RGBA32, true) { name = "BodyAccessories " + p.Name, wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = 4 };
        t.SetPixels32(px);
        t.Apply(true, true);
        t.hideFlags = HideFlags.HideAndDontSave;
        building?.Add(t);
        return t;
    }

    static Texture2D Solid(Color32 c, float alpha)
    {
        c.a = (byte)(Math.Clamp(alpha, 0f, 1f) * 255f);
        var t = SolidRaw(c, false);
        building?.Add(t);
        return t;
    }

    static Texture2D SolidRaw(Color32 c, bool linear)
    {
        var t = new Texture2D(4, 4, TextureFormat.RGBA32, false, linear) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Point };
        var px = new Color32[16];
        for (int i = 0; i < 16; i++) px[i] = c;
        t.SetPixels32(px);
        t.Apply(false, false);
        return t;
    }
}
