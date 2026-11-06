namespace BodyAccessories;
// Stands in for the plugin's logger in the offline test.
internal static class Plugin
{
    public const string Version = "test";
    public static readonly Log Logger = new();
    public sealed class Log
    {
        public void LogInfo(object m) => System.Console.WriteLine("[info] " + m);
        public void LogWarning(object m) => System.Console.WriteLine("[warn] " + m);
    }
}
