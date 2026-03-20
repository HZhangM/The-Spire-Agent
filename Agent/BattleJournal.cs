using System.Text;
using System.Text.Json;
using AutoPlayMod.Core;
using MegaCrit.Sts2.Core.Logging;

namespace AutoPlayMod.Agent;

/// <summary>
/// Records battle events and performs post-combat reflection.
/// Inspired by GameSense's five-phase learning loop:
///   1. Track actions during combat
///   2. Structured post-combat review
///   3. Extract lessons and strategy updates
///   4. Persist to file for cross-battle learning
///   5. Load reflections as context for future decisions
/// </summary>
public class BattleJournal
{
    private readonly string _journalPath;
    private readonly ILlmClient? _client;

    /// <summary>
    /// Reference to the unified agent for getting conversation logs.
    /// Set by ModEntry when wiring up dependencies.
    /// </summary>
    public UnifiedGameAgent? Agent { get; set; }

    /// <summary>
    /// Background memory writer for post-combat memory extraction.
    /// Set by ModEntry when wiring up dependencies.
    /// </summary>
    public Memory.BackgroundMemoryWriter? BackgroundWriter { get; set; }

    // Current battle tracking
    private readonly List<string> _actionLog = [];
    private readonly List<string> _cardsUsed = [];
    private readonly List<string> _enemyNames = [];
    private int _turnCount;
    private int _startHp;
    private int _totalDamageTaken;
    private string _enemies = "";

    // Persistent reflections (loaded at startup, appended after each battle)
    private readonly List<BattleReflection> _reflections = [];
    private const int MaxReflections = 20; // Keep last N reflections

    public BattleJournal(string journalPath, ILlmClient? client = null)
    {
        _journalPath = journalPath;
        _client = client;
        LoadReflections();
    }

    /// <summary>Get the most recent reflection (for RunContextExtractor).</summary>
    public BattleReflection? GetLatestReflection()
    {
        lock (_reflections) { return _reflections.Count > 0 ? _reflections[^1] : null; }
    }

    /// <summary>Get recent reflections as context string for prompts.</summary>
    public string GetReflectionContext(int maxEntries = 5)
    {
        lock (_reflections)
        {
        if (_reflections.Count == 0) return "";

        var recent = _reflections.TakeLast(maxEntries);
        var sb = new StringBuilder();
        foreach (var r in recent)
        {
            sb.AppendLine($"[Battle vs {r.Enemies} | {r.Outcome} | HP {r.RemainingHp}/{r.MaxHp} | Rating: {r.Rating}/5]");
            if (!string.IsNullOrEmpty(r.HpLossAnalysis))
                sb.AppendLine($"  HP Loss: {r.HpLossAnalysis}");
            if (!string.IsNullOrEmpty(r.DeckGaps))
                sb.AppendLine($"  Deck Gaps: {r.DeckGaps}");
            if (r.Lessons.Count > 0)
                sb.AppendLine($"  Lessons: {string.Join("; ", r.Lessons)}");
            if (!string.IsNullOrEmpty(r.StrategyUpdate))
                sb.AppendLine($"  Strategy: {r.StrategyUpdate}");
        }
        return sb.ToString();
        } // lock
    }

    /// <summary>Call at combat start to reset tracking.</summary>
    public void OnCombatStart(BattleState state)
    {
        _actionLog.Clear();
        _cardsUsed.Clear();
        _enemyNames.Clear();
        _turnCount = 0;
        _startHp = state.Player.Hp;
        _totalDamageTaken = 0;
        _enemies = string.Join(", ", state.Enemies.Select(e => $"{e.Name}(HP:{e.MaxHp})"));
        _enemyNames.AddRange(state.Enemies.Select(e => e.Name).Distinct());
        Log.Info($"[AutoPlay/Journal] Combat started vs {_enemies}");
    }

    /// <summary>Record a text note in the combat log (e.g. timeout marker).</summary>
    public void RecordAction(string note)
    {
        _actionLog.Add(note);
    }

    /// <summary>Record an action taken during combat with resolved names.</summary>
    public void RecordAction(CombatAction action, BattleState state)
    {
        if (action.Type == CombatActionType.EndTurn)
        {
            _turnCount++;
            _actionLog.Add($"--- Turn {_turnCount} end (energy left: {state.Player.Energy}) ---");
            return;
        }

        var entry = $"T{_turnCount + 1}: ";
        if (action.Type == CombatActionType.PlayCard)
        {
            var cardName = "?";
            if (action.CardIndex >= 0 && action.CardIndex < state.Hand.Count)
            {
                var card = state.Hand[action.CardIndex];
                cardName = card.Name + (card.Upgraded ? "+" : "");
            }
            var targetName = "";
            if (action.TargetIndex.HasValue)
            {
                var ti = action.TargetIndex.Value;
                if (ti >= 0 && ti < state.Enemies.Count)
                    targetName = $" → {state.Enemies[ti].Name}";
            }
            entry += $"Play {cardName}{targetName}";
        }
        else if (action.Type == CombatActionType.UsePotion)
        {
            var potionName = "?";
            if (action.PotionIndex >= 0 && action.PotionIndex < state.Potions.Count)
                potionName = state.Potions[action.PotionIndex].Name;
            var targetName = "";
            if (action.TargetIndex.HasValue)
            {
                var ti = action.TargetIndex.Value;
                if (ti >= 0 && ti < state.Enemies.Count)
                    targetName = $" → {state.Enemies[ti].Name}";
            }
            entry += $"Use {potionName}{targetName}";
        }

        _actionLog.Add(entry);

        // Track unique card names used this combat
        if (action.Type == CombatActionType.PlayCard && action.CardIndex >= 0 && action.CardIndex < state.Hand.Count)
        {
            var name = state.Hand[action.CardIndex].Name;
            if (!_cardsUsed.Contains(name)) _cardsUsed.Add(name);
        }
    }

    /// <summary>
    /// Call after combat ends. Performs reflection if LLM client is available.
    /// </summary>
    public async Task OnCombatEnd(BattleState finalState, bool victory)
    {
        var hpLost = _startHp - finalState.Player.Hp;
        _totalDamageTaken = Math.Max(0, hpLost);

        Log.Info($"[AutoPlay/Journal] Combat ended: {(victory ? "VICTORY" : "DEFEAT")} " +
                 $"HP: {finalState.Player.Hp}/{finalState.Player.MaxHp} " +
                 $"Turns: {_turnCount} Enemies: {_enemies}");

        // Always save a basic reflection
        var reflection = new BattleReflection
        {
            Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            Enemies = _enemies,
            Outcome = victory ? "Victory" : "Defeat",
            RemainingHp = finalState.Player.Hp,
            MaxHp = finalState.Player.MaxHp,
            Turns = _turnCount,
            Rating = victory ? 3 : 1,
            Lessons = [],
            StrategyUpdate = ""
        };

        // Save basic reflection immediately (non-blocking, no API call)
        _reflections.Add(reflection);
        while (_reflections.Count > MaxReflections)
            _reflections.RemoveAt(0);
        SaveReflections();
        Log.Info($"[AutoPlay/Journal] Basic reflection saved (rating: {reflection.Rating}/5)");

        // Fire AI-powered reflection + memory extraction in background (non-blocking).
        // If the user quits before this completes, at least the basic reflection is saved.
        // Snapshot all mutable state BEFORE starting background task
        var client = _client;
        var bgWriter = BackgroundWriter;
        var enemies = _enemies;
        var startHp = _startHp;
        var turnCount = _turnCount;
        var actionLog = _actionLog.ToList();
        var enemyNamesCopy = _enemyNames.ToList();
        var cardsUsedCopy = _cardsUsed.ToList();
        var conversationLog = Agent?.GetConversationLog() ?? (client as ILlmClient)?.GetConversationLog() ?? "";
        string previousContext;
        lock (_reflections) { previousContext = GetReflectionContext(3); }
        var agent = Agent;

        _ = Task.Run(async () =>
        {
            try
            {
                if (client != null)
                {
                    var aiReflection = await PerformAiReflection(client, finalState, victory,
                        enemies, startHp, turnCount, actionLog, conversationLog, previousContext);
                    if (aiReflection != null)
                    {
                        // Replace the basic reflection with the AI one
                        lock (_reflections)
                        {
                            var idx = _reflections.IndexOf(reflection);
                            if (idx >= 0) _reflections[idx] = aiReflection;
                        }
                        SaveReflections();
                        reflection = aiReflection;
                        Log.Info($"[AutoPlay/Journal] AI reflection saved (rating: {aiReflection.Rating}/5)");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"[AutoPlay/Journal] AI reflection failed: {ex.Message}");
            }

            // Memory extraction with accurate names from game state
            try
            {
                if (bgWriter != null)
                {
                    bgWriter.ProcessCombatEnd(reflection, enemyNamesCopy, cardsUsedCopy);
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"[AutoPlay/Journal] Background memory extraction failed: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// All parameters are snapshots — safe to call from background thread.
    /// Does NOT access any instance fields.
    /// </summary>
    private async Task<BattleReflection?> PerformAiReflection(ILlmClient toolClient, BattleState finalState, bool victory,
        string enemies, int startHp, int turnCount, List<string> actionLog, string conversationLog, string previousContext)
    {
        var tools = new[] { ToolDefinitions.Reflection };

        var battleSummary = new StringBuilder();
        battleSummary.AppendLine($"Enemies: {enemies}");
        battleSummary.AppendLine($"Outcome: {(victory ? "Victory" : "Defeat")}");
        battleSummary.AppendLine($"HP: {startHp} -> {finalState.Player.Hp}/{finalState.Player.MaxHp}");
        battleSummary.AppendLine($"Turns: {turnCount}");
        battleSummary.AppendLine($"Relics: {string.Join(", ", finalState.Relics.Select(r => r.Name))}");

        if (!string.IsNullOrEmpty(conversationLog) && conversationLog != "[]")
        {
            battleSummary.AppendLine($"\n=== FULL COMBAT CONVERSATION LOG ===");
            battleSummary.AppendLine(conversationLog);
            battleSummary.AppendLine($"=== END LOG ===");
        }
        else
        {
            battleSummary.AppendLine($"\nAction log ({actionLog.Count} actions):");
            foreach (var action in actionLog.TakeLast(30))
                battleSummary.AppendLine($"  {action}");
        }

        if (finalState.Player.Powers.Count > 0)
            battleSummary.AppendLine($"\nFinal player powers: {string.Join(", ", finalState.Player.Powers.Select(p => $"{p.Name}x{p.Amount}"))}");

        var userPrompt = $"""
            {battleSummary}

            Previous battle reflections for context:
            {(string.IsNullOrEmpty(previousContext) ? "(first battle)" : previousContext)}

            Analyze this battle and provide structured reflection. Pay special attention to:
            - HP LOSS ANALYSIS: Was HP lost because of insufficient offense (fight dragged on) or
              insufficient defense (didn't block big hits)? Be specific about which turns and decisions.
            - DECK GAPS: What card types/effects would have improved this fight?
              Categories: Offense (damage/scaling/AOE), Defense (block/weak), Economy (energy/cost), Draw (draw/thin/scry)
            - Turns where cards were available but end_turn was called early
            - Missed combos (e.g. Vulnerable not applied before big attacks)
            - Inefficient energy usage
            - Poor target selection
            """;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var result = await toolClient.CompleteWithToolAsync(
            Prompts.ReflectionSystem,
            userPrompt,
            tools,
            cts.Token);

        if (!result.HasValue) return null;

        var input = result.Value.GetProperty("input");

        var reflection = new BattleReflection
        {
            Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            Enemies = _enemies,
            Outcome = victory ? "Victory" : "Defeat",
            RemainingHp = finalState.Player.Hp,
            MaxHp = finalState.Player.MaxHp,
            Turns = _turnCount,
            Rating = input.TryGetProperty("performance_rating", out var r) ? r.GetInt32() : (victory ? 3 : 1),
            HpLossAnalysis = input.TryGetProperty("hp_loss_analysis", out var hla) ? hla.GetString() ?? "" : "",
            DeckGaps = input.TryGetProperty("deck_gaps", out var dg) ? dg.GetString() ?? "" : "",
            Lessons = input.TryGetProperty("lessons", out var l)
                ? l.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToList()
                : [],
            StrategyUpdate = input.TryGetProperty("strategy_update", out var s) ? s.GetString() ?? "" : "",
            KeyMistakes = input.TryGetProperty("key_mistakes", out var m)
                ? m.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
                : [],
        };

        foreach (var lesson in reflection.Lessons)
            Log.Info($"[AutoPlay/Journal] Lesson: {lesson}");
        if (!string.IsNullOrEmpty(reflection.StrategyUpdate))
            Log.Info($"[AutoPlay/Journal] Strategy update: {reflection.StrategyUpdate}");

        return reflection;
    }

    #region Persistence

    private void LoadReflections()
    {
        if (!File.Exists(_journalPath)) return;
        try
        {
            var json = File.ReadAllText(_journalPath);
            var list = JsonSerializer.Deserialize<List<BattleReflection>>(json, JsonCtx.Default);
            if (list != null) _reflections.AddRange(list);
            Log.Info($"[AutoPlay/Journal] Loaded {_reflections.Count} reflections from {_journalPath}");
        }
        catch (Exception ex)
        {
            Log.Warn($"[AutoPlay/Journal] Failed to load reflections: {ex.Message}");
        }
    }

    private void SaveReflections()
    {
        try
        {
            var dir = Path.GetDirectoryName(_journalPath);
            if (dir != null) Directory.CreateDirectory(dir);
            string json;
            lock (_reflections)
            {
                json = JsonSerializer.Serialize(_reflections, new JsonSerializerOptions { WriteIndented = true });
            }
            File.WriteAllText(_journalPath, json);
        }
        catch (Exception ex)
        {
            Log.Warn($"[AutoPlay/Journal] Failed to save reflections: {ex.Message}");
        }
    }

    #endregion
}

public class BattleReflection
{
    public string Timestamp { get; set; } = "";
    public string Enemies { get; set; } = "";
    public string Outcome { get; set; } = "";
    public int RemainingHp { get; set; }
    public int MaxHp { get; set; }
    public int Turns { get; set; }
    public int Rating { get; set; }
    public string HpLossAnalysis { get; set; } = "";
    public string DeckGaps { get; set; } = "";
    public List<string> Lessons { get; set; } = [];
    public List<string> KeyMistakes { get; set; } = [];
    public string StrategyUpdate { get; set; } = "";
}
