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

        // OPERATOR CHEAT LAYER (docs/CHEATS_DESIGN.md). Off unless enabled here or with
        // --btbridge-allow-cheats; read once at startup.
        public bool AllowCheats;
        public int CheatArmMinutes = 15;
        public int CheatArmMaxOps;
        public string CheatArmHotkey = "Ctrl+Shift+F9";
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
                Cheats.CheatConfig.Init(Settings.AllowCheats, Settings.CheatArmMinutes, Settings.CheatArmMaxOps, Settings.CheatArmHotkey);
                Cheats.CheatService.Init(modDir);
                HarmonyInstance.Create("hexylab.btbridge").PatchAll(Assembly.GetExecutingAssembly());
                Ui.ChatOverlay.Create(modDir);
                if (Cheats.CheatConfig.Capability)
                {
                    Cheats.CheatOverlay.Create();
                }
                server = new BridgeServer(Settings.Port, Routes.Build(Cheats.CheatConfig.Capability));
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
            settings.AllowCheats = obj.Value<bool?>("AllowCheats") ?? false;
            settings.CheatArmMinutes = obj.Value<int?>("CheatArmMinutes") ?? settings.CheatArmMinutes;
            settings.CheatArmMaxOps = obj.Value<int?>("CheatArmMaxOps") ?? settings.CheatArmMaxOps;
            settings.CheatArmHotkey = obj.Value<string>("CheatArmHotkey") ?? settings.CheatArmHotkey;
            return settings;
        }
    }
}
