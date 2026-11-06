using System;
using System.IO;
using System.Runtime.InteropServices;

namespace BodyAccessories;

/// Decodes an accessory's texture (the PNG or JPEG Blender packs into the .glb) with Windows' own picture library,
/// GDI+, through its flat C API, the way Cape Editor loads cape pictures. It runs on the background thread that reads
/// the .glb, so the game only uploads finished pixels. (Unity's ImageConversion.LoadImage is stripped in this build:
/// kit trap 1.)
internal static unsafe class Picture
{
    const string Dll = "gdiplus.dll";
    const int Argb32 = 0x0026200A; // PixelFormat32bppARGB: bytes are B, G, R, A
    const int LockRead = 1;
    public const int MaxSide = 4096;
    static readonly object gate = new();
    static IntPtr token;

    [StructLayout(LayoutKind.Sequential)]
    struct StartupInput { public uint Version; public IntPtr DebugCallback; public int SuppressBackgroundThread, SuppressExternalCodecs; }

    [StructLayout(LayoutKind.Sequential)]
    struct LockRect { public int X, Y, Width, Height; }

    [StructLayout(LayoutKind.Sequential)]
    struct BitmapData { public uint Width, Height; public int Stride, PixelFormat; public IntPtr Scan0, Reserved; }

    [DllImport(Dll)] static extern int GdiplusStartup(out IntPtr token, ref StartupInput input, IntPtr output);
    [DllImport(Dll, CharSet = CharSet.Unicode)] static extern int GdipCreateBitmapFromFile(string file, out IntPtr bitmap);
    [DllImport(Dll)] static extern int GdipGetImageWidth(IntPtr image, out uint width);
    [DllImport(Dll)] static extern int GdipGetImageHeight(IntPtr image, out uint height);
    [DllImport(Dll)] static extern int GdipDisposeImage(IntPtr image);
    [DllImport(Dll)] static extern int GdipBitmapLockBits(IntPtr bitmap, ref LockRect rect, int flags, int format, ref BitmapData data);
    [DllImport(Dll)] static extern int GdipBitmapUnlockBits(IntPtr bitmap, ref BitmapData data);

    /// The picture's pixels as R, G, B, A bytes, bottom row first (the order Unity's textures use).
    public static byte[] Decode(byte[] file, out int width, out int height)
    {
        lock (gate)
        {
            if (token == IntPtr.Zero)
            {
                var input = new StartupInput { Version = 1 };
                Check(GdiplusStartup(out token, ref input, IntPtr.Zero), "GdiplusStartup");
            }
            // GDI+ reads from a file; a temp copy is the simplest way in
            var temp = Path.Combine(Path.GetTempPath(), $"BodyAccessories-{Guid.NewGuid():N}.img");
            File.WriteAllBytes(temp, file);
            IntPtr bmp = IntPtr.Zero;
            try
            {
                Check(GdipCreateBitmapFromFile(temp, out bmp), "reading the picture");
                GdipGetImageWidth(bmp, out uint w);
                GdipGetImageHeight(bmp, out uint h);
                width = (int)w;
                height = (int)h;
                if (width <= 0 || height <= 0 || width > MaxSide || height > MaxSide)
                    throw new InvalidDataException($"the texture is {width}x{height}; the most is {MaxSide}x{MaxSide}");
                var rect = new LockRect { Width = width, Height = height };
                var data = new BitmapData();
                Check(GdipBitmapLockBits(bmp, ref rect, LockRead, Argb32, ref data), "LockBits");
                var rgba = new byte[width * height * 4];
                try
                {
                    for (int y = 0; y < height; y++)
                    {
                        byte* src = (byte*)data.Scan0 + (long)y * data.Stride;
                        int dst = (height - 1 - y) * width * 4; // GDI+ is top row first, Unity bottom row first
                        for (int x = 0; x < width; x++, src += 4, dst += 4)
                        {
                            rgba[dst] = src[2]; rgba[dst + 1] = src[1]; rgba[dst + 2] = src[0]; rgba[dst + 3] = src[3];
                        }
                    }
                }
                finally
                {
                    GdipBitmapUnlockBits(bmp, ref data);
                }
                return rgba;
            }
            finally
            {
                if (bmp != IntPtr.Zero) GdipDisposeImage(bmp);
                try { File.Delete(temp); } catch { /* the OS cleans temp */ }
            }
        }
    }

    static void Check(int status, string what)
    {
        if (status != 0) throw new InvalidOperationException($"{what} failed (GDI+ error {status})");
    }
}
