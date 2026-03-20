using System.Reflection;
using System.Runtime.Loader;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using AutoPlayMod.Config;
using AutoPlayMod.Core;
using AutoPlayMod.Scripting;
using AutoPlayMod.Agent;
using AutoPlayMod.Agent.Clients;

namespace AutoPlayMod;

[ModInitializer("Initialize")]
public class ModEntry
{
    public static ModEntry? Instance { get; private set; }

    public AutoPlayer AutoPlayer { get; } = new();
    public ModConfig Config { get; private set; } = new();

    private static readonly string ModDir = Path.GetDirectoryName(
        System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".";

    public static void Initialize()
    {
        try
        {
            // Register assembly resolver so the runtime can find MoonSharp etc.
            AssemblyLoadContext.Default.Resolving += ResolveModAssembly;

            Log.Info("[AutoPlay] ========================================");
            Log.Info("[AutoPlay] Auto-Play Mod initializing...");

            var entry = new ModEntry();
            Instance = entry;

            // Load config
            var configPath = Path.Combine(ModDir, "autoplay_config.json");
            entry.Config = ModConfig.Load(configPath);

            // Set up strategy based on mode
            entry.SetupStrategy();

            // Apply Harmony patches
            var harmony = new Harmony("autoplaymod.patch");
            harmony.PatchAll();

            var hotkey = entry.Config.ToggleHotkey;
            Log.Info($"[AutoPlay] Mode: {entry.Config.Mode}");
            Log.Info($"[AutoPlay] Strategy: {entry.AutoPlayer.ActiveStrategy?.Name ?? "none"}");
            Log.Info($"[AutoPlay] Toggle hotkey: {hotkey}");
            Log.Info($"[AutoPlay] Initialized! Press {hotkey} in combat to toggle.");
            Log.Info("[AutoPlay] ========================================");
        }
        catch (Exception ex)
        {
            Log.Error($"[AutoPlay] FATAL init error: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private void SetupStrategy()
    {
        var scriptPath = ResolvePath(Config.ScriptPath);

        switch (Config.Mode.ToLowerInvariant())
        {
            case "script":
                SetupScriptMode(scriptPath);
                break;

            case "agentic":
                SetupAgenticMode(scriptPath);
                break;

            case "agent":
                SetupAgentMode();
                break;

            default:
                Log.Warn($"[AutoPlay] Unknown mode '{Config.Mode}', falling back to 'script'");
                SetupScriptMode(scriptPath);
                break;
        }
    }

    private void SetupScriptMode(string scriptPath)
    {
        // Try Lua first, fall back to SimpleStrategy
        try
        {
            if (!File.Exists(scriptPath))
            {
                Log.Warn($"[AutoPlay] Script not found: {scriptPath}, using SimpleStrategy");
                AutoPlayer.SetStrategy(new SimpleStrategy());
                return;
            }
            var strategy = new LuaStrategy(scriptPath);
            AutoPlayer.SetStrategy(strategy);
        }
        catch (Exception ex)
        {
            Log.Warn($"[AutoPlay] Lua init failed: {ex.Message}, using SimpleStrategy");
            AutoPlayer.SetStrategy(new SimpleStrategy());
        }
    }

    private void SetupAgenticMode(string scriptPath)
    {
        var client = CreateLlmClient();
        if (client == null)
        {
            Log.Warn("[AutoPlay] No LLM client configured, falling back to script mode");
            SetupScriptMode(scriptPath);
            return;
        }

        // Use tool-based advisor for non-combat decisions
        AutoPlayer.Handler.Advisor = new GameAgentAdvisor(client, timeoutMs: Config.LlmTimeoutMs);

        // Try Lua for combat, fall back to SimpleStrategy
        try
        {
            if (!File.Exists(scriptPath))
            {
                Log.Warn($"[AutoPlay] Script not found: {scriptPath}, using SimpleStrategy for combat");
                AutoPlayer.SetStrategy(new SimpleStrategy());
                return;
            }
            var luaStrategy = new LuaStrategy(scriptPath);
            var savePath = ResolvePath(Config.AgenticScriptSavePath);
            var strategy = new AgenticScriptStrategy(luaStrategy, client, savePath);
            AutoPlayer.SetStrategy(strategy);
        }
        catch (Exception ex)
        {
            Log.Warn($"[AutoPlay] Lua failed: {ex.Message}, using SimpleStrategy for combat");
            AutoPlayer.SetStrategy(new SimpleStrategy());
        }
    }

    private void SetupAgentMode()
    {
        var client = CreateLlmClient();
        if (client == null)
        {
            Log.Error("[AutoPlay] Agent mode requires an LLM API key. Check autoplay_config.json");
            return;
        }

        // Agent makes ALL decisions: combat via tool use, non-combat via tool use
        // Journal goes to user data dir (not mod dir) so it persists across mod updates
        var userDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SlayTheSpire2", "AutoPlayMod");
        Directory.CreateDirectory(userDataDir);

        var logDir = Path.Combine(userDataDir, "conversation_logs");
        var journalPath = Path.Combine(userDataDir, "battle_journal.json");
        var journal = new BattleJournal(journalPath, client);

        // Initialize memory system
        var memoriesPath = Path.Combine(userDataDir, "memories");
        // TODO: detect character dynamically; for now default to "ironclad"
        var memoryStore = new Memory.MemoryStore(memoriesPath, "ironclad");
        var bgWriter = new Memory.BackgroundMemoryWriter(memoryStore, client);

        // Create the unified agent — shared by both combat and non-combat
        var agent = new UnifiedGameAgent(client, logDir) { Journal = journal, Memory = memoryStore };
        journal.Agent = agent;
        journal.BackgroundWriter = bgWriter;

        var strategy = new AgentStrategy(client, agent, Config.LlmTimeoutMs, logDir)
        {
            RunContext = AutoPlayer.RunContext
        };
        AutoPlayer.SetStrategy(strategy);

        var advisor = new GameAgentAdvisor(client, agent, Config.LlmTimeoutMs)
        {
            Memory = memoryStore,
            RunContext = AutoPlayer.RunContext
        };
        AutoPlayer.Handler.Advisor = advisor;
        AutoPlayer.Handler.CombatAgent = agent;
        AutoPlayer.ContextExtractor = new Core.RunContextExtractor(AutoPlayer.RunContext, client);
        AutoPlayer.BackgroundWriter = bgWriter;
    }

    private ILlmClient? CreateLlmClient()
    {
        if (string.IsNullOrWhiteSpace(Config.LlmApiKey))
        {
            Log.Warn("[AutoPlay] No LLM API key configured");
            return null;
        }

        var provider = Config.LlmProvider.ToLowerInvariant();
        var model = Config.LlmModel;

        return provider switch
        {
            "claude" => new ClaudeClient(
                Config.LlmApiKey,
                string.IsNullOrEmpty(model) ? "claude-sonnet-4-20250514" : model),

            "gpt" => new GptClient(
                Config.LlmApiKey,
                string.IsNullOrEmpty(model) ? "gpt-4o" : model,
                string.IsNullOrEmpty(Config.LlmBaseUrl) ? null : Config.LlmBaseUrl),

            "gemini" => new GeminiClient(
                Config.LlmApiKey,
                string.IsNullOrEmpty(model) ? "gemini-2.5-flash" : model),

            _ => throw new InvalidOperationException($"Unknown LLM provider: {provider}")
        };
    }

    private static string ResolvePath(string path)
    {
        if (Path.IsPathRooted(path)) return path;
        return Path.Combine(ModDir, path);
    }

    /// <summary>
    /// Custom assembly resolver: loads DLLs from the mod directory.
    /// Needed because Godot's runtime doesn't search mod folders for dependencies.
    /// </summary>
    private static Assembly? ResolveModAssembly(AssemblyLoadContext context, AssemblyName assemblyName)
    {
        var dllPath = Path.Combine(ModDir, assemblyName.Name + ".dll");
        if (File.Exists(dllPath))
        {
            Log.Info($"[AutoPlay] Resolving assembly: {assemblyName.Name} from {dllPath}");
            return context.LoadFromAssemblyPath(dllPath);
        }
        return null;
    }
}
