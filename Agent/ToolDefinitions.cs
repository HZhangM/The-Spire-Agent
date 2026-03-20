using System.Text.Json;

namespace AutoPlayMod.Agent;

/// <summary>
/// All tool definitions for the unified game agent.
/// Tools are disclosed selectively based on game context.
/// </summary>
public static class ToolDefinitions
{
    // ==================== COMBAT TOOLS ====================

    public static readonly JsonElement PlayCard = JsonDocument.Parse("""
    {
        "name": "play_card",
        "description": "Play a card from your hand. Specify card_index (0-based index in hand) and optionally target_index (0-based index in enemies list) for cards that target a single enemy.",
        "input_schema": {
            "type": "object",
            "properties": {
                "card_index": { "type": "integer", "description": "0-based index of the card in hand" },
                "target_index": { "type": "integer", "description": "0-based index of the target enemy. Required for cards with target_type 'AnyEnemy'." }
            },
            "required": ["card_index"]
        }
    }
    """).RootElement;

    public static readonly JsonElement UsePotion = JsonDocument.Parse("""
    {
        "name": "use_potion",
        "description": "Use a potion from your potion slots. Specify potion_index and optionally target_index for targeted potions.",
        "input_schema": {
            "type": "object",
            "properties": {
                "potion_index": { "type": "integer", "description": "0-based index of the potion" },
                "target_index": { "type": "integer", "description": "0-based index of the target enemy/player if needed" }
            },
            "required": ["potion_index"]
        }
    }
    """).RootElement;

    public static readonly JsonElement EndTurn = JsonDocument.Parse("""
    {
        "name": "end_turn",
        "description": "End your turn. ONLY use this when energy is 0 or ALL remaining cards in hand have can_play=false. NEVER end turn if you still have energy AND playable cards.",
        "input_schema": {
            "type": "object",
            "properties": {},
            "required": []
        }
    }
    """).RootElement;

    // ==================== MAP TOOLS ====================

    public static readonly JsonElement ChooseMapNode = JsonDocument.Parse("""
    {
        "name": "choose_map_node",
        "description": "Choose which map node to travel to next.",
        "input_schema": {
            "type": "object",
            "properties": {
                "node_index": { "type": "integer", "description": "0-based index of the available node to travel to" }
            },
            "required": ["node_index"]
        }
    }
    """).RootElement;

    // ==================== EVENT TOOLS ====================

    public static readonly JsonElement ChooseEventOption = JsonDocument.Parse("""
    {
        "name": "choose_event_option",
        "description": "Choose an option in the current event.",
        "input_schema": {
            "type": "object",
            "properties": {
                "option_index": { "type": "integer", "description": "0-based index of the event option to choose" }
            },
            "required": ["option_index"]
        }
    }
    """).RootElement;

    // ==================== CARD SELECTION TOOLS ====================

    public static readonly JsonElement ChooseCard = JsonDocument.Parse("""
    {
        "name": "choose_card",
        "description": "Select a card. Used for: card rewards after combat, upgrading a card, removing a card, transforming a card, or any other card selection prompt.",
        "input_schema": {
            "type": "object",
            "properties": {
                "card_index": { "type": "integer", "description": "0-based index of the card to select" }
            },
            "required": ["card_index"]
        }
    }
    """).RootElement;

    public static readonly JsonElement SkipCardReward = JsonDocument.Parse("""
    {
        "name": "skip_card_reward",
        "description": "Skip the card reward / card selection. Use when none of the offered cards are good for your deck.",
        "input_schema": {
            "type": "object",
            "properties": {},
            "required": []
        }
    }
    """).RootElement;

    // ==================== REST SITE TOOLS ====================

    public static readonly JsonElement ChooseRestOption = JsonDocument.Parse("""
    {
        "name": "choose_rest_option",
        "description": "Choose what to do at a rest site (e.g., Rest to heal, Smith to upgrade a card).",
        "input_schema": {
            "type": "object",
            "properties": {
                "option_index": { "type": "integer", "description": "0-based index of the rest site option" }
            },
            "required": ["option_index"]
        }
    }
    """).RootElement;

    // ==================== SHOP TOOLS ====================

    public static readonly JsonElement ShopBuy = JsonDocument.Parse("""
    {
        "name": "shop_buy",
        "description": "Buy an item from the shop (card, relic, or potion). Check if you have enough gold first.",
        "input_schema": {
            "type": "object",
            "properties": {
                "item_index": { "type": "integer", "description": "0-based index of the item in the shop listing" }
            },
            "required": ["item_index"]
        }
    }
    """).RootElement;

    public static readonly JsonElement ShopRemoveCard = JsonDocument.Parse("""
    {
        "name": "shop_remove_card",
        "description": "Use the card removal service to permanently remove a card from your deck. Costs gold (increases each use).",
        "input_schema": {
            "type": "object",
            "properties": {},
            "required": []
        }
    }
    """).RootElement;

    public static readonly JsonElement ShopLeave = JsonDocument.Parse("""
    {
        "name": "shop_leave",
        "description": "Leave the shop without buying anything else.",
        "input_schema": {
            "type": "object",
            "properties": {},
            "required": []
        }
    }
    """).RootElement;

    // ==================== REFLECTION TOOLS ====================

    public static readonly JsonElement Reflection = JsonDocument.Parse("""
    {
        "name": "battle_reflection",
        "description": "Record structured reflection after a combat. Analyze HP loss causes, deck weaknesses, and strategy adjustments.",
        "input_schema": {
            "type": "object",
            "properties": {
                "performance_rating": { "type": "integer", "description": "1-5 rating (1=terrible, 5=perfect)" },
                "hp_loss_analysis": {
                    "type": "string",
                    "description": "Why HP was lost: insufficient offense (fight too long) or insufficient defense (missed blocks). Be specific about turns."
                },
                "deck_gaps": {
                    "type": "string",
                    "description": "What the deck is MISSING. Categories: Offense (damage/scaling/AOE), Defense (block/weak), Economy (energy/cost), Draw (draw/thin/scry). List specific effects that would help."
                },
                "key_mistakes": {
                    "type": "array",
                    "items": { "type": "string" },
                    "description": "List of specific mistakes made during combat"
                },
                "key_successes": {
                    "type": "array",
                    "items": { "type": "string" },
                    "description": "List of things done well"
                },
                "lessons": {
                    "type": "array",
                    "items": { "type": "string" },
                    "description": "Actionable lessons for future combats"
                },
                "card_evaluations": {
                    "type": "string",
                    "description": "Brief evaluation of which cards performed well or poorly"
                },
                "strategy_update": {
                    "type": "string",
                    "description": "One-sentence strategy adjustment for future fights"
                }
            },
            "required": ["performance_rating", "hp_loss_analysis", "deck_gaps", "lessons", "strategy_update"]
        }
    }
    """).RootElement;

    // ==================== GENERIC TOOLS ====================

    public static readonly JsonElement Proceed = JsonDocument.Parse("""
    {
        "name": "proceed",
        "description": "Click proceed/continue/skip to advance past the current screen.",
        "input_schema": {
            "type": "object",
            "properties": {},
            "required": []
        }
    }
    """).RootElement;

    // ==================== QUERY TOOLS (information gathering, no game action) ====================

    public static readonly JsonElement InspectDeck = JsonDocument.Parse("""
    {
        "name": "inspect_deck",
        "description": "Look at cards in a specific pile. Returns card names, costs, types, and descriptions. Use this to plan ahead — check draw pile to know what's coming, discard pile to see what's been played, exhaust pile for removed cards.",
        "input_schema": {
            "type": "object",
            "properties": {
                "pile": {
                    "type": "string",
                    "enum": ["draw", "discard", "exhaust"],
                    "description": "Which pile to inspect"
                }
            },
            "required": ["pile"]
        }
    }
    """).RootElement;

    public static readonly JsonElement InspectRelics = JsonDocument.Parse("""
    {
        "name": "inspect_relics",
        "description": "View all relics and their effects. Use at the start of combat to understand your passive bonuses.",
        "input_schema": {
            "type": "object",
            "properties": {},
            "required": []
        }
    }
    """).RootElement;

    public static readonly JsonElement ViewDeckFull = JsonDocument.Parse("""
    {
        "name": "view_deck_full",
        "description": "View your full deck with card names, costs, types, rarities, and descriptions. Works outside of combat to review your entire deck.",
        "input_schema": {
            "type": "object",
            "properties": {},
            "required": []
        }
    }
    """).RootElement;

    public static readonly JsonElement ViewBattleHistory = JsonDocument.Parse("""
    {
        "name": "view_battle_history",
        "description": "View recent battle summaries and lessons learned. Helps inform card reward, upgrade, and pathing decisions.",
        "input_schema": {
            "type": "object",
            "properties": {
                "count": { "type": "integer", "description": "Number of recent battles to show (default 3, max 10)" }
            },
            "required": []
        }
    }
    """).RootElement;

    public static readonly JsonElement ViewDeckStats = JsonDocument.Parse("""
    {
        "name": "view_deck_stats",
        "description": "View deck statistics: total cards, attack/skill/power counts, average cost, upgrade count. Useful for evaluating card rewards.",
        "input_schema": {
            "type": "object",
            "properties": {},
            "required": []
        }
    }
    """).RootElement;

    // ==================== MEMORY TOOLS ====================

    public static readonly JsonElement RecallMemory = JsonDocument.Parse("""
    {
        "name": "recall_memory",
        "description": "Retrieve knowledge from persistent memory. Supports batch lookup — pass multiple names to query several entities at once.",
        "input_schema": {
            "type": "object",
            "properties": {
                "category": {
                    "type": "string",
                    "enum": ["card", "enemy", "event", "relic", "archetype", "strategy", "run"],
                    "description": "What type of knowledge to recall"
                },
                "names": {
                    "type": "array",
                    "items": { "type": "string" },
                    "description": "List of exact entity names to look up (e.g. ['Heavy Blade', 'Inflame', 'Bash']). Use this for batch queries."
                },
                "name": {
                    "type": "string",
                    "description": "Single exact entity name (e.g. 'Heavy Blade'). For looking up one entity. Use 'names' for batch."
                },
                "keyword": {
                    "type": "string",
                    "description": "Search keyword across all entries in the category. Used when you don't know exact names."
                }
            },
            "required": ["category"]
        }
    }
    """).RootElement;

    public static readonly JsonElement SaveMemory = JsonDocument.Parse("""
    {
        "name": "save_memory",
        "description": "Save an insight to persistent memory. Include WHY (reasoning), not just WHAT. Memories persist across combats and runs. For 'strategy' category: ONLY save general reusable insights (e.g. 'Apply Vulnerable before heavy attacks'), NOT run-specific details (e.g. 'At Floor 5 chose Inflame').",
        "input_schema": {
            "type": "object",
            "properties": {
                "category": {
                    "type": "string",
                    "enum": ["card", "enemy", "event", "relic", "strategy"],
                    "description": "What type of knowledge to save"
                },
                "name": {
                    "type": "string",
                    "description": "Entity name. For cards/enemies/relics use the in-game name (e.g. 'Heavy Blade'). For events use the event ID from the prompt (e.g. 'EVENT.SAPPHIRE_SEED'). For strategy use 'general' or 'character'."
                },
                "observation": {
                    "type": "string",
                    "description": "One concise insight with reasoning. E.g. 'Strong with Strength builds because damage scales with multiplier (3x base)'"
                },
                "rating": {
                    "type": "integer",
                    "description": "1-5 usefulness rating (optional)"
                },
                "synergies": {
                    "type": "array",
                    "items": { "type": "string" },
                    "description": "Other entities that synergize (optional)"
                }
            },
            "required": ["category", "name", "observation"]
        }
    }
    """).RootElement;

    // ==================== TOOL SETS BY CONTEXT ====================

    /// <summary>Query tools available in ALL contexts (combat and non-combat).</summary>
    public static JsonElement[] QueryTools => [InspectRelics, ViewDeckFull, ViewBattleHistory, ViewDeckStats, RecallMemory, SaveMemory];

    /// <summary>Query tools only available during combat.</summary>
    public static JsonElement[] CombatQueryTools => [InspectDeck];

    /// <summary>Action tools for combat (no query tools — those are added by UnifiedGameAgent).</summary>
    public static JsonElement[] CombatActionTools => [PlayCard, UsePotion, EndTurn];

    /// <summary>All combat tools (legacy, includes query tools inline).</summary>
    public static JsonElement[] CombatTools => [PlayCard, UsePotion, EndTurn, InspectDeck, InspectRelics];

    public static JsonElement[] MapTools => [ChooseMapNode];
    public static JsonElement[] EventTools => [ChooseEventOption];
    public static JsonElement[] CardSelectionTools => [ChooseCard, SkipCardReward];
    public static JsonElement[] RestSiteTools => [ChooseRestOption];
    public static JsonElement[] ShopTools => [ShopBuy, ShopRemoveCard, ShopLeave];
    public static JsonElement[] GenericTools => [Proceed];

    /// <summary>All query tool names for quick lookup.</summary>
    public static readonly HashSet<string> QueryToolNames =
    [
        "inspect_deck", "inspect_relics", "view_deck_full", "view_battle_history", "view_deck_stats",
        "recall_memory", "save_memory"
    ];
}
