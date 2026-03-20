using System.Linq;

namespace AutoPlayMod.Agent;

public static class Prompts
{
    /// <summary>
    /// System prompt for combat decisions via tool use.
    /// Cached via prompt caching — only the battle state changes per call.
    /// </summary>
    public const string CombatSystem = """
        You are an expert Slay the Spire 2 combat AI. Called ONCE PER ACTION — return exactly ONE tool call.
        After your action executes, you'll be called again with UPDATED state. This loops until you end_turn.

        ███ ABSOLUTE RULE: NEVER call end_turn if energy > 0 AND any card has can_play=true. ███
        If the STATUS section says "You MUST play a card", you are FORBIDDEN from calling end_turn.

        ███ CORE OBJECTIVE: Minimize TOTAL HP lost across the entire combat. ███
        This means:
        - Sometimes you should rush damage to kill enemies fast (fewer turns = less damage taken).
        - Sometimes you should block first to survive a big hit, then counter-attack.
        - Killing an enemy removes its future damage — prioritize targets that deal the most.
        - Applying Vulnerable/Weak early multiplies your efficiency across many turns.

        RULES:
        1. Return EXACTLY ONE tool call per response. No text, just the tool call.
        2. For cards with target_type="AnyEnemy", you MUST provide target_index.
        3. All indices are 0-based.

        READ EACH CARD'S "description" FIELD — it contains the actual effect with resolved numbers.
        Key mechanics:
        - "Vulnerable": target takes 50% more damage for N turns.
        - "Weak": target deals 25% less damage for N turns.
        - "Strength": permanently increases ALL attack damage by N.
        - "Dexterity": permanently increases ALL block gained by N.
        - Power cards: permanent effects that last the entire combat.

        THINK BEFORE EACH ACTION:
        - What is the enemy about to do? (check intent_type and intent_damage)
        - Will I die this turn if I don't block? (incoming vs hp + block)
        - Can I kill an enemy THIS turn to prevent future damage?
        - What synergies exist between my cards? (e.g. apply Vulnerable before big attacks)
        - Should I draw more cards first for better options?
        - Which enemy should I focus down to reduce incoming damage?
        - Adapt to YOUR deck — there is no fixed play order. A Strength deck plays differently from a Block deck.

        POTIONS: Read each potion's "description". Use when they provide the most value —
        damage potions on tough enemies, heal when low, block potions against big hits.
        """;

    /// <summary>
    /// System prompt for post-combat reflection.
    /// </summary>
    public const string ReflectionSystem = """
        You are an expert Slay the Spire 2 analyst reviewing a completed combat.
        Analyze the battle and provide structured insights.

        Return your analysis as a tool call with:
        - performance_rating: 1-5 (1=terrible, 5=perfect)
        - hp_loss_analysis: Why HP was lost — was it because of insufficient offense (fight
          dragged on too long, allowing enemies to attack repeatedly) or insufficient defense
          (not blocking big hits when we should have)? Be specific about which turns.
        - deck_gaps: What the deck is MISSING — categorize needs:
          * Offense (damage, scaling, multi-hit, AOE)
          * Defense (block, weak application, damage mitigation)
          * Economy (energy generation, cost reduction)
          * Draw (card draw, deck thinning, scry)
          List specific card types or effects that would have helped.
        - key_mistakes: list of specific mistakes made
        - key_successes: list of things done well
        - lessons: list of actionable lessons for future combats
        - card_evaluations: which cards performed well/poorly in this fight
        - strategy_update: one-sentence strategy adjustment for future fights
        """;

    /// <summary>
    /// System prompt for non-combat decisions (used by GameAgentAdvisor).
    /// Kept separate for prompt caching — different cache key from combat.
    /// </summary>
    public const string NonCombatSystem = """
        You are an expert Slay the Spire 2 player making strategic decisions outside of combat.
        You will be given the current game state and must make a decision by calling the appropriate tool.

        You have access to QUERY TOOLS to gather information before deciding:
        - view_deck_full: See your complete deck with card descriptions
        - view_deck_stats: See deck composition statistics
        - inspect_relics: See all your relics and their effects
        - view_battle_history: See reflections from recent combats (HP loss analysis, deck gaps, lessons)

        USE THESE TOOLS to make informed decisions. For example:
        - Before choosing a card reward, check view_deck_stats to understand what your deck needs.
        - Before an event choice, check view_battle_history to see what your deck is lacking.
        - Before choosing a map path, check your HP and deck state.

        STRATEGY FRAMEWORK:
        1. DECK BUILDING: Your deck has GAPS identified in battle reflections (offense/defense/economy/draw).
           Prioritize filling the most critical gap. A focused deck beats a bloated one.
        2. MAP: Avoid elites when below 60% HP. Prefer rest sites when below 40% HP.
           Events are good for card removal and relics. Take shops with 200+ gold.
           Prioritize paths leading to campfires before boss fights.
        3. EVENTS: Read ALL options carefully. Consider from the perspective of your deck's strategy
           and the gaps identified in battle reflections. Prefer options that:
           - Give relics or remove bad cards (deck thinning is powerful)
           - Provide resources you're lacking (gold, max HP, card upgrades)
           Avoid losing HP when already low. Trade HP for power only when healthy.
        4. CARD REWARDS: Evaluate synergy with existing deck. Skip if none fit.
           Check battle reflections — if your deck lacks defense, prioritize block cards.
           If it lacks offense, prioritize damage/scaling cards.
        5. REST SITE: Heal when below 60% HP. Upgrade best scaling card otherwise.
           Before boss: heal if below 75% HP.

        Always call exactly one action tool after gathering information.
        """;

    /// <summary>
    /// System prompt for Agentic Script mode.
    /// </summary>
    public const string AgenticScriptSystem = """
        You are an expert Slay the Spire 2 player and Lua programmer.
        Your job is to improve a Lua strategy script based on combat results and reflection.

        The script controls an auto-play bot. Entry point: decide_action(state) -> action table
        Called repeatedly each turn with fresh state. Must return ONE action.

        Available actions: play_card(index, target), play_card_no_target(index), use_potion(index, target), end_turn()
        Available helpers: find_card(state, filter), find_cards(state, filter), lowest_hp_enemy(state),
                          total_incoming_damage(state), has_power(powers, name), get_power_amount(powers, name), log(msg)

        RULES:
        1. Return ONLY the complete Lua script in ```lua ... ``` block
        2. Must define decide_action(state)
        3. NEVER end turn with energy > 0 and playable cards remaining
        4. Consider: lethal threats, energy efficiency, card synergies
        """;

    public const string AgenticScriptUserTemplate = """
        Current script:
        ```lua
        {CURRENT_SCRIPT}
        ```

        Combat result: {OUTCOME} | HP: {REMAINING_HP}/{MAX_HP} | Rounds: {ROUNDS}
        Enemies: {ENEMIES}

        Battle summary:
        {BATTLE_SUMMARY}

        Reflection from previous combats:
        {REFLECTION}

        Improve the script based on this data. Fix any issues that caused suboptimal play.
        """;

    public static string FormatStateForAgent(Core.BattleState state, bool includeRelics = true)
    {
        var sb = new System.Text.StringBuilder();
        var p = state.Player;

        // Player
        sb.AppendLine($"PLAYER: HP {p.Hp}/{p.MaxHp} | Block {p.Block} | Energy {p.Energy}/{p.MaxEnergy}");
        if (p.Powers.Count > 0)
        {
            sb.AppendLine("  Buffs/Debuffs:");
            foreach (var pw in p.Powers)
                sb.AppendLine($"    {pw.Name}({pw.Amount}) — {pw.Description}");
        }

        // Enemies
        sb.AppendLine("ENEMIES:");
        foreach (var e in state.Enemies)
        {
            if (!e.IsAlive) continue;
            var intent = e.IntentType == "Attack"
                ? $"Attack {e.IntentDamage}x{e.IntentHits} (total {e.IntentDamage * e.IntentHits})"
                : e.IntentType;
            sb.AppendLine($"  [{e.Index}] {e.Name}: HP {e.Hp}/{e.MaxHp} | Block {e.Block} | Intent: {intent}");
            if (e.Powers.Count > 0)
            {
                foreach (var pw in e.Powers)
                    sb.AppendLine($"    {pw.Name}({pw.Amount}) — {pw.Description}");
            }
        }

        // Hand
        int playableCount = 0;
        sb.AppendLine("HAND:");
        foreach (var c in state.Hand)
        {
            if (c.CanPlay) playableCount++;
            var playable = c.CanPlay ? "✓" : "✗";
            var target = c.TargetType == "None" ? "" : $", target:{c.TargetType}";
            sb.AppendLine($"  [{c.Index}] {c.Name}{(c.Upgraded ? "+" : "")} ({c.Cost}⚡ {c.Type}{target}, {playable}) — {c.Description}");
        }

        // Potions
        if (state.Potions.Count > 0)
        {
            sb.AppendLine("POTIONS:");
            foreach (var pot in state.Potions)
                sb.AppendLine($"  [{pot.Index}] {pot.Name} (target:{pot.TargetType}) — {pot.Description}");
        }

        // Relics — only in first message of combat
        if (includeRelics && state.Relics.Count > 0)
        {
            sb.AppendLine("RELICS:");
            foreach (var r in state.Relics)
            {
                var counter = r.Counter > 0 ? $" [{r.Counter}]" : "";
                sb.AppendLine($"  {r.Name}{counter} — {r.Description}");
            }
        }

        // Piles
        sb.AppendLine($"PILES: Draw {state.DrawPileCount} | Discard {state.DiscardPileCount} | Exhaust {state.ExhaustPileCount} | Round {state.Round}");

        // Incoming damage calculation
        int incomingDamage = 0;
        foreach (var e in state.Enemies)
            if (e.IsAlive && e.IntentType == "Attack")
                incomingDamage += e.IntentDamage * e.IntentHits;

        // Status reminder
        sb.AppendLine($"\n--- Energy: {p.Energy}/{p.MaxEnergy} | Playable: {playableCount}/{state.Hand.Count} | Incoming: {incomingDamage} ---");
        if (p.Energy > 0 && playableCount > 0)
            sb.Append("⚠ You MUST play a card. Do NOT end turn.");
        else
            sb.Append("→ No more plays possible. End turn.");

        return sb.ToString();
    }

    private static string FormatPowers(List<Core.PowerState> powers)
    {
        return string.Join(", ", powers.Select(p => p.Amount != 0 ? $"{p.Name}({p.Amount})" : p.Name));
    }

    public static string FormatAgenticScriptUser(
        string currentScript, bool victory, int remainingHp, int maxHp,
        int rounds, string enemies, string battleSummary, string reflection = "")
    {
        return AgenticScriptUserTemplate
            .Replace("{CURRENT_SCRIPT}", currentScript)
            .Replace("{OUTCOME}", victory ? "VICTORY" : "DEFEAT")
            .Replace("{REMAINING_HP}", remainingHp.ToString())
            .Replace("{MAX_HP}", maxHp.ToString())
            .Replace("{ROUNDS}", rounds.ToString())
            .Replace("{ENEMIES}", enemies)
            .Replace("{BATTLE_SUMMARY}", battleSummary)
            .Replace("{REFLECTION}", string.IsNullOrEmpty(reflection) ? "(no prior reflections)" : reflection);
    }
}
