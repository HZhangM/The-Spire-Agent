using AutoPlayMod.Core;
using AutoPlayMod.Scripting;
using MegaCrit.Sts2.Core.Logging;

namespace AutoPlayMod.Agent;

/// <summary>
/// Mode 2: Agentic Script. Uses a Lua script for combat decisions,
/// but after each combat, sends the results to an LLM to improve the script.
/// The LLM rewrites the Lua, which is hot-reloaded for the next battle.
/// </summary>
public class AgenticScriptStrategy : IPlayStrategy
{
    public string Name => $"AgenticScript ({_client.ProviderName})";

    private readonly LuaStrategy _luaStrategy;
    private readonly ILlmClient _client;
    private readonly string? _scriptSavePath;

    // Track battle state for post-combat analysis
    private int _roundCount;
    private string _enemyNames = "";

    public AgenticScriptStrategy(LuaStrategy luaStrategy, ILlmClient client, string? scriptSavePath = null)
    {
        _luaStrategy = luaStrategy;
        _client = client;
        _scriptSavePath = scriptSavePath;
    }

    public async Task<CombatAction> DecideAction(BattleState state)
    {
        // Track info for post-combat analysis
        _roundCount = state.Round;
        if (string.IsNullOrEmpty(_enemyNames))
        {
            _enemyNames = string.Join(", ", state.Enemies.Select(e => $"{e.Name}(HP:{e.MaxHp})"));
        }

        // Delegate to the Lua script
        return await _luaStrategy.DecideAction(state);
    }

    public async Task OnCombatEnd(BattleState finalState, bool victory, int remainingHp)
    {
        // Notify the Lua script too
        await _luaStrategy.OnCombatEnd(finalState, victory, remainingHp);

        // Now ask the LLM to improve the script
        try
        {
            await ImproveScriptAsync(finalState, victory, remainingHp);
        }
        catch (Exception ex)
        {
            Log.Error($"[AutoPlay/Agentic] Script improvement failed: {ex.Message}");
        }
        finally
        {
            _enemyNames = "";
        }
    }

    private async Task ImproveScriptAsync(BattleState finalState, bool victory, int remainingHp)
    {
        var currentScript = _luaStrategy.GetCurrentSource();

        var battleSummary = BuildBattleSummary(finalState, victory);

        var userPrompt = Prompts.FormatAgenticScriptUser(
            currentScript: currentScript,
            victory: victory,
            remainingHp: remainingHp,
            maxHp: finalState.Player.MaxHp,
            rounds: _roundCount,
            enemies: _enemyNames,
            battleSummary: battleSummary);

        Log.Info("[AutoPlay/Agentic] Requesting script improvement from LLM...");

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var response = await _client.CompleteAsync(
            Prompts.AgenticScriptSystem,
            userPrompt,
            cts.Token);

        // Extract Lua code from response
        var newScript = ExtractLuaCode(response);
        if (string.IsNullOrWhiteSpace(newScript))
        {
            Log.Warn("[AutoPlay/Agentic] No valid Lua code in LLM response, keeping current script");
            return;
        }

        // Try to hot-reload the new script
        try
        {
            _luaStrategy.ReloadScript(newScript);
            Log.Info("[AutoPlay/Agentic] Script improved and hot-reloaded!");

            // Save to disk if path is configured
            if (!string.IsNullOrEmpty(_scriptSavePath))
            {
                var dir = Path.GetDirectoryName(_scriptSavePath);
                if (dir != null) Directory.CreateDirectory(dir);

                // Save backup of old script
                var backupPath = _scriptSavePath + $".backup_{DateTime.Now:yyyyMMdd_HHmmss}";
                File.WriteAllText(backupPath, currentScript);

                // Save new script
                File.WriteAllText(_scriptSavePath, newScript);
                Log.Info($"[AutoPlay/Agentic] Script saved to: {_scriptSavePath}");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[AutoPlay/Agentic] New script failed to load: {ex.Message}, keeping current script");
        }
    }

    private static string BuildBattleSummary(BattleState finalState, bool victory)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Player HP: {finalState.Player.Hp}/{finalState.Player.MaxHp}, Block: {finalState.Player.Block}");

        if (finalState.Player.Powers.Count > 0)
        {
            sb.Append("Player powers: ");
            sb.AppendLine(string.Join(", ", finalState.Player.Powers.Select(p => $"{p.Name}x{p.Amount}")));
        }

        foreach (var enemy in finalState.Enemies)
        {
            sb.AppendLine($"Enemy '{enemy.Name}': HP={enemy.Hp}/{enemy.MaxHp}, Alive={enemy.IsAlive}");
        }

        sb.AppendLine($"Cards in hand: {finalState.Hand.Count}");
        sb.AppendLine($"Draw pile: {finalState.DrawPileCount}, Discard: {finalState.DiscardPileCount}, Exhaust: {finalState.ExhaustPileCount}");

        if (finalState.Relics.Count > 0)
        {
            sb.Append("Relics: ");
            sb.AppendLine(string.Join(", ", finalState.Relics.Select(r => r.Name)));
        }

        return sb.ToString();
    }

    private static string ExtractLuaCode(string response)
    {
        // Look for ```lua ... ``` code block
        const string luaStart = "```lua";
        const string blockEnd = "```";

        var startIdx = response.IndexOf(luaStart, StringComparison.OrdinalIgnoreCase);
        if (startIdx < 0)
        {
            // Try generic code block
            startIdx = response.IndexOf("```\n", StringComparison.Ordinal);
            if (startIdx < 0) return "";
            startIdx += 4;
        }
        else
        {
            startIdx += luaStart.Length;
            // Skip to next line
            var newline = response.IndexOf('\n', startIdx);
            if (newline >= 0) startIdx = newline + 1;
        }

        var endIdx = response.IndexOf(blockEnd, startIdx, StringComparison.Ordinal);
        if (endIdx < 0) return "";

        return response[startIdx..endIdx].Trim();
    }
}
