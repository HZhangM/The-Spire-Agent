using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using MegaCrit.Sts2.Core.Logging;

namespace AutoPlayMod.Config;

public class ModConfig
{
    /// <summary>Which mode to use: "script", "agentic", "agent"</summary>
    public string Mode { get; set; } = "script";

    /// <summary>Path to the Lua strategy script (for script and agentic modes)</summary>
    public string ScriptPath { get; set; } = "scripts/default_strategy.lua";

    /// <summary>Hotkey to toggle auto-play (Godot Key enum name)</summary>
    public string ToggleHotkey { get; set; } = "F10";

    /// <summary>LLM provider: "claude", "gpt", "gemini"</summary>
    public string LlmProvider { get; set; } = "claude";

    /// <summary>API key for the LLM provider</summary>
    public string LlmApiKey { get; set; } = "";

    /// <summary>Model name override (empty = use default)</summary>
    public string LlmModel { get; set; } = "";

    /// <summary>Custom API base URL (for OpenAI-compatible endpoints)</summary>
    public string LlmBaseUrl { get; set; } = "";

    /// <summary>Delay between auto-play actions in ms</summary>
    public int ActionDelayMs { get; set; } = 300;

    /// <summary>LLM request timeout in ms</summary>
    public int LlmTimeoutMs { get; set; } = 60000;

    /// <summary>Path to save improved scripts (agentic mode)</summary>
    public string AgenticScriptSavePath { get; set; } = "scripts/improved_strategy.lua";

    /// <summary>Parse the hotkey string to a Godot Key</summary>
    public Key GetToggleKey()
    {
        if (Enum.TryParse<Key>(ToggleHotkey, true, out var key))
            return key;
        return Key.F10;
    }

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    };

    public static ModConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            Log.Info($"[AutoPlay] Config not found at {path}, creating default");
            var config = new ModConfig();
            config.Save(path);
            return config;
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ModConfig>(json, _jsonOptions) ?? new ModConfig();
        }
        catch (Exception ex)
        {
            Log.Error($"[AutoPlay] Failed to load config: {ex.Message}");
            return new ModConfig();
        }
    }

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (dir != null) Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(this, _jsonOptions);
        File.WriteAllText(path, json);
    }
}
