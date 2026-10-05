using System;
using System.Reflection;
using BTBridge.Bridge;
using Harmony;
using Newtonsoft.Json.Linq;

namespace BTBridge
{
    public class Settings
    {
        public int Port = 8787;
        public int MainThreadTimeoutMs = 5000;
    }

    public static class Main
    {
        public const string Version = "0.1.0";

        public static Settings Settings { get; private set; } = new Settings();
        public static string ModDir { get; private set; }

        private static BridgeServer server;

        // Invoked by both the HBS ModLoader and ModTek: they match parameters by name.
        public static void Init(string modDir, string settingsJson)
        {
            ModDir = modDir;
            Log.Init(modDir);
            try
            {
                Settings = LoadSettings(settingsJson);
                HarmonyInstance.Create("hexylab.btbridge").PatchAll(Assembly.GetExecutingAssembly());
                server = new BridgeServer(Settings.Port, Routes.Build());
                server.Start();
                Log.Info($"BTBridge {Version} initialized; listening on 127.0.0.1:{Settings.Port}");
            }
            catch (Exception e)
            {
                Log.Error("Init failed", e);
            }
        }

        private static Settings LoadSettings(string json)
        {
            var settings = new Settings();
            if (string.IsNullOrEmpty(json))
            {
                return settings;
            }
            var obj = JObject.Parse(json);
            settings.Port = obj.Value<int?>("Port") ?? settings.Port;
            settings.MainThreadTimeoutMs = obj.Value<int?>("MainThreadTimeoutMs") ?? settings.MainThreadTimeoutMs;
            return settings;
        }
    }
}
