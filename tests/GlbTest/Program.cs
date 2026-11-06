using System;
using System.Linq;
using BodyAccessories;

// Offline check of the .glb reader: reads a file and prints each part's size and bounds in Flippy's body space.
// dotnet GlbTest.dll <file.glb> [expect z centre]
var parts = Glb.Read(args[0]);
bool ok = parts.Count > 0;
foreach (var p in parts)
{
    float[] min = { float.MaxValue, float.MaxValue, float.MaxValue }, max = { float.MinValue, float.MinValue, float.MinValue };
    for (int v = 0; v < p.VertexCount; v++)
        for (int a = 0; a < 3; a++) { min[a] = Math.Min(min[a], p.Positions[v * 3 + a]); max[a] = Math.Max(max[a], p.Positions[v * 3 + a]); }
    Console.WriteLine($"{p.Name}: {p.VertexCount} verts, {p.Triangles.Length / 3} tris, normals {p.Normals != null}, uvs {p.Uvs != null}, colour [{string.Join(", ", p.Color.Select(c => c.ToString("0.00")))}], emissive [{string.Join(", ", p.Emissive.Select(c => c.ToString("0.00")))}]");
    Console.WriteLine($"  x {min[0]:0.000}..{max[0]:0.000}  y {min[1]:0.000}..{max[1]:0.000}  z {min[2]:0.000}..{max[2]:0.000}");
    if (args.Length > 1)
    {
        float zc = (min[2] + max[2]) / 2, want = float.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
        bool good = Math.Abs(zc - want) < 0.08f && Math.Abs((min[0] + max[0]) / 2) < 0.02f;
        Console.WriteLine($"  centre z {zc:0.000} (want {want}): {(good ? "OK" : "WRONG")}");
        ok &= good;
    }
    // winding: in Unity (clockwise front faces) the face normal from the triangle should agree with the vertex normals
    if (p.Normals != null)
    {
        int agree = 0, n = p.Triangles.Length / 3;
        for (int t = 0; t < n; t++)
        {
            int a = p.Triangles[t * 3], b = p.Triangles[t * 3 + 1], c = p.Triangles[t * 3 + 2];
            var A = new System.Numerics.Vector3(p.Positions[a * 3], p.Positions[a * 3 + 1], p.Positions[a * 3 + 2]);
            var B = new System.Numerics.Vector3(p.Positions[b * 3], p.Positions[b * 3 + 1], p.Positions[b * 3 + 2]);
            var C = new System.Numerics.Vector3(p.Positions[c * 3], p.Positions[c * 3 + 1], p.Positions[c * 3 + 2]);
            var face = System.Numerics.Vector3.Cross(B - A, C - A); // Unity: clockwise seen from the front = this points away from the viewer... checked below
            var nrm = new System.Numerics.Vector3(p.Normals[a * 3], p.Normals[a * 3 + 1], p.Normals[a * 3 + 2]);
            // in a left-handed system, a clockwise (front) triangle's Cross(B-A, C-A) points toward the viewer = along the normal
            if (System.Numerics.Vector3.Dot(face, nrm) > 0) agree++;
        }
        bool wind = agree > n * 0.95;
        Console.WriteLine($"  winding: {agree}/{n} triangles face along their normals: {(wind ? "OK" : "WRONG")}");
        ok &= wind;
    }
}
foreach (var p in parts)
    if (p.Image != null)
    {
        var px = Picture.Decode(p.Image, out int w, out int h);
        bool good = px.Length == w * h * 4 && w > 0;
        Console.WriteLine($"  texture of {p.Name}: {p.Image.Length} bytes -> {w}x{h}, first pixel rgba({px[0]},{px[1]},{px[2]},{px[3]}): {(good ? "OK" : "WRONG")}");
        ok &= good;
    }
Console.WriteLine(ok ? "ALL OK" : "FAILED");
return ok ? 0 : 1;
