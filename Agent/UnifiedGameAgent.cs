using System.Text;
using System.Text.Json;
using AutoPlayMod.Agent.Clients;
using AutoPlayMod.Core;
using AutoPlayMod.Memory;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Runs;

namespace AutoPlayMod.Agent;

/// <summary>
/// Unified game agent that provides a single agent loop for ALL decision points.
/// Manages multi-turn conversations with Claude, handling query tools internally
/// and returning action tool calls to the caller.
///
/// Combat and non-combat share the same conversation infrastructure:
/// - Combat: one conversation per combat encounter
/// - Non-combat: one conversation per post-combat flow (rewards -> map -> event -> rest)
/// </summary>
public class UnifiedGameAgent
{
    private readonly ILlmClient _client;
    private readonly string _logDir;

    public BattleJournal? Journal { get; set; }
    public MemoryStore? Memory { get; set; }

    /// <summary>Callback to update UI status (Vibing/not). Set by AutoPlayer.</summary>
    public Action<bool>? OnThinkingChanged { get; set; }

    // No artificial query limit — the agent queries as much as it needs.
    // The caller's CancellationToken timeout (typically 30s) is the natural safeguard.

    /// <summary>
    /// Ensures only one RunAgentLoop call executes at a time.
    /// Prevents conversation corruption from concurrent calls
    /// (e.g., combat loop + flow controller overlay handling).
    /// </summary>
    private readonly SemaphoreSlim _agentLock = new(1, 1);

    private bool _inCombatSession;
    private bool _inNonCombatSession;
    private int _combatCount;
    private int _nonCombatCount;

    public UnifiedGameAgent(ILlmClient client, string logDir = "")
    {
        _client = client;
        _logDir = logDir;
    }

    /// <summary>
    /// Start a new conversation for a combat encounter.
    /// </summary>
    public void StartCombatSession()
    {
        // Save previous non-combat session if any
        if (_inNonCombatSession)
            _ = SaveConversationLog($"noncombat_{_nonCombatCount:D3}");

        _inCombatSession = true;
        _inNonCombatSession = false;
        _combatCount++;
        _client.StartConversation();
        Log.Info("[AutoPlay/Agent] Started combat session");
    }

    /// <summary>
    /// Start a new conversation for post-combat flow (rewards, map, events, rest).
    /// The session stays alive so the AI remembers previous choices in the flow.
    /// </summary>
    public void StartNonCombatSession(string initialContext = "")
    {
        // Save previous session if any
        if (_inCombatSession)
            _ = SaveConversationLog($"combat_{_combatCount:D3}_incomplete");
        if (_inNonCombatSession)
            _ = SaveConversationLog($"noncombat_{_nonCombatCount:D3}");

        _inNonCombatSession = true;
        _inCombatSession = false;
        _nonCombatCount++;
        _client.StartConversation();
        Log.Info($"[AutoPlay/Agent] Started non-combat session{(string.IsNullOrEmpty(initialContext) ? "" : " with battle context")}");
    }

    /// <summary>
    /// End the current combat session and save its conversation log.
    /// </summary>
    public async Task EndCombatSession(string outcome)
    {
        if (!_inCombatSession) return;
        _inCombatSession = false;
        await SaveConversationLog($"combat_{_combatCount:D3}_{outcome}");
    }

    /// <summary>
    /// End the current non-combat session and save its conversation log.
    /// </summary>
    public async Task EndNonCombatSession()
    {
        if (!_inNonCombatSession) return;
        _inNonCombatSession = false;
        await SaveConversationLog($"noncombat_{_nonCombatCount:D3}");
    }

    /// <summary>
    /// Core agent loop: send message to Claude, handle query tools internally,
    /// return when an action tool is called.
    /// Returns (toolName, toolInput) or null if no action tool was called.
    /// </summary>
    public async Task<(string toolName, JsonElement input)?> RunAgentLoop(
        string userMessage, JsonElement[] actionTools, string systemPrompt, CancellationToken ct,
        bool includeMemoryTools = true)
    {
        try
        {
            if (!await _agentLock.WaitAsync(TimeSpan.FromSeconds(60), ct))
            {
                Log.Warn("[AutoPlay/Agent] RunAgentLoop lock timeout — another call is in progress, skipping");
                return null;
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled while waiting for lock — no messages were added, no cleanup needed
            Log.Warn("[AutoPlay/Agent] RunAgentLoop cancelled while waiting for lock");
            return null;
        }

        try
        {
            return await RunAgentLoopInternal(userMessage, actionTools, systemPrompt, ct, includeMemoryTools);
        }
        catch (OperationCanceledException)
        {
            // Timeout or cancellation mid-conversation — clean up to prevent corruption
            Log.Warn("[AutoPlay/Agent] RunAgentLoop cancelled, cleaning up conversation state");
            _client.CleanupAfterInterruption();
            return null;
        }
        finally
        {
            _agentLock.Release();
        }
    }

    private async Task<(string toolName, JsonElement input)?> RunAgentLoopInternal(
        string userMessage, JsonElement[] actionTools, string systemPrompt, CancellationToken ct,
        bool includeMemoryTools = true)
    {
        // Combine action tools + query tools into one set
        var allTools = BuildToolSet(actionTools, includeMemoryTools);
        string currentMessage = userMessage;
        int recallCount = 0;
        bool recallDisabled = false;
        const int MaxRecallBeforeThrottle = 6;

        // Loop until the agent chooses an action tool.
        // The caller's CancellationToken timeout is the safeguard against infinite loops.
        while (!ct.IsCancellationRequested)
        {
            OnThinkingChanged?.Invoke(true);
            (string toolName, JsonElement input, string toolUseId)? result;
            try
            {
                result = await _client.SendMessageAsync(systemPrompt, currentMessage, allTools, ct);
            }
            finally
            {
                OnThinkingChanged?.Invoke(false);
            }

            if (!result.HasValue)
            {
                Log.Warn("[AutoPlay/Agent] No tool call in response");
                return null;
            }

            var (toolName, input, toolUseId) = result.Value;
            Log.Info($"[AutoPlay/Agent] Tool call: {toolName} -> {input.GetRawText()}");

            // Handle any extra tool_uses from the same response (parallel tool calls).
            foreach (var extra in _client.ExtraToolUses)
            {
                Log.Info($"[AutoPlay/Agent] Extra tool call: {extra.name} -> {extra.input.GetRawText()}");
                if (ToolDefinitions.QueryToolNames.Contains(extra.name))
                {
                    var extraResult = HandleQueryTool(extra.name, extra.input);
                    _client.SetPendingToolResult(extra.id, extraResult);
                    if (extra.name == "recall_memory") recallCount++;
                }
                else
                {
                    _client.SetPendingToolResult(extra.id, "Action acknowledged but not executed (only one action per turn).");
                }
            }

            // Query tool → handle internally, loop continues
            if (ToolDefinitions.QueryToolNames.Contains(toolName))
            {
                var queryResult = HandleQueryTool(toolName, input);
                _client.SetPendingToolResult(toolUseId, queryResult);

                // Throttle recall_memory — after too many calls, return error as tool_result
                // AND remove recall_memory + save_memory from tool set
                if (toolName == "recall_memory")
                {
                    recallCount++;
                    if (recallCount > MaxRecallBeforeThrottle)
                    {
                        if (!recallDisabled)
                        {
                            recallDisabled = true;
                            allTools = allTools.Where(t =>
                                !t.GetRawText().Contains("\"recall_memory\"") &&
                                !t.GetRawText().Contains("\"save_memory\"")
                            ).ToArray();
                            Log.Info($"[AutoPlay/Agent] recall_memory disabled, removed from tools. Remaining: {allTools.Length}");
                        }
                        // Still return a proper tool_result so the conversation stays valid
                        _client.SetPendingToolResult(toolUseId,
                            "ERROR: recall_memory is disabled. You have queried enough. CHOOSE AN ACTION NOW.");
                        currentMessage = "recall_memory is disabled. Choose an action.";
                        continue;
                    }
                }

                currentMessage = queryResult;
                continue;
            }

            // Action tool → return to caller
            return (toolName, input);
        }

        Log.Warn("[AutoPlay/Agent] Agent loop cancelled (timeout)");
        return null;
    }

    /// <summary>
    /// Notify the agent about a failed action. The error will be sent as the next
    /// tool_result so the AI can adjust.
    /// </summary>
    public void NotifyActionFailed(string errorMessage)
    {
        // The conversation client has a pending tool_use_id, so sending this as the next
        // message will automatically be formatted as a tool_result.
        // We don't need to do anything special here — the caller will include
        // the error in the next RunAgentLoop call's userMessage.
    }

    /// <summary>
    /// Get the raw conversation log for reflection or saving.
    /// </summary>
    public string GetConversationLog()
    {
        return _client.GetConversationLog();
    }

    /// <summary>
    /// Save the current conversation log to disk.
    /// </summary>
    public async Task SaveConversationLog(string label)
    {
        if (string.IsNullOrEmpty(_logDir)) return;

        try
        {
            Directory.CreateDirectory(_logDir);
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            var filename = Path.Combine(_logDir, $"{label}_{timestamp}.json");
            await File.WriteAllTextAsync(filename, _client.GetConversationLog());
            Log.Info($"[AutoPlay/Agent] Conversation log saved: {filename}");
        }
        catch (Exception ex)
        {
            Log.Warn($"[AutoPlay/Agent] Failed to save log: {ex.Message}");
        }
    }

    #region Query Tool Handling

    /// <summary>
    /// Build the combined tool set: action tools + query tools appropriate for the context.
    /// </summary>
    private JsonElement[] BuildToolSet(JsonElement[] actionTools, bool includeMemoryTools = true)
    {
        var tools = new List<JsonElement>(actionTools);

        // Add query tools — optionally exclude memory tools for decisions
        // where all info is already in the prompt
        if (includeMemoryTools)
        {
            tools.AddRange(ToolDefinitions.QueryTools);
        }
        else
        {
            // Only include non-memory query tools (deck inspection, etc.)
            tools.AddRange(ToolDefinitions.QueryTools.Where(t =>
                !t.GetRawText().Contains("\"recall_memory\"") &&
                !t.GetRawText().Contains("\"save_memory\"")));
        }

        // Add combat-only query tools when in combat
        if (_inCombatSession)
        {
            tools.AddRange(ToolDefinitions.CombatQueryTools);
        }

        return tools.ToArray();
    }

    /// <summary>
    /// Execute a query tool and return the result string.
    /// </summary>
    private string HandleQueryTool(string toolName, JsonElement input)
    {
        try
        {
            return toolName switch
            {
                "inspect_deck" => HandleInspectDeck(input),
                "inspect_relics" => HandleInspectRelics(),
                "view_deck_full" => HandleViewDeckFull(),
                "view_battle_history" => HandleViewBattleHistory(input),
                "view_deck_stats" => HandleViewDeckStats(),
                "recall_memory" => HandleRecallMemory(input),
                "save_memory" => HandleSaveMemory(input),
                _ => $"Unknown query tool: {toolName}"
            };
        }
        catch (Exception ex)
        {
            Log.Warn($"[AutoPlay/Agent] Query tool '{toolName}' failed: {ex.Message}");
            return $"Error executing {toolName}: {ex.Message}";
        }
    }

    /// <summary>Inspect a combat pile (draw/discard/exhaust). Combat only.</summary>
    private static string HandleInspectDeck(JsonElement input)
    {
        var pile = input.GetProperty("pile").GetString() ?? "draw";
        var cards = BattleStateCollector.CollectPile(pile);

        if (cards.Count == 0)
            return $"{pile} pile is empty.";

        var sb = new StringBuilder();
        sb.AppendLine($"{pile} pile ({cards.Count} cards):");
        foreach (var c in cards)
            sb.AppendLine($"  {c.Name}{(c.Upgraded ? "+" : "")} ({c.Cost} energy, {c.Type}) - {c.Description}");
        return sb.ToString();
    }

    /// <summary>View all relics. Works in any context.</summary>
    private static string HandleInspectRelics()
    {
        try
        {
            var runState = RunManager.Instance?.DebugOnlyGetState();
            var player = runState?.Players.FirstOrDefault();
            if (player == null) return "No relics available (player not found).";

            var relics = new List<string>();
            foreach (var relic in player.Relics)
            {
                string desc = "";
                try { desc = relic.Description?.GetRawText() ?? ""; }
                catch { }

                var name = relic.Title?.GetRawText() ?? relic.GetType().Name;
                var counter = relic.DisplayAmount > 0 ? $" [{relic.DisplayAmount}]" : "";
                relics.Add($"  {name}{counter} - {desc}");
            }

            if (relics.Count == 0) return "No relics.";
            return $"Relics ({relics.Count}):\n{string.Join("\n", relics)}";
        }
        catch (Exception ex)
        {
            return $"Error inspecting relics: {ex.Message}";
        }
    }

    /// <summary>View full deck with descriptions. Works outside combat.</summary>
    private static string HandleViewDeckFull()
    {
        try
        {
            var runState = RunManager.Instance?.DebugOnlyGetState();
            var player = runState?.Players.FirstOrDefault();
            var pcs = player?.PlayerCombatState;
            if (pcs == null) return "Deck not available (not in a run or player not found).";

            var cards = pcs.AllCards.ToList();
            if (cards.Count == 0) return "Deck is empty.";

            var sb = new StringBuilder();
            sb.AppendLine($"Full deck ({cards.Count} cards):");
            foreach (var card in cards)
            {
                var name = card.Title ?? card.GetType().Name;
                var upgraded = card.IsUpgraded ? "+" : "";
                var cost = card.EnergyCost?.GetAmountToSpend() ?? 0;
                var type = card.Type.ToString();
                var rarity = card.Rarity.ToString();
                string desc = "";
                try { desc = card.GetDescriptionForPile(MegaCrit.Sts2.Core.Entities.Cards.PileType.None) ?? ""; }
                catch { }

                sb.AppendLine($"  {name}{upgraded} ({cost} energy, {type}, {rarity}) - {desc}");
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Error viewing deck: {ex.Message}";
        }
    }

    /// <summary>View recent battle history from journal.</summary>
    private string HandleViewBattleHistory(JsonElement input)
    {
        if (Journal == null) return "No battle history available (journal not configured).";

        int count = 3;
        if (input.TryGetProperty("count", out var countEl))
            count = Math.Clamp(countEl.GetInt32(), 1, 10);

        var context = Journal.GetReflectionContext(count);
        return string.IsNullOrEmpty(context) ? "No battle history yet." : context;
    }

    /// <summary>Recall from persistent memory.</summary>
    private string HandleRecallMemory(JsonElement input)
    {
        if (Memory == null) return "Memory system not available.";

        var category = input.GetProperty("category").GetString() ?? "card";
        var name = input.TryGetProperty("name", out var n) ? n.GetString() : null;
        var keyword = input.TryGetProperty("keyword", out var k) ? k.GetString() : null;

        try
        {
            if (category == "strategy")
            {
                var (charStrat, genStrat) = Memory.ReadStrategy();
                var sb = new StringBuilder();
                if (charStrat != null && charStrat.Observations.Count > 0)
                {
                    sb.AppendLine("Character strategy:");
                    foreach (var obs in charStrat.Observations) sb.AppendLine($"  - {obs}");
                }
                if (genStrat != null && genStrat.Observations.Count > 0)
                {
                    sb.AppendLine("General strategy:");
                    foreach (var obs in genStrat.Observations) sb.AppendLine($"  - {obs}");
                }
                return sb.Length > 0 ? sb.ToString() : "No strategy memories yet.";
            }

            if (category == "archetype")
            {
                if (!string.IsNullOrEmpty(name))
                {
                    var arch = Memory.ReadArchetype(name);
                    return arch != null ? arch.ToInjectionString() : $"No archetype memory for '{name}'.";
                }
                var archetypes = Memory.GetAllArchetypes();
                if (archetypes.Count == 0) return "No archetype memories yet.";
                return string.Join("\n\n", archetypes.Select(a => a.ToInjectionString()));
            }

            if (category == "run")
            {
                var count = input.TryGetProperty("count", out var c) ? c.GetInt32() : 5;
                var runs = Memory.GetRecentRuns(count);
                if (runs.Count == 0) return "No run memories yet.";
                var sb = new StringBuilder();
                foreach (var run in runs)
                {
                    sb.AppendLine($"[Run #{run.RunId} | {run.Character} | {run.Archetype} | {run.Result} | Floor {run.FinalFloor}]");
                    if (!string.IsNullOrEmpty(run.CauseOfEnd)) sb.AppendLine($"  Cause: {run.CauseOfEnd}");
                    foreach (var lesson in run.Lessons) sb.AppendLine($"  Lesson: {lesson}");
                }
                return sb.ToString();
            }

            // Batch lookup: names array
            var names = new List<string>();
            if (input.TryGetProperty("names", out var namesArr) && namesArr.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var n2 in namesArr.EnumerateArray())
                {
                    var s = n2.GetString();
                    if (!string.IsNullOrEmpty(s)) names.Add(s);
                }
            }
            // Single name fallback
            if (names.Count == 0 && !string.IsNullOrEmpty(name))
                names.Add(name);

            // Entity categories: card, enemy, event, relic
            if (names.Count > 0)
            {
                var results = new List<string>();
                bool anyMiss = false;
                foreach (var entityName in names)
                {
                    var entry = Memory.Read(category, entityName);
                    if (entry != null)
                        results.Add(entry.ToInjectionString());
                    else
                    {
                        results.Add($"No memory for {category} '{entityName}'.");
                        anyMiss = true;
                    }
                }
                // On any miss, show what IS available so agent can use correct names or stop guessing
                if (anyMiss)
                {
                    var available = Memory.GetEntityNames(category);
                    if (available.Count > 0)
                        results.Add($"Available {category} memories: {string.Join(", ", available.Take(30))}");
                    else
                        results.Add($"No {category} memories exist yet — this knowledge will accumulate over time.");
                }
                return string.Join("\n\n", results);
            }

            if (!string.IsNullOrEmpty(keyword))
            {
                var results = Memory.Search(category, keyword);
                if (results.Count == 0)
                {
                    var available = Memory.GetEntityNames(category);
                    if (available.Count > 0)
                        return $"No {category} memories matching '{keyword}'. Available: {string.Join(", ", available.Take(30))}";
                    return $"No {category} memories exist yet.";
                }
                return string.Join("\n\n", results.Take(10).Select(e => e.ToInjectionString()));
            }

            return $"Provide 'names' for batch lookup, 'name' for single lookup, or 'keyword' for search.";
        }
        catch (Exception ex)
        {
            return $"Error recalling memory: {ex.Message}";
        }
    }

    /// <summary>Save to persistent memory.</summary>
    private string HandleSaveMemory(JsonElement input)
    {
        if (Memory == null) return "Memory system not available.";

        var category = input.GetProperty("category").GetString() ?? "card";
        var name = input.GetProperty("name").GetString() ?? "";
        var observation = input.GetProperty("observation").GetString() ?? "";
        var rating = input.TryGetProperty("rating", out var r) ? (int?)r.GetInt32() : null;
        List<string>? synergies = null;
        if (input.TryGetProperty("synergies", out var syn))
            synergies = syn.EnumerateArray().Select(s => s.GetString() ?? "").Where(s => s.Length > 0).ToList();

        try
        {
            // Validate relic names against player's actual relics
            if (category == "relic")
            {
                var validRelicNames = GetCurrentRelicNames();
                if (validRelicNames.Count > 0 && !validRelicNames.Any(r =>
                    r.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                    AutoPlayMod.Memory.MemoryStore.SanitizeName(r) == AutoPlayMod.Memory.MemoryStore.SanitizeName(name)))
                {
                    return $"Rejected: '{name}' is not a valid relic name. " +
                           $"Your relics: {string.Join(", ", validRelicNames)}. Use the EXACT relic name.";
                }
            }

            // Reject agent-written archetypes — archetypes are auto-generated from run data
            if (category == "archetype")
            {
                return "Rejected: archetypes are auto-generated from completed runs. " +
                       "Use save_memory with category 'strategy' for strategic insights instead.";
            }

            if (category == "strategy")
            {
                // Reject run-specific observations (floor numbers, specific HP values, etc.)
                // Strategy should be general reusable insights
                if (System.Text.RegularExpressions.Regex.IsMatch(observation,
                    @"(?i)(floor \d|at floor|F\d+:|HP \d+/\d+|\d+/\d+ HP|deck at|deck has \d)"))
                {
                    return "Rejected: strategy observations must be GENERAL insights, not run-specific details. " +
                           "Use recall_memory/save_memory with category 'card' or 'enemy' for specific knowledge.";
                }
                var scope = name == "general" ? "general" : "character";
                Memory.AppendStrategy(scope, observation);
                return $"Strategy observation saved ({scope}).";
            }

            Memory.Write(category, name, observation, rating, synergies);
            return $"Memory saved for {category} '{name}'.";
        }
        catch (Exception ex)
        {
            return $"Error saving memory: {ex.Message}";
        }
    }

    /// <summary>Get current player's relic names from game state.</summary>
    private static List<string> GetCurrentRelicNames()
    {
        try
        {
            var runState = MegaCrit.Sts2.Core.Runs.RunManager.Instance?.DebugOnlyGetState();
            var player = runState?.Players.FirstOrDefault();
            if (player == null) return [];
            return player.Relics.Select(r => r.Title?.GetRawText() ?? r.GetType().Name).ToList();
        }
        catch { return []; }
    }

    /// <summary>Compute deck statistics.</summary>
    private static string HandleViewDeckStats()
    {
        try
        {
            var runState = RunManager.Instance?.DebugOnlyGetState();
            var player = runState?.Players.FirstOrDefault();
            var pcs = player?.PlayerCombatState;
            if (pcs == null) return "Deck stats not available.";

            var cards = pcs.AllCards.ToList();
            if (cards.Count == 0) return "Deck is empty.";

            int attacks = 0, skills = 0, powers = 0, other = 0;
            int totalCost = 0, upgraded = 0;

            foreach (var card in cards)
            {
                var type = card.Type.ToString().ToLowerInvariant();
                if (type.Contains("attack")) attacks++;
                else if (type.Contains("skill")) skills++;
                else if (type.Contains("power")) powers++;
                else other++;

                totalCost += card.EnergyCost?.GetAmountToSpend() ?? 0;
                if (card.IsUpgraded) upgraded++;
            }

            var avgCost = cards.Count > 0 ? (double)totalCost / cards.Count : 0;

            return $"""
                Deck Statistics:
                  Total cards: {cards.Count}
                  Attacks: {attacks} | Skills: {skills} | Powers: {powers}{(other > 0 ? $" | Other: {other}" : "")}
                  Average cost: {avgCost:F1} energy
                  Upgraded: {upgraded}/{cards.Count}
                """;
        }
        catch (Exception ex)
        {
            return $"Error computing deck stats: {ex.Message}";
        }
    }

    #endregion
}
