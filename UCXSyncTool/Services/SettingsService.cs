using System;
using System.IO;
using System.Text.Json;
using UCXSyncTool.Models;

namespace UCXSyncTool.Services
{
    public static class SettingsService
    {
        private static string GetSettingsPath()
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UCXSyncTool");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "settings.json");
        }

        public static AppSettings Load()
        {
            var path = GetSettingsPath();
            try
            {
                if (!File.Exists(path)) return new AppSettings();
                var txt = File.ReadAllText(path);
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var s = JsonSerializer.Deserialize<AppSettings>(txt, opts);
                return s ?? new AppSettings();
            }
            catch
            {
                return new AppSettings();
            }
        }

        public static void Save(AppSettings settings)
        {
            var path = GetSettingsPath();
            var opts = new JsonSerializerOptions { WriteIndented = true };
            var txt = JsonSerializer.Serialize(settings, opts);
            File.WriteAllText(path, txt);
        }
    }
}
