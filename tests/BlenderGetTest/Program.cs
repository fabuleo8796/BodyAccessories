using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using BodyAccessories;

// Offline check of the Blender downloader: download, checksum, unzip, run "blender --version", then Remove().
Console.WriteLine("installed before: " + BlenderGet.Installed);
var sw = Stopwatch.StartNew();
BlenderGet.Start();
string last = "";
while (BlenderGet.Busy) { if (BlenderGet.Status != last && sw.ElapsedMilliseconds % 1 == 0) { last = BlenderGet.Status; } Thread.Sleep(2000); Console.WriteLine($"  {BlenderGet.Progress:P0} {BlenderGet.Status}"); }
Console.WriteLine($"done in {sw.Elapsed.TotalSeconds:0}s: {BlenderGet.Status}; installed {BlenderGet.Installed}; exe {BlenderGet.Exe}");
bool ok = BlenderGet.Installed;
if (ok)
{
    var p = Process.Start(new ProcessStartInfo(BlenderGet.Exe, "--version") { RedirectStandardOutput = true, UseShellExecute = false });
    var outp = p.StandardOutput.ReadToEnd(); p.WaitForExit();
    Console.WriteLine("blender says: " + outp.Split('\n')[0].Trim());
    ok &= outp.Contains("Blender 5.2.3");
}
BlenderGet.Remove();
Console.WriteLine("after Remove: installed " + BlenderGet.Installed + ", folder exists " + Directory.Exists(BlenderGet.Folder) + " (" + BlenderGet.Status + ")");
ok &= !Directory.Exists(BlenderGet.Folder);
Console.WriteLine(ok ? "ALL OK" : "FAILED");
return ok ? 0 : 1;
