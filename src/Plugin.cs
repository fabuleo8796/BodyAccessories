using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using UnityEngine.InputSystem;
using Object = UnityEngine.Object;

namespace BodyAccessories;

/// Body Accessories for Flipping is Hard: model anything around Flippy in Blender and wear it in game.
/// X opens the panel: your accessories (wear each or not, Edit opens one in Blender, New starts one), the look, and the
/// Flippy model. Blender opens on a template with the real plain Flippy, copied from your own game. Its "Export to
/// FIH" button (or just saving) writes a .glb into BepInEx\config\BodyAccessories\accessories, and the game puts it
/// on Flippy within a second. Shift+X: accessories on/off.
/// Animations and video textures loop like Blender's timeline. With FIH Sync installed, other modded players see them.
[BepInPlugin(Guid, "Body Accessories", Version)]
[BepInDependency("com.fabuleo8796.fihsync", BepInDependency.DependencyFlags.SoftDependency)]
public class Plugin : BasePlugin
{
    public const string Guid = "com.fabuleo8796.bodyaccessories", Version = "0.2.4";
    internal static ManualLogSource Logger;
    internal static string Folder;
    internal static ConfigEntry<Key> OpenKey;
    internal static ConfigEntry<bool> Enabled, Outline;
    internal static ConfigEntry<string> BlenderPath, AccessoryName, Hidden;
    internal static ConfigEntry<int> Look;

    public override void Load()
    {
        Logger = Log;
        Folder = Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
        if (string.IsNullOrEmpty(Folder)) Folder = Path.Combine(Paths.PluginPath, "BodyAccessories");
        OpenKey = Config.Bind("Keys", "Panel", Key.X, "Opens the Body Accessories panel. Shift + this key: accessories on/off.");
        Hidden = Config.Bind("Accessories", "Hidden", "", "Accessories you turned off in the panel (names, separated by |).");
        Enabled = Config.Bind("Accessories", "On", true, "Wear the accessories.");
        AccessoryName = Config.Bind("Accessories", "Name", "my_accessory", "The accessory X opens in Blender (its .blend and .glb are named after it).");
        Outline = Config.Bind("Accessories", "ToonOutline", false, "Draw the game's toon outline around accessories (test).");
        Look = Config.Bind("Accessories", "Look", 0, "0 = the game's own player shading, 1 = Unity lit, 2 = flat colour.");
        BlenderPath = Config.Bind("Blender", "Path", "", "blender.exe. Empty = find it (Program Files\\Blender Foundation, Steam).");

        ClassInjector.RegisterTypeInIl2Cpp<WearDriver>();
        var host = new GameObject("BodyAccessories");
        Object.DontDestroyOnLoad(host);
        host.hideFlags = HideFlags.HideAndDontSave;
        host.AddComponent<WearDriver>();
        Log.LogInfo($"Body Accessories loaded: {OpenKey.Value} opens Blender, accessories load from {AccessoryFolder}");
    }

    internal static string DataFolder => Path.Combine(Paths.ConfigPath, "BodyAccessories");
    internal static string ConfigFolder => DataFolder;
    internal static string AccessoryFolder => Path.Combine(DataFolder, "accessories");
}
