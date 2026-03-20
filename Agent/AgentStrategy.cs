using System.Text;
using System.Text.Json;
using AutoPlayMod.Core;
using AutoPlayMod.Memory;
using MegaCrit.Sts2.Core.Logging;

namespace AutoPlayMod.Agent;

/// <summary>
/// Mode 3: Fully Agent. Uses UnifiedGameAgent for multi-turn conversation.
/// Handles combat-specific logic: retry, fallback, error tracking.
/// Delegates all conversation management to UnifiedGameAgent.
/// </summary>
public class AgentStrategy : IPlayStrategy
{
    public string Name => $"FullyAgent ({_client.ProviderName})";

    private readonly ILlmClient _client;
    private readonly UnifiedGameAgent _agent;
    private readonly int _timeoutMs;
    private readonly string _logDir;

    /// <summary>Run context injected into first message of each combat.</summary>
    public Core.RunContext? RunContext { get; set; }
    public BattleJournal? Journal
    {
        get => _agent.Journal;
        set => _agent.Journal = value;
    }

    private bool _combatStarted;
    private bool _relicsSentThisCombat;
    private int _combatCount;
    private string? _pendingActionError;
    private bool _fallbackMode;
    private int _lastTurnNumber;
    private IPlayStrategy? _fallbackStrategy;
    private int _consecutiveErrors;

    private const int MaxRetries = 3;
    private const int MaxConsecutiveErrorsBeforeFallback = 5;

    public AgentStrategy(ILlmClient client, UnifiedGameAgent agent, int timeoutMs = 30000, string? logDir = null)
    {
        _client = client;
        _agent = agent;
        _timeoutMs = timeoutMs;
        _logDir = logDir ?? "";
        // SimpleStrategy as fallback when API is unavailable
        _fallbackStrategy = new Scripting.SimpleStrategy();
    }

    public async Task<CombatAction> DecideAction(BattleState state)
    {
        // Track combat start — new conversation thread
        if (!_combatStarted)
        {
            _combatStarted = true;
            _relicsSentThisCombat = false;
            _fallbackMode = false;
            _consecutiveErrors = 0;
            _combatCount++;
            _agent.Journal?.OnCombatStart(state);
            _agent.StartCombatSession();
        }

        // Reset fallback on new turn — give the agent another chance
        if (state.Round != _lastTurnNumber)
        {
            _lastTurnNumber = state.Round;
            if (_fallbackMode)
            {
                _fallbackMode = false;
                Log.Info("[AutoPlay/Agent] New turn — resetting fallback, trying agent again");
            }
        }

        // If in fallback mode (timeout this turn), use script strategy
        if (_fallbackMode)
        {
            return await _fallbackStrategy!.DecideAction(state);
        }

        return await DecideWithRetry(state);
    }

    private async Task<CombatAction> DecideWithRetry(BattleState state)
    {
        Exception? lastError = null;

        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            if (attempt > 0)
            {
                var delayMs = attempt * 2000;
                Log.Info($"[AutoPlay/Agent] Retry #{attempt} after {delayMs}ms...");
                await Task.Delay(delayMs);
            }

            try
            {
                using var cts = new CancellationTokenSource(_timeoutMs);
                var userMessage = BuildUserMessage(state);

                // If previous action failed, prepend error context
                if (_pendingActionError != null)
                {
                    userMessage = $"[TOOL ERROR] {_pendingActionError}\n\nUpdated state:\n{userMessage}";
                    _pendingActionError = null;
                }

                CombatAction action;
                if (_agent != null)
                {
                    action = await DecideWithAgent(userMessage, state, cts.Token);
                }
                else
                {
                    var response = await _client.CompleteAsync(
                        Prompts.CombatSystem, userMessage, cts.Token);
                    action = ParseTextAction(response);
                }

                _consecutiveErrors = 0;
                return action;
            }
            catch (HttpRequestException httpEx)
            {
                lastError = httpEx;
                var errorCategory = ClassifyHttpError(httpEx);

                switch (errorCategory)
                {
                    case ApiErrorCategory.Auth:
                        Log.Error($"[AutoPlay/Agent] Auth error (will not retry): {httpEx.Message}");
                        ActivateFallback("Authentication failed. Check your API key.");
                        return await _fallbackStrategy!.DecideAction(state);

                    case ApiErrorCategory.RateLimit:
                        Log.Warn($"[AutoPlay/Agent] Rate limited, waiting before retry...");
                        await Task.Delay(5000 + attempt * 5000);
                        continue;

                    case ApiErrorCategory.ServerError:
                        Log.Warn($"[AutoPlay/Agent] Server error (attempt {attempt + 1}/{MaxRetries + 1}): {httpEx.Message}");
                        continue;

                    case ApiErrorCategory.RequestError:
                        Log.Error($"[AutoPlay/Agent] Request error: {httpEx.Message}");
                        _consecutiveErrors++;
                        if (_consecutiveErrors >= MaxConsecutiveErrorsBeforeFallback)
                            ActivateFallback("Too many consecutive request errors.");
                        // Use fallback strategy instead of EndTurn — don't waste a turn
                        Log.Info("[AutoPlay/Agent] Using fallback strategy for this action");
                        return await _fallbackStrategy!.DecideAction(state);

                    default:
                        Log.Warn($"[AutoPlay/Agent] HTTP error (attempt {attempt + 1}): {httpEx.Message}");
                        continue;
                }
            }
            catch (OperationCanceledException)
            {
                lastError = new TimeoutException("Request timed out");
                Log.Warn($"[AutoPlay/Agent] Timeout (attempt {attempt + 1}/{MaxRetries + 1})");
                continue;
            }
            catch (Exception ex)
            {
                lastError = ex;
                Log.Error($"[AutoPlay/Agent] Unexpected error: {ex.Message}");
                break;
            }
        }

        _consecutiveErrors++;
        if (_consecutiveErrors >= MaxConsecutiveErrorsBeforeFallback)
        {
            ActivateFallback($"All retries exhausted: {lastError?.Message}");
            return await _fallbackStrategy!.DecideAction(state);
        }

        Log.Warn($"[AutoPlay/Agent] All retries failed, using fallback: {lastError?.Message}");
        return await _fallbackStrategy!.DecideAction(state);
    }

    private async Task<CombatAction> DecideWithAgent(
        string userMessage, BattleState state, CancellationToken ct)
    {
        var result = await _agent.RunAgentLoop(
            userMessage, ToolDefinitions.CombatActionTools, Prompts.CombatSystem, ct);

        if (!result.HasValue)
        {
            Log.Warn("[AutoPlay/Agent] No action tool returned (timeout), switching to fallback for rest of turn");
            _agent.Journal?.RecordAction("[TIMEOUT] Agent timed out, switching to fallback script for remaining actions");
            _fallbackMode = true;
            return await _fallbackStrategy!.DecideAction(state);
        }

        var (toolName, input) = result.Value;
        CombatAction action;
        try
        {
            action = toolName switch
            {
                "play_card" => CombatAction.PlayCard(
                    input.GetProperty("card_index").GetInt32(),
                    input.TryGetProperty("target_index", out var ti) ? ti.GetInt32() : null),
                "use_potion" => CombatAction.UsePotion(
                    input.GetProperty("potion_index").GetInt32(),
                    input.TryGetProperty("target_index", out var ti2) ? ti2.GetInt32() : null),
                "end_turn" => CombatAction.EndTurn(),
                _ => throw new InvalidOperationException($"Unknown combat tool: {toolName}")
            };
        }
        catch (Exception ex)
        {
            Log.Warn($"[AutoPlay/Agent] Failed to parse tool call '{toolName}': {ex.Message}, using fallback");
            return await _fallbackStrategy!.DecideAction(state);
        }

        _agent.Journal?.RecordAction(action, state);
        return action;
    }

    private void ActivateFallback(string reason)
    {
        _fallbackMode = true;
        Log.Warn($"[AutoPlay/Agent] Switching to fallback script for rest of combat. Reason: {reason}");
    }

    private static ApiErrorCategory ClassifyHttpError(HttpRequestException ex)
    {
        var msg = ex.Message;
        if (msg.Contains("401") || msg.Contains("403") || msg.Contains("Unauthorized") || msg.Contains("Forbidden"))
            return ApiErrorCategory.Auth;
        if (msg.Contains("429") || msg.Contains("rate") || msg.Contains("Rate"))
            return ApiErrorCategory.RateLimit;
        if (msg.Contains("500") || msg.Contains("502") || msg.Contains("503") || msg.Contains("overloaded"))
            return ApiErrorCategory.ServerError;
        if (msg.Contains("400") || msg.Contains("404") || msg.Contains("NotFound") || msg.Contains("BadRequest") || msg.Contains("invalid_request"))
            return ApiErrorCategory.RequestError;
        return ApiErrorCategory.Unknown;
    }

    private enum ApiErrorCategory { Auth, RateLimit, ServerError, RequestError, Unknown }

    private string BuildUserMessage(BattleState state)
    {
        var sb = new StringBuilder();

        bool includeRelics = !_relicsSentThisCombat;
        sb.Append(Prompts.FormatStateForAgent(state, includeRelics: includeRelics));

        if (includeRelics)
        {
            _relicsSentThisCombat = true;

            // Inject enemy memories (auto-injection)
            var memory = _agent.Memory;
            if (memory != null)
            {
                var enemyNames = state.Enemies.Where(e => e.IsAlive).Select(e => e.Name).Distinct();
                var enemyMemories = memory.GetForInjection("enemy", enemyNames, 300);
                if (!string.IsNullOrEmpty(enemyMemories))
                    sb.Append($"\n\n=== ENEMY KNOWLEDGE (from memory) ===\n{enemyMemories}");

                // Inject strategy
                var (charStrat, genStrat) = memory.ReadStrategy();
                var stratParts = new List<string>();
                if (charStrat?.Observations.Count > 0)
                    stratParts.AddRange(charStrat.Observations.TakeLast(5));
                if (genStrat?.Observations.Count > 0)
                    stratParts.AddRange(genStrat.Observations.TakeLast(3));
                if (stratParts.Count > 0)
                    sb.Append($"\n\n=== STRATEGY NOTES ===\n{string.Join("\n", stratParts.Select(s => $"- {s}"))}");
            }

            var reflectionCtx = _agent.Journal?.GetReflectionContext(3) ?? "";
            if (!string.IsNullOrEmpty(reflectionCtx))
                sb.Append($"\n\nPrevious battle lessons:\n{reflectionCtx}");

            // Inject run context (archetype, goals, recent decisions)
            var runCtx = RunContext?.ToInjectionString() ?? "";
            if (!string.IsNullOrEmpty(runCtx))
                sb.Append($"\n\n{runCtx}");
        }

        return sb.ToString();
    }

    public async Task OnCombatEnd(BattleState finalState, bool victory, int remainingHp)
    {
        _combatStarted = false;
        _fallbackMode = false;
        _consecutiveErrors = 0;

        var outcome = victory ? "win" : "loss";
        await _agent.EndCombatSession(outcome);

        if (_agent.Journal != null)
        {
            await _agent.Journal.OnCombatEnd(finalState, victory);
        }
    }

    /// <summary>
    /// Called by AutoPlayer when an action fails to execute.
    /// Feeds error back into the conversation so AI can adjust.
    /// </summary>
    public void NotifyActionFailed(CombatAction failedAction, string reason = "")
    {
        var msg = string.IsNullOrEmpty(reason)
            ? $"Action failed: {failedAction}. The card/potion index may be invalid or the target may be wrong."
            : $"Action failed: {failedAction}. Reason: {reason}";
        _pendingActionError = msg;
    }

    /// <summary>
    /// Fallback: parse a text response (for non-Claude providers).
    /// </summary>
    private static CombatAction ParseTextAction(string response)
    {
        Log.Info($"[AutoPlay/Agent] Text response: {response[..Math.Min(response.Length, 200)]}");

        var json = ExtractJson(response);
        if (json == null)
        {
            Log.Warn("[AutoPlay/Agent] No JSON found in response");
            return CombatAction.EndTurn();
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString() ?? "end_turn";

            return type switch
            {
                "play_card" => CombatAction.PlayCard(
                    root.GetProperty("card_index").GetInt32(),
                    root.TryGetProperty("target_index", out var ti) ? ti.GetInt32() : null),
                "use_potion" => CombatAction.UsePotion(
                    root.GetProperty("potion_index").GetInt32(),
                    root.TryGetProperty("target_index", out var ti2) ? ti2.GetInt32() : null),
                "end_turn" => CombatAction.EndTurn(),
                _ => CombatAction.EndTurn()
            };
        }
        catch (Exception ex)
        {
            Log.Warn($"[AutoPlay/Agent] Parse failed: {ex.Message}");
            return CombatAction.EndTurn();
        }
    }

    private static string? ExtractJson(string response)
    {
        var text = response.Trim();
        if (text.StartsWith("{"))
            return ExtractJsonObject(text, 0);

        var codeStart = text.IndexOf("```json", StringComparison.OrdinalIgnoreCase);
        if (codeStart >= 0)
        {
            var afterFence = text.IndexOf('\n', codeStart);
            if (afterFence >= 0)
            {
                var blockEnd = text.IndexOf("```", afterFence + 1, StringComparison.Ordinal);
                if (blockEnd >= 0)
                    return text[(afterFence + 1)..blockEnd].Trim();
            }
        }

        var braceIdx = text.IndexOf('{');
        return braceIdx >= 0 ? ExtractJsonObject(text, braceIdx) : null;
    }

    private static string? ExtractJsonObject(string text, int startIdx)
    {
        int depth = 0;
        for (int i = startIdx; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}') depth--;
            if (depth == 0) return text[startIdx..(i + 1)];
        }
        return text[startIdx..];
    }
}
