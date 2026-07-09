using System;
using System.IO;
using System.Text.Json;

namespace PalworldLauncher
{
    public class LauncherConfig
    {
        public string PalworldPath { get; set; } = "";
        public string ManifestUrl { get; set; } = "https://gist.githubusercontent.com/odd999/3f5f93c7f235c2ef8b92a7b733522cf5/raw/manifest.json";
        public string ServerIp { get; set; } = "127.0.0.1";
        public int ServerPort { get; set; } = 8211;
        public string ServerPassword { get; set; } = "";
        public bool AutoConnect { get; set; } = true;
    }

    public static class ConfigManager
    {
        private static readonly string ConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "launcher_config.json");
        public static LauncherConfig Current { get; private set; } = new LauncherConfig();

        public static void Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath);
                    var config = JsonSerializer.Deserialize<LauncherConfig>(json);
                    if (config != null)
                    {
                        Current = config;
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading config: {ex.Message}");
            }
            
            Current = new LauncherConfig();
        }

        public static void Save()
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(Current, options);
                File.WriteAllText(ConfigPath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error saving config: {ex.Message}");
            }
        }
    }
}
