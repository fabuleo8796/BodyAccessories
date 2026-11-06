using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace BodyAccessories;

/// For players without a usable Blender: downloads Blender's official portable build (a zip, no installer, no admin
/// rights) from Blender's own download mirrors, checks it against the SHA-256 Blender publishes for that file (pinned
/// here: a download that doesn't match is thrown away), and unzips it to %LOCALAPPDATA%\FIHBlender. A short folder on
/// purpose: Blender's glTF exporter has files deep enough to pass Windows' 260-character path limit under longer
/// folders. Everything runs on a background thread; the panel shows the progress. Remove() deletes it again.
internal static class BlenderGet
{
    public const string Version = "5.2.3";
    const string Zip = "blender-5.2.3-windows-x64.zip";
    const string Sha256 = "723f08a4401502561eb763cdeacf8745b31e5f3f154d287d3830d97cf75dc2fb"; // blender-5.2.3.sha256
    public const long Size = 405_839_452;
    // Blender's official mirrors first: download.blender.org itself answers apps with 403 (a bot check)
    static readonly string[] Mirrors =
    {
        "https://mirrors.ocf.berkeley.edu/blender/release/Blender5.2/",
        "https://ftp.halifax.rwth-aachen.de/blender/release/Blender5.2/",
        "https://mirror.clarkson.edu/blender/release/Blender5.2/",
        "https://download.blender.org/release/Blender5.2/",
    };

    /// FIHBLENDER_DIR overrides the folder (only for the offline test).
    public static string Folder => Environment.GetEnvironmentVariable("FIHBLENDER_DIR") is { Length: > 0 } dir ? dir
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FIHBlender");
    public static string Exe => Path.Combine(Folder, "blender-5.2.3-windows-x64", "blender.exe");
    public static bool Installed => File.Exists(Exe);

    public static bool Busy { get; private set; }
    public static string Status { get; private set; } = "";
    public static float Progress { get; private set; }
    static CancellationTokenSource cancel;

    public static void Start()
    {
        if (Busy || Installed) return;
        Busy = true;
        Progress = 0f;
        Status = "Starting the download…";
        cancel = new CancellationTokenSource();
        var token = cancel.Token;
        Task.Run(async () =>
        {
            try { await Download(token); }
            catch (OperationCanceledException) { Status = "Download cancelled"; }
            catch (Exception e)
            {
                Status = "Download failed: " + e.Message;
                Plugin.Logger.LogWarning("Blender download: " + e);
            }
            finally { Busy = false; }
        });
    }

    public static void Cancel() => cancel?.Cancel();

    static async Task Download(CancellationToken token)
    {
        Directory.CreateDirectory(Folder);
        var part = Path.Combine(Folder, Zip + ".part");
        Exception last = null;
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("BodyAccessories/" + Plugin.Version + " (Flipping is Hard mod)");
        foreach (var mirror in Mirrors)
        {
            try
            {
                Status = "Downloading Blender " + Version + "…";
                using var resp = await http.GetAsync(mirror + Zip, HttpCompletionOption.ResponseHeadersRead, token);
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? Size;
                if (resp.Content.Headers.ContentType?.MediaType?.Contains("html") == true) throw new Exception("the mirror sent a web page, not the file");
                using var sha = SHA256.Create();
                using (var src = await resp.Content.ReadAsStreamAsync(token))
                using (var dst = File.Create(part))
                {
                    var buf = new byte[1 << 20];
                    long got = 0;
                    int n;
                    while ((n = await src.ReadAsync(buf, 0, buf.Length, token)) > 0)
                    {
                        sha.TransformBlock(buf, 0, n, null, 0);
                        await dst.WriteAsync(buf, 0, n, token);
                        got += n;
                        Progress = Math.Min(0.95f, got / (float)total * 0.95f);
                        Status = $"Downloading Blender {Version}… {got / 1048576} of {total / 1048576} MB";
                    }
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                var hash = Convert.ToHexString(sha.Hash).ToLowerInvariant();
                if (hash != Sha256) throw new Exception("the file didn't match Blender's published checksum, so it was thrown away");
                Unpack(part);
                Plugin.Logger.LogInfo($"Blender {Version} downloaded from {mirror} and unpacked to {Path.GetDirectoryName(Exe)} (checksum OK)");
                return;
            }
            catch (UnpackException) { throw; } // the download was good: don't fetch it again from another mirror
            catch (OperationCanceledException) { TryDelete(part); throw; }
            catch (Exception e)
            {
                last = e;
                Plugin.Logger.LogWarning($"Blender download from {mirror} failed: {e.Message}");
                TryDelete(part);
            }
        }
        throw last ?? new Exception("no mirror worked");
    }

    sealed class UnpackException : Exception
    {
        public UnpackException(string m, Exception inner) : base(m, inner) { }
    }

    /// Unzips straight into place (the zip holds one folder, blender-5.2.3-windows-x64): no moving afterwards, which
    /// antivirus scanners of fresh files can block. A half-unpacked folder from an earlier try is cleared first.
    static void Unpack(string zip)
    {
        Status = "Unpacking Blender…";
        try
        {
            var final = Path.GetDirectoryName(Exe);
            for (int attempt = 0; Directory.Exists(final); attempt++)
            {
                try { Directory.Delete(final, true); }
                catch when (attempt < 5) { Thread.Sleep(1500); }
            }
            ZipFile.ExtractToDirectory(zip, Folder);
            File.Delete(zip);
            if (!File.Exists(Exe)) throw new Exception("blender.exe isn't in the zip");
            Progress = 1f;
            Status = $"Blender {Version} is ready";
        }
        catch (Exception e)
        {
            throw new UnpackException("couldn't unpack Blender: " + e.Message, e);
        }
    }

    /// Deletes the downloaded Blender (only ours, in %LOCALAPPDATA%\FIHBlender; never one the player installed).
    public static void Remove()
    {
        if (Busy) return;
        try
        {
            if (Directory.Exists(Folder)) Directory.Delete(Folder, true);
            Status = "The downloaded Blender was removed";
        }
        catch (Exception e)
        {
            Status = "Couldn't remove it (is Blender still open?): " + e.Message;
        }
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* left for the next try */ }
    }
}
