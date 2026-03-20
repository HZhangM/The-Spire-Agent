using System.Text.Json;
using AutoPlayMod.Core;
using AutoPlayMod.Memory;
using MegaCrit.Sts2.Core.Logging;

namespace AutoPlayMod.Agent;

/// <summary>
/// Unified non-combat advisor using UnifiedGameAgent for multi-turn conversations.
/// Each decision calls RunAgentLoop which handles query tools internally.
/// The non-combat session persists across reward -> map -> event -> rest decisions
/// so the AI remembers its previous choices.
/// </summary>
public class GameAgentAdvisor : INonCombatAdvisor
{
    private readonly ILlmClient _client;
    private readonly UnifiedGameAgent? _agent;
    private readonly int _timeoutMs;
    public MemoryStore? Memory { get; set; }
    public Core.RunContext? RunContext { get; set; }

    public GameAgentAdvisor(ILlmClient client, UnifiedGameAgent? agent = null, int timeoutMs = 60000)
    {
        _client = client;
        _agent = agent;
        _timeoutMs = timeoutMs;
    }

    /// <summary>
    /// Start a non-combat session. Called when combat ends to begin
    /// the post-combat flow (rewards, map, events, rest).
    /// </summary>
    public void StartNonCombatSession(string battleSummary = "")
    {
        _agent?.StartNonCombatSession(battleSummary);
    }

    public async Task<int> ChooseMapNode(List<MapNodeInfo> availableNodes, GameSummary summary)
    {
        var stateDesc = FormatSummary(summary);
        var nodesDesc = string.Join("\n", availableNodes.Select((n, i) =>
            $"  [{i}] {n.Type} at column {n.Col}, row {n.Row}"));
        var prompt = $"{stateDesc}\n\nAvailable map nodes:\n{nodesDesc}\n\nChoose which node to travel to.";

        var result = await CallAgent(prompt, ToolDefinitions.MapTools);
        if (result.HasValue)
        {
            var (toolName, input) = result.Value;
            if (input.TryGetProperty("node_index", out var idx))
            {
                var choice = idx.GetInt32();
                if (choice >= 0 && choice < availableNodes.Count)
                    return choice;
            }
        }
        return 0;
    }

    public async Task<int> ChooseEventOption(string eventDescription, List<string> options, GameSummary summary)
    {
        var stateDesc = FormatSummary(summary);
        var optionsDesc = string.Join("\n", options.Select((o, i) => $"  [{i}] {o}"));
        var prompt = $"{stateDesc}\n\nEvent: {eventDescription}\n\nOptions:\n{optionsDesc}\n\nChoose an option.";

        // Auto-inject event memory if available — search by event ID (bracketed prefix)
        if (Memory != null)
        {
            // eventDescription format: "[EVENT.SAPPHIRE_SEED] You narrowly avoid..."
            var eventId = eventDescription.StartsWith("[")
                ? eventDescription[1..eventDescription.IndexOf(']')] : eventDescription;
            var eventMemory = Memory.Read("event", eventId);
            if (eventMemory != null)
                prompt += $"\n\n=== EVENT KNOWLEDGE (from memory) ===\n{eventMemory.ToInjectionString()}";
        }

        var result = await CallAgent(prompt, ToolDefinitions.EventTools);
        if (result.HasValue)
        {
            var (toolName, input) = result.Value;
            if (input.TryGetProperty("option_index", out var idx))
            {
                var choice = idx.GetInt32();
                if (choice >= 0 && choice < options.Count)
                    return choice;
            }
        }
        return 0;
    }

    public async Task<int> ChooseRestSiteOption(List<string> options, GameSummary summary)
    {
        var stateDesc = FormatSummary(summary);
        var optionsDesc = string.Join("\n", options.Select((o, i) => $"  [{i}] {o}"));
        var prompt = $"{stateDesc}\n\nRest site options:\n{optionsDesc}\n\nChoose what to do.";

        var result = await CallAgent(prompt, ToolDefinitions.RestSiteTools);
        if (result.HasValue)
        {
            var (toolName, input) = result.Value;
            if (input.TryGetProperty("option_index", out var idx))
            {
                var choice = idx.GetInt32();
                if (choice >= 0 && choice < options.Count)
                    return choice;
            }
        }
        return 0;
    }

    public async Task<int> ChooseCard(CardSelectionContext context, List<string> cardNames, GameSummary summary)
    {
        var stateDesc = FormatSummary(summary);
        var cardsDesc = string.Join("\n", cardNames.Select((c, i) => $"  [{i}] {c}"));

        var purposeDesc = context.Purpose switch
        {
            CardSelectionPurpose.Reward => """
                Card REWARD: Choose a card to ADD to your deck.
                THINK about: Does this card synergize with my deck's strategy?
                A focused deck (fewer, better cards) beats a bloated deck.
                Skipping is often correct if none of the cards improve your deck.
                Consider: What archetype is my deck building toward? (Strength, Block, Exhaust, etc.)
                """,
            CardSelectionPurpose.UpgradeInHand =>
                "UPGRADE a card in your hand (mid-combat). Pick the one with the biggest upgrade impact.",
            CardSelectionPurpose.UpgradeFromDeck =>
                "UPGRADE a card from your deck. Priorities: key scaling cards > important skills > rarely-played cards last.",
            CardSelectionPurpose.Remove =>
                "REMOVE a card permanently. Thin your deck to draw key cards more often. Remove: Strikes first, then Defends, then situational dead cards.",
            CardSelectionPurpose.Transform =>
                "TRANSFORM a card into a random new one. Transform your worst card.",
            _ => context.Description.Length > 0 ? context.Description : "Choose a card.",
        };

        var skipNote = context.CanSkip ? "\nYou can call skip_card_reward if NONE of the cards improve your deck." : "";
        var prompt = $"{stateDesc}\n\n{purposeDesc}{skipNote}\n\nAvailable cards (with full effects):\n{cardsDesc}";

        // Auto-inject card memories for candidates
        if (Memory != null)
        {
            // Extract card names (strip upgrade markers and cost info)
            var rawNames = cardNames.Select(c => c.Split('(')[0].TrimEnd(' ', '+')).ToList();
            var cardMemories = Memory.GetForInjection("card", rawNames, 300);
            if (!string.IsNullOrEmpty(cardMemories))
                prompt += $"\n\n=== CARD KNOWLEDGE (from memory) ===\n{cardMemories}";
        }

        var result = await CallAgent(prompt, ToolDefinitions.CardSelectionTools);
        if (result.HasValue)
        {
            var (toolName, input) = result.Value;
            if (toolName == "skip_card_reward")
                return -1;

            if (input.TryGetProperty("card_index", out var idx))
            {
                var choice = idx.GetInt32();
                if (choice >= 0 && choice < cardNames.Count)
                    return choice;
            }
        }
        return 0;
    }

    private async Task<(string toolName, JsonElement input)?> CallAgent(string userPrompt, JsonElement[] actionTools)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(_timeoutMs));

            // Multi-turn agent (preferred — has query tools + conversation history)
            if (_agent != null)
            {
                return await _agent.RunAgentLoop(userPrompt, actionTools, Prompts.NonCombatSystem, cts.Token);
            }

            // Single-shot tool call (no agent)
            var result = await _client.CompleteWithToolAsync(
                Prompts.NonCombatSystem, userPrompt, actionTools, cts.Token);
            if (result.HasValue)
            {
                var toolName = result.Value.GetProperty("tool").GetString()!;
                var input = result.Value.GetProperty("input");
                return (toolName, input);
            }
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn($"[AutoPlay/Agent] Advisor call failed: {ex.Message}");
            return null;
        }
    }

    private string FormatSummary(GameSummary summary)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"HP: {summary.Hp}/{summary.MaxHp} | Gold: {summary.Gold} | Floor: {summary.Floor} | Act: {summary.Act}");
        sb.AppendLine($"Deck ({summary.DeckCards.Count} cards): {string.Join(", ", summary.DeckCards.Take(25))}{(summary.DeckCards.Count > 25 ? "..." : "")}");
        sb.AppendLine($"Relics: {(summary.Relics.Count > 0 ? string.Join(", ", summary.Relics) : "none")}");
        sb.AppendLine($"Potions: {(summary.Potions.Count > 0 ? string.Join(", ", summary.Potions) : "none")}");

        // Inject run context
        var runCtx = RunContext?.ToInjectionString() ?? "";
        if (!string.IsNullOrEmpty(runCtx))
            sb.AppendLine($"\n{runCtx}");

        return sb.ToString();
    }
}
