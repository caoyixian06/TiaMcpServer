using System;
using System.IO;
using Newtonsoft.Json;

namespace TiaMcpServer;

internal sealed class BeginnerUserSettings
{
    public int ConfigVersion { get; set; } = 3;
    public bool FirstRunCompleted { get; set; }
    public bool BeginnerMode { get; set; } = true; // V2 兼容字段
    public string ExecutionMode { get; set; } = "Safe";
    public string PreferredClient { get; set; } = "";

    public static string SettingsPath
    {
        get
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "博途智能工程助手");
            try { Directory.CreateDirectory(root); } catch { }
            return Path.Combine(root, "用户设置.json");
        }
    }

    public static BeginnerUserSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new BeginnerUserSettings();
            var text = File.ReadAllText(SettingsPath);
            var settings = JsonConvert.DeserializeObject<BeginnerUserSettings>(text) ?? new BeginnerUserSettings();
            if (settings.ConfigVersion < 3)
            {
                settings.ExecutionMode = settings.BeginnerMode ? "Safe" : "Assist";
                settings.ConfigVersion = 3;
            }
            return settings;
        }
        catch
        {
            return new BeginnerUserSettings();
        }
    }


    public TiaMcpServer.ExecutionMode ResolveExecutionMode()
    {
        return Enum.TryParse(ExecutionMode, true, out TiaMcpServer.ExecutionMode mode)
            ? mode
            : (BeginnerMode ? TiaMcpServer.ExecutionMode.Safe : TiaMcpServer.ExecutionMode.Assist);
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(SettingsPath, JsonConvert.SerializeObject(this, Formatting.Indented));
        }
        catch { }
    }
}
