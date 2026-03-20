using System.Text;
using System.Text.Json;
using AutoPlayMod.Agent;
using AutoPlayMod.Agent;
using MegaCrit.Sts2.Core.Logging;

namespace AutoPlayMod.Memory;

/// <summary>
/// Runs LLM-powered memory extraction in the background after combat/events/runs.
/// Fire-and-forget — does not block the game thread.
/// </summary>
public class BackgroundMemoryWriter
{
    private readonly MemoryStore _store;
    private readonly ILlmClient _client;
    private int _activeTasks;
    private const int MaxConcurrent = 3;

    public BackgroundMemoryWriter(MemoryStore store, ILlmClient client)
    {
        _store = store;
        _client = client;
    }

    /// <summary>
    /// Process combat end: extract card/enemy observations and write to memory.
    /// Runs in background, non-blocking.
    /// </summary>
    public void ProcessCombatEnd(BattleReflection? reflection, List<string> enemyNames, List<string> cardsUsed)
    {
        if (reflection == null) return;
        if (Interlocked.Increment(ref _activeTasks) > MaxConcurrent)
        {
            Interlocked.Decrement(ref _activeTasks);
            Log.Info("[Memory/BG] Skipping — too many background tasks");
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await ExtractCombatMemories(reflection, enemyNames, cardsUsed);
            }
            catch (Exception ex)
            {
                Log.Warn($"[Memory/BG] Combat memory extraction failed: {ex.Message}");
            }
            finally
            {
                Interlocked.Decrement(ref _activeTasks);
            }
        });
    }

    /// <summary>
    /// Process event end: save event choice and outcome.
    /// </summary>
    public void ProcessEventEnd(string eventName, string chosenOption, string outcome)
    {
        if (string.IsNullOrEmpty(eventName)) return;

        _ = Task.Run(() =>
        {
            try
            {
                var observation = $"Chose: {chosenOption}. Result: {outcome}";
                _store.Write("event", eventName, observation);
                Log.Info($"[Memory/BG] Saved event memory: {eventName}");
            }
            catch (Exception ex)
            {
                Log.Warn($"[Memory/BG] Event memory save failed: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Process run end: generate run summary and update strategy.
    /// </summary>
    public void ProcessRunEnd(string character, string archetype, string result,
        int finalFloor, string causeOfEnd, List<string> keyDecisions)
    {
        if (Interlocked.Increment(ref _activeTasks) > MaxConcurrent)
        {
            Interlocked.Decrement(ref _activeTasks);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var runId = _store.GetNextRunId();
                var run = new RunEntry
                {
                    RunId = runId,
                    Character = character,
                    Archetype = archetype,
                    Result = result,
                    FinalFloor = finalFloor,
                    CauseOfEnd = causeOfEnd,
                    KeyDecisions = keyDecisions,
                    Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                };

                // Ask LLM to extract lessons from the run
                var lessons = await ExtractRunLessons(run);
                run.Lessons = lessons;

                _store.SaveRun(run);
                Log.Info($"[Memory/BG] Run #{runId} saved ({result}, floor {finalFloor})");

                // Update strategy based on run outcome
                if (lessons.Count > 0)
                {
                    _store.AppendStrategy("character", $"Run #{runId} ({result}): {lessons[0]}");
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"[Memory/BG] Run memory save failed: {ex.Message}");
            }
            finally
            {
                Interlocked.Decrement(ref _activeTasks);
            }
        });
    }

    /// <summary>
    /// Merge an entity's observations if they exceed the threshold.
    /// </summary>
    public void MergeIfNeeded(string category, string name)
    {
        if (!_store.NeedsMerge(category, name)) return;

        _ = Task.Run(async () =>
        {
            try
            {
                await MergeObservations(category, name);
            }
            catch (Exception ex)
            {
                Log.Warn($"[Memory/BG] Merge failed for {category}/{name}: {ex.Message}");
            }
        });
    }

    #region LLM-powered extraction

    private async Task ExtractCombatMemories(BattleReflection reflection, List<string> enemyNames, List<string> cardsUsed)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine("Extract structured memory observations from this combat reflection.");
        prompt.AppendLine();
        prompt.AppendLine("EXACT enemy names (use these as JSON keys, do NOT rename):");
        foreach (var name in enemyNames)
            prompt.AppendLine($"  - {name}");
        prompt.AppendLine();
        prompt.AppendLine("EXACT card names used in this combat (use these as JSON keys, do NOT rename):");
        foreach (var name in cardsUsed)
            prompt.AppendLine($"  - {name}");
        prompt.AppendLine();
        prompt.AppendLine($"Outcome: {reflection.Outcome} | HP: {reflection.RemainingHp}/{reflection.MaxHp}");
        prompt.AppendLine($"HP Loss Analysis: {reflection.HpLossAnalysis}");
        prompt.AppendLine($"Deck Gaps: {reflection.DeckGaps}");
        prompt.AppendLine($"Key Mistakes: {string.Join("; ", reflection.KeyMistakes)}");
        prompt.AppendLine($"Strategy Update: {reflection.StrategyUpdate}");
        prompt.AppendLine();
        prompt.AppendLine("Return a JSON object with:");
        prompt.AppendLine("- enemy_observations: use EXACT enemy names above as keys, e.g. { \"CeremonialBeast\": \"observation\" }");
        prompt.AppendLine("- card_observations: use EXACT card names above as keys, e.g. { \"Bash\": { \"observation\": \"...\", \"rating\": 1-5, \"synergies\": [...] } }");
        prompt.AppendLine("- strategy_note: one GENERAL reusable insight (or empty string if none)");
        prompt.AppendLine("  GOOD: 'Apply Vulnerable before heavy attacks for 50% more damage'");
        prompt.AppendLine("  BAD: 'At Floor 5 with 36 HP, chose Inflame' (run-specific, not general)");
        prompt.AppendLine("IMPORTANT: Use the EXACT names listed above. Do NOT invent names like 'card1' or 'unknown'.");

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var response = await _client.CompleteAsync(
                "You are a memory extraction agent. Return ONLY valid JSON, no markdown fences.",
                prompt.ToString(), cts.Token);

            // Parse and save
            var json = ExtractJsonFromResponse(response);
            if (json == null) return;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Save enemy observations
            if (root.TryGetProperty("enemy_observations", out var enemies))
            {
                foreach (var prop in enemies.EnumerateObject())
                {
                    var obs = prop.Value.GetString();
                    if (!string.IsNullOrEmpty(obs))
                    {
                        _store.Write("enemy", prop.Name, obs);
                        MergeIfNeeded("enemy", prop.Name);
                    }
                }
            }

            // Save card observations
            if (root.TryGetProperty("card_observations", out var cards))
            {
                foreach (var prop in cards.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Object)
                    {
                        var obs = prop.Value.TryGetProperty("observation", out var o) ? o.GetString() : null;
                        var rating = prop.Value.TryGetProperty("rating", out var r) ? (int?)r.GetInt32() : null;
                        List<string>? synergies = null;
                        if (prop.Value.TryGetProperty("synergies", out var s))
                            synergies = s.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList();

                        if (!string.IsNullOrEmpty(obs))
                        {
                            _store.Write("card", prop.Name, obs, rating, synergies);
                            MergeIfNeeded("card", prop.Name);
                        }
                    }
                    else if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var obs = prop.Value.GetString();
                        if (!string.IsNullOrEmpty(obs))
                            _store.Write("card", prop.Name, obs);
                    }
                }
            }

            // Save strategy note
            if (root.TryGetProperty("strategy_note", out var strat))
            {
                var note = strat.GetString();
                if (!string.IsNullOrEmpty(note))
                    _store.AppendStrategy("character", note);
            }

            Log.Info($"[Memory/BG] Combat memories extracted for {enemyNames.Count} enemies, {cardsUsed.Distinct().Count()} cards");
        }
        catch (Exception ex)
        {
            Log.Warn($"[Memory/BG] LLM extraction error: {ex.Message}");
        }
    }

    private async Task<List<string>> ExtractRunLessons(RunEntry run)
    {
        var prompt = $"""
            Extract 2-3 key lessons from this completed run:
            Character: {run.Character} | Archetype: {run.Archetype}
            Result: {run.Result} | Floor: {run.FinalFloor}
            Cause of end: {run.CauseOfEnd}
            Key decisions: {string.Join("; ", run.KeyDecisions.TakeLast(5))}

            Return a JSON array of strings, e.g. ["lesson1", "lesson2"]. Be concise and actionable.
            """;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var response = await _client.CompleteAsync(
                "Return ONLY a JSON array of lesson strings.",
                prompt, cts.Token);

            var json = ExtractJsonFromResponse(response);
            if (json != null)
            {
                var lessons = JsonSerializer.Deserialize<List<string>>(json);
                return lessons ?? [];
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[Memory/BG] Run lesson extraction failed: {ex.Message}");
        }
        return [];
    }

    private async Task MergeObservations(string category, string name)
    {
        var entry = _store.Read(category, name);
        if (entry == null || entry.Observations.Count < 8) return;

        var prompt = $"""
            Merge these {entry.Observations.Count} observations about {category} "{entry.Name}" into ~5 high-quality insights.

            Current observations:
            {string.Join("\n", entry.Observations.Select((o, i) => $"{i + 1}. {o}"))}

            Current synergies: {string.Join(", ", entry.Synergies)}
            Current anti-synergies: {string.Join(", ", entry.AntiSynergies)}

            Rules:
            - Remove redundancies, keep the most informative version
            - When observations CONTRADICT, produce a CONDITIONAL conclusion (e.g. "Strong in X builds, weak in Y situations")
            - Include WHY in each observation
            - Update synergies and anti_synergies lists
            - Provide an updated rating (1-5)

            Return JSON with keys: "observations" (array), "synergies" (array), "anti_synergies" (array), "rating" (number 1-5)
            """;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var response = await _client.CompleteAsync(
                "You are a memory merge agent. Return ONLY valid JSON.",
                prompt, cts.Token);

            var json = ExtractJsonFromResponse(response);
            if (json == null) return;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var observations = root.TryGetProperty("observations", out var obs)
                ? obs.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
                : entry.Observations;
            var synergies = root.TryGetProperty("synergies", out var syn)
                ? syn.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
                : null;
            var antiSynergies = root.TryGetProperty("anti_synergies", out var asyn)
                ? asyn.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
                : null;
            var rating = root.TryGetProperty("rating", out var r) ? (int?)r.GetInt32() : null;

            _store.ReplaceObservations(category, name, observations, synergies, antiSynergies, rating);
            Log.Info($"[Memory/BG] Merged {entry.Observations.Count} → {observations.Count} observations for {category}/{name}");
        }
        catch (Exception ex)
        {
            Log.Warn($"[Memory/BG] Merge LLM error: {ex.Message}");
        }
    }

    #endregion

    private static string? ExtractJsonFromResponse(string response) => JsonUtils.ExtractJson(response);
}
