# AutoPlay Mod for Slay the Spire 2

[中文文档](MultiLanguages/README_CN.md)

An AI-powered auto-play mod that plays Slay the Spire 2 autonomously — combat, card rewards, map navigation, events, rest sites, shops, and treasure rooms.

Supports three LLM providers (**Claude**, **GPT**, **Gemini**) with a unified agent architecture. The agent reasons about every decision, learns from experience, and accumulates persistent knowledge across runs.

## Features

- **Full run automation** — handles every game phase from start to death/victory
- **Multi-provider Agent mode** — Claude, GPT, and Gemini all support full tool-use conversations
- **Three-tier memory** — run context (current run), battle reflections (recent combats), persistent knowledge (across runs)
- **Post-combat reflection** — analyzes HP loss causes, deck gaps, and strategic mistakes
- **Background learning** — extracts card/enemy/event knowledge asynchronously without blocking gameplay
- **Run context tracking** — auto-maintains archetype, goals, and key decisions throughout a run
- **Script mode** — zero-latency Lua scripting for combat with customizable strategies
- **Hot-toggle** — press F10 (configurable) to enable/disable at any time

## Modes

### Script Mode

Uses a Lua script (`scripts/default_strategy.lua`) for combat decisions. Fast and deterministic. Non-combat decisions use simple heuristics.

Best for: testing, fast runs, low-cost operation.

### Agent Mode

Uses an LLM for **all** decisions via multi-turn tool-use conversations. The agent queries its memory, inspects the deck, saves observations, and reasons about card plays. Separate conversation threads for combat and non-combat flows, with run-level context automatically carried across sessions.

Best for: intelligent play, learning, research.

**Agent capabilities:**
- Reads card descriptions, enemy intents, and power effects
- Queries persistent memory for knowledge about cards, enemies, events, and relics
- Saves new observations with reasoning (card synergies, enemy patterns)
- Receives auto-injected context: enemy knowledge at combat start, card knowledge during rewards, run strategy throughout
- Performs structured post-combat reflection (HP loss analysis, deck gap identification)
- Maintains run context across sessions (archetype, goals, key decisions) — no memory loss between combats
- Falls back to script strategy on timeout, resumes agent on next turn

## Setup

### Requirements

- Slay the Spire 2 (v0.99.1+) with modding enabled
- For Agent mode: an API key for Claude, GPT, or Gemini

### Installation

Copy the `AutoPlayMod` folder to your STS2 mods directory:

```
{Steam}/steamapps/common/Slay the Spire 2/mods/AutoPlayMod/
```

Contents:
```
AutoPlayMod/
├── AutoPlayMod.dll
├── AutoPlayMod.pck
├── MoonSharp.Interpreter.dll
├── mod_manifest.json
├── autoplay_config.json
└── scripts/
    └── default_strategy.lua
```

### Configuration

Edit `autoplay_config.json`:

```json
{
  "mode": "agent",
  "script_path": "scripts/default_strategy.lua",
  "toggle_hotkey": "F10",
  "llm_provider": "claude",
  "llm_api_key": "YOUR-API-KEY",
  "llm_model": "claude-sonnet-4-6",
  "llm_base_url": "",
  "action_delay_ms": 300,
  "llm_timeout_ms": 60000
}
```

| Field | Description |
|-------|-------------|
| `mode` | `"script"` or `"agent"` |
| `script_path` | Lua strategy script path (script mode) |
| `toggle_hotkey` | Toggle key (default: F10) |
| `llm_provider` | `"claude"`, `"gpt"`, or `"gemini"` — all support full Agent mode |
| `llm_api_key` | Your API key |
| `llm_model` | Model ID (e.g., `claude-sonnet-4-6`, `gpt-4o`, `gemini-2.5-flash`) |
| `llm_base_url` | Custom endpoint URL (GPT only, for Azure/local proxies) |
| `action_delay_ms` | Delay between actions in ms |
| `llm_timeout_ms` | API timeout in ms (default: 60000) |

### Usage

1. Launch Slay the Spire 2
2. Start or continue a run
3. Press **F10** to toggle auto-play
4. The mod handles everything from there

## Building from Source

### Prerequisites

- .NET 9.0 SDK
- Godot 4.5.1 Mono (for PCK export)
- Slay the Spire 2 installed

### Build

Create `local.props` with your paths, then `dotnet build`:

```xml
<Project>
  <PropertyGroup>
    <STS2GamePath>D:\SteamLibrary\steamapps\common\Slay the Spire 2</STS2GamePath>
    <GodotExePath>D:\Godot\Godot.exe</GodotExePath>
  </PropertyGroup>
</Project>
```

### Project Structure

```
mod/
├── Core/                          # Game loop and state machine
│   ├── AutoPlayer.cs              # Main controller: event-driven combat + phase-based poll
│   ├── GamePhase.cs               # Phase enum + GamePhaseDetector
│   ├── PhaseHandler.cs            # Unified handler for all game phases
│   ├── RunContext.cs              # In-memory run context (archetype, goals, decisions)
│   ├── RunContextExtractor.cs     # Auto-extracts run context via background LLM
│   ├── BattleStateCollector.cs    # Extracts combat state for the agent
│   └── ActionExecutor.cs         # Executes card plays and potion uses
├── Agent/                         # LLM integration (provider-agnostic)
│   ├── ILlmClient.cs             # Unified interface: text + tools + conversation
│   ├── UnifiedGameAgent.cs        # Multi-turn conversation loop with tool use + mutex
│   ├── AgentStrategy.cs          # Combat via agent (with timeout fallback)
│   ├── GameAgentAdvisor.cs        # Non-combat decisions via agent
│   ├── BattleJournal.cs          # Post-combat reflection (thread-safe, background AI)
│   ├── Prompts.cs                # System prompts for combat/non-combat/reflection
│   ├── ToolDefinitions.cs        # All tool schemas
│   ├── JsonUtils.cs              # Shared JSON/markdown extraction
│   └── Clients/
│       ├── ClaudeClient.cs       # Claude API (ILlmClient)
│       ├── GptClient.cs          # OpenAI-compatible API (ILlmClient)
│       └── GeminiClient.cs       # Google Gemini API (ILlmClient)
├── Memory/                        # Persistent knowledge system
│   ├── MemoryStore.cs            # File-based CRUD, search, injection scoring
│   ├── MemoryEntry.cs            # Data classes (entity, archetype, run, strategy)
│   └── BackgroundMemoryWriter.cs  # Async extraction + observation merging
├── Scripting/                     # Lua scripting engine
│   ├── LuaStrategy.cs
│   └── LuaEngine.cs
├── Patches/                       # Harmony patches
│   ├── CombatPatches.cs          # Combat turn start event
│   └── InputPatch.cs             # Hotkey capture
├── Config/
│   └── ModConfig.cs
└── scripts/
    └── default_strategy.lua
```

## Architecture

### Game Phase State Machine

The mod reads game state to determine what to do — it does not run its own game loop:

```
GamePhaseDetector.Detect():
  NPlayerHand.IsInCardSelection?  → CombatHandSelect
  NOverlayStack.Peek()?           → RewardsScreen / CardRewardSelect / CardGridSelect / ...
  CombatManager.IsInProgress?     → CombatPlayerTurn (Idle if enemy turn)
  NMapScreen.IsOpen?              → MapScreen
  CurrentRoom.RoomType?           → EventRoom / RestSite / Shop / TreasureRoom
  else                            → Idle
```

**Combat** is driven by the game's `TurnStarted` event — the agent plays the entire turn in response. **Non-combat** uses a poll loop that acts only on phase **transitions**. Single-choice situations (one map node, proceed buttons) are handled instantly without querying the agent.

### Provider-Agnostic Agent

All three providers implement the same `ILlmClient` interface — text completion, single-turn tool use, and multi-turn conversation:

```
ILlmClient
├── ClaudeClient   (Anthropic API)
├── GptClient      (OpenAI-compatible API)
└── GeminiClient   (Google Gemini API)
```

Switching providers requires only changing `llm_provider` in config. No code changes needed.

### Tool-Use Loop

```
Game State → LLM → Tool Call → Execute → Updated State → LLM → ...
```

- **Query tools** (internal): `recall_memory`, `save_memory`, `view_deck_full`, `inspect_relics`, etc.
- **Action tools** (returned to caller): `play_card`, `end_turn`, `choose_map_node`, etc.

No artificial query limit. The agent queries as much as it needs, with `recall_memory` throttled after 6 calls to prevent infinite search loops. The caller's timeout (configurable, default 60s) is the safeguard. On timeout, the agent falls back to the script strategy for the rest of the turn and retries on the next turn.

### Three-Tier Memory

| Tier | Scope | Lifetime | Storage | Example |
|------|-------|----------|---------|---------|
| **Run Context** | Current run | Reset on new run | In-memory | "Building Strength, need AOE before Act 2 boss" |
| **Battle Reflections** | Recent combats | Last 20 | `battle_journal.json` | "Lost HP due to insufficient block vs Mawler" |
| **Persistent Memory** | All runs | Forever | `memories/` directory | "Bash: apply Vulnerable before heavy attacks" |

#### Persistent Memory

Located at `%APPDATA%/SlayTheSpire2/AutoPlayMod/memories/`:

```
memories/
├── shared/              # Cross-character game knowledge
│   ├── cards/           # Card observations, synergies, ratings
│   ├── enemies/         # Enemy patterns and strategies
│   ├── events/          # Event outcomes (indexed by game event ID)
│   └── relics/          # Relic evaluations
├── general/
│   └── strategy.json    # Universal strategic insights (general only, no run-specific)
└── {character}/
    ├── strategy.json    # Character-specific strategy
    ├── archetypes/      # Deck build patterns
    └── runs/            # Episodic run summaries
```

All files are plain JSON — freely viewable, editable, and deletable. Changes take effect immediately.

**Auto-injection**: Enemy knowledge at combat start, card knowledge during rewards, event knowledge at events — scored by relevance to stay within token budgets.

**Background extraction**: After each combat, a background task extracts card/enemy observations using exact game names (not LLM-guessed). When an entity accumulates 8+ observations, a merge pass consolidates them with contradiction resolution.

**Strategy filtering**: `strategy.json` rejects run-specific content (floor numbers, HP values). General insights only.

### Conversation & Safety

- **Combat session**: One thread per encounter. Mid-combat overlays (Headbutt, Armaments) stay in the same thread.
- **Non-combat session**: One thread per post-combat flow (rewards → map → event → rest).
- **Mutex lock**: Only one `RunAgentLoop` at a time — no interleaved messages.
- **Timeout recovery**: `CleanupAfterInterruption` rolls back to the last complete exchange. Basic reflection is saved immediately; AI reflection runs in background.
- **Parallel tool calls**: All handled correctly — query results batched, extras tracked.
- **Thread-safe reflections**: All `_reflections` access is locked. Background AI reflection replaces basic reflection without data races.

### Post-Combat Pipeline

```
Combat ends
├── Immediate: save basic reflection (0ms, never lost)
├── Background: AI reflection → replace basic → extract memories
│   ├── Card observations (exact names from game state)
│   ├── Enemy observations
│   ├── Strategy note (general only)
│   └── Merge if 8+ observations
├── Background: RunContext update (archetype, goals)
└── If defeat: save run summary to memories/{character}/runs/
```

## Support the Game

If you enjoy this mod, please support the developers by purchasing [Slay the Spire 2 on Steam](https://store.steampowered.com/app/2868840/Slay_the_Spire_2/) — my favorite game of 2026!

## License

This project is licensed under the [MIT License](LICENSE). Slay the Spire 2 is developed by Mega Crit.
