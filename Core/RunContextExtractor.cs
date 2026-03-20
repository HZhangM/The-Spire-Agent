using System.Text;
using System.Text.Json;
using AutoPlayMod.Agent;
using AutoPlayMod.Agent;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Runs;

namespace AutoPlayMod.Core;

/// <summary>
/// Automatically extracts and updates RunContext after each session.
/// Uses the LLM to summarize decisions and update strategy — runs in background.
/// </summary>
public class RunContextExtractor
{
    private readonly RunContext _context;
    private readonly ILlmClient? _client;

    public RunContextExtractor(RunContext context, ILlmClient? client)
    {
        _context = context;
        _client = client;
    }

    /// <summary>
    /// Update context after a combat ends. Extracts archetype, gaps, goals from reflection.
    /// </summary>
    public void AfterCombat(BattleReflection? reflection, BattleState? finalState)
    {
        _context.CombatsCompleted++;
        UpdateFloorInfo();

        if (reflection == null && finalState == null) return;

        // Synchronous updates from reflection data (no LLM needed)
        if (reflection != null)
        {
            if (!string.IsNullOrEmpty(reflection.DeckGaps))
            {
                var gaps = reflection.DeckGaps.Split(',', ';')
                    .Select(g => g.Trim()).Where(g => g.Length > 0).ToList();
                if (gaps.Count > 0) _context.SetGaps(gaps);
            }

            if (!string.IsNullOrEmpty(reflection.StrategyUpdate))
                _context.StrategyNote = reflection.StrategyUpdate;
        }

        // LLM-powered extraction in background
        if (_client != null)
        {
            var deckSummary = CollectDeckSummary();
            _ = Task.Run(async () =>
            {
                try { await ExtractCombatContext(reflection, deckSummary); }
                catch (Exception ex) { Log.Warn($"[RunContext] Combat extraction failed: {ex.Message}"); }
            });
        }
    }

    /// <summary>
    /// Update context after a non-combat session ends.
    /// Extracts key decisions from the conversation log.
    /// </summary>
    public void AfterNonCombat(string conversationLog)
    {
        UpdateFloorInfo();

        if (_client == null || string.IsNullOrEmpty(conversationLog) || conversationLog == "[]")
            return;

        _ = Task.Run(async () =>
        {
            try { await ExtractNonCombatContext(conversationLog); }
            catch (Exception ex) { Log.Warn($"[RunContext] Non-combat extraction failed: {ex.Message}"); }
        });
    }

    /// <summary>Update floor/act from game state.</summary>
    public void UpdateFloorInfo()
    {
        try
        {
            var runState = RunManager.Instance?.DebugOnlyGetState();
            if (runState != null)
            {
                _context.Floor = runState.TotalFloor;
                _context.Act = runState.CurrentActIndex + 1;
            }
        }
        catch { }
    }

    #region LLM Extraction

    private async Task ExtractCombatContext(BattleReflection? reflection, string deckSummary)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine("Update the run context after a combat. Return JSON.");
        prompt.AppendLine();
        prompt.AppendLine($"Current archetype: {(_context.Archetype.Length > 0 ? _context.Archetype : "not yet identified")}");
        prompt.AppendLine($"Current strategy: {(_context.StrategyNote.Length > 0 ? _context.StrategyNote : "none")}");
        prompt.AppendLine($"Current goals: {(string.Join(", ", _context.CurrentGoals))}");
        prompt.AppendLine();
        prompt.AppendLine($"Deck: {deckSummary}");

        if (reflection != null)
        {
            prompt.AppendLine($"Combat result: {reflection.Outcome} vs {reflection.Enemies}");
            prompt.AppendLine($"HP loss analysis: {reflection.HpLossAnalysis}");
            prompt.AppendLine($"Deck gaps: {reflection.DeckGaps}");
            prompt.AppendLine($"Strategy update: {reflection.StrategyUpdate}");
        }

        prompt.AppendLine();
        prompt.AppendLine("Return JSON with:");
        prompt.AppendLine("- archetype: string (deck archetype name, e.g. 'Strength Build', 'Block Engine')");
        prompt.AppendLine("- goals: string[] (1-3 current strategic goals)");
        prompt.AppendLine("- strategy_note: string (one-line strategy summary)");

        await CallAndApply(prompt.ToString());
    }

    private async Task ExtractNonCombatContext(string conversationLog)
    {
        // Truncate conversation log to avoid huge prompts
        var truncated = conversationLog.Length > 3000
            ? conversationLog[^3000..] : conversationLog;

        var prompt = new StringBuilder();
        prompt.AppendLine("Extract key decisions from this non-combat session. Return JSON.");
        prompt.AppendLine();
        prompt.AppendLine($"Current archetype: {_context.Archetype}");
        prompt.AppendLine($"Floor: {_context.Floor}, Act: {_context.Act}");
        prompt.AppendLine();
        prompt.AppendLine("Recent conversation (agent's decisions):");
        prompt.AppendLine(truncated);
        prompt.AppendLine();
        prompt.AppendLine("Return JSON with:");
        prompt.AppendLine("- decisions: string[] (1-3 key decisions with reasoning, e.g. 'F12: Chose Inflame over Cleave — building Strength archetype')");
        prompt.AppendLine("- goals_update: string[] (updated goals, or empty to keep current)");

        await CallAndApply(prompt.ToString());
    }

    private async Task CallAndApply(string prompt)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var response = await _client!.CompleteAsync(
            "You are a context extraction agent. Return ONLY valid JSON, no markdown fences.",
            prompt, cts.Token);

        var json = ExtractJson(response);
        if (json == null) return;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Apply archetype
        if (root.TryGetProperty("archetype", out var arch))
        {
            var val = arch.GetString();
            if (!string.IsNullOrEmpty(val))
            {
                _context.Archetype = val;
                Log.Info($"[RunContext] Archetype: {val}");
            }
        }

        // Apply goals
        if (root.TryGetProperty("goals", out var goals) && goals.ValueKind == JsonValueKind.Array)
        {
            var list = goals.EnumerateArray()
                .Select(g => g.GetString() ?? "").Where(g => g.Length > 0).ToList();
            if (list.Count > 0) _context.SetGoals(list);
        }

        if (root.TryGetProperty("goals_update", out var goalsUp) && goalsUp.ValueKind == JsonValueKind.Array)
        {
            var list = goalsUp.EnumerateArray()
                .Select(g => g.GetString() ?? "").Where(g => g.Length > 0).ToList();
            if (list.Count > 0) _context.SetGoals(list);
        }

        // Apply strategy note
        if (root.TryGetProperty("strategy_note", out var strat))
        {
            var val = strat.GetString();
            if (!string.IsNullOrEmpty(val))
                _context.StrategyNote = val;
        }

        // Apply decisions
        if (root.TryGetProperty("decisions", out var decs) && decs.ValueKind == JsonValueKind.Array)
        {
            foreach (var d in decs.EnumerateArray())
            {
                var val = d.GetString();
                if (!string.IsNullOrEmpty(val))
                    _context.AddDecision($"F{_context.Floor}: {val}");
            }
        }
    }

    #endregion

    private static string CollectDeckSummary()
    {
        try
        {
            var runState = RunManager.Instance?.DebugOnlyGetState();
            var player = runState?.Players.FirstOrDefault();
            var pcs = player?.PlayerCombatState;
            if (pcs == null) return "unavailable";

            var cards = pcs.AllCards.Select(c =>
            {
                var name = c.Title?.ToString() ?? c.GetType().Name;
                var up = c.IsUpgraded ? "+" : "";
                return $"{name}{up}";
            });
            return string.Join(", ", cards);
        }
        catch { return "unavailable"; }
    }

    private static string? ExtractJson(string response) => Agent.JsonUtils.ExtractJson(response);
}
