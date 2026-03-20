using MoonSharp.Interpreter;
using MegaCrit.Sts2.Core.Logging;
using AutoPlayMod.Core;

namespace AutoPlayMod.Scripting;

/// <summary>
/// Wraps MoonSharp to execute Lua strategy scripts.
/// Handles state marshalling between C# BattleState and Lua tables.
/// Supports hot-reload of scripts.
/// </summary>
public class LuaEngine : IDisposable
{
    private Script _script;
    private string _currentScriptSource = "";

    public string CurrentScriptSource => _currentScriptSource;

    public LuaEngine()
    {
        _script = CreateScript();
    }

    /// <summary>
    /// Load a Lua script from string. Can be called multiple times to hot-reload.
    /// </summary>
    public void LoadScript(string luaSource)
    {
        var newScript = CreateScript();
        // Validate by loading
        newScript.DoString(luaSource);

        // Check that decide_action function exists
        var fn = newScript.Globals.Get("decide_action");
        if (fn.Type != DataType.Function)
            throw new InvalidOperationException("Lua script must define a 'decide_action(state)' function");

        _script = newScript;
        _currentScriptSource = luaSource;
        Log.Info("[AutoPlay/Lua] Script loaded successfully");
    }

    /// <summary>
    /// Load a Lua script from file.
    /// </summary>
    public void LoadScriptFile(string path)
    {
        var source = File.ReadAllText(path);
        LoadScript(source);
        Log.Info($"[AutoPlay/Lua] Script loaded from: {path}");
    }

    /// <summary>
    /// Call decide_action(state) in the Lua script.
    /// Returns a CombatAction.
    /// </summary>
    public CombatAction DecideAction(BattleState state)
    {
        var luaState = MarshalStateToLua(state);
        var fn = _script.Globals.Get("decide_action");
        var result = _script.Call(fn, luaState);
        return MarshalActionFromLua(result);
    }

    /// <summary>
    /// Call on_combat_end(state, victory, remaining_hp) if it exists.
    /// </summary>
    public void NotifyCombatEnd(BattleState state, bool victory, int remainingHp)
    {
        var fn = _script.Globals.Get("on_combat_end");
        if (fn.Type == DataType.Function)
        {
            var luaState = MarshalStateToLua(state);
            _script.Call(fn, luaState, DynValue.NewBoolean(victory), DynValue.NewNumber(remainingHp));
        }
    }

    private Script CreateScript()
    {
        var script = new Script(CoreModules.Preset_SoftSandbox);
        RegisterHelperFunctions(script);
        return script;
    }

    /// <summary>
    /// Register helper functions available to Lua scripts.
    /// </summary>
    private void RegisterHelperFunctions(Script script)
    {
        // Action constructors
        script.Globals["play_card"] = (Func<int, int?, Table>)((cardIndex, targetIndex) =>
        {
            var t = new Table(script);
            t["type"] = "play_card";
            t["card_index"] = cardIndex;
            if (targetIndex.HasValue)
                t["target_index"] = targetIndex.Value;
            return t;
        });

        // Overload without target
        script.Globals["play_card_no_target"] = (Func<int, Table>)((cardIndex) =>
        {
            var t = new Table(script);
            t["type"] = "play_card";
            t["card_index"] = cardIndex;
            return t;
        });

        script.Globals["use_potion"] = (Func<int, int?, Table>)((potionIndex, targetIndex) =>
        {
            var t = new Table(script);
            t["type"] = "use_potion";
            t["potion_index"] = potionIndex;
            if (targetIndex.HasValue)
                t["target_index"] = targetIndex.Value;
            return t;
        });

        script.Globals["end_turn"] = (Func<Table>)(() =>
        {
            var t = new Table(script);
            t["type"] = "end_turn";
            return t;
        });

        // Utility: find card in hand by filter
        script.Globals["find_card"] = (Func<Table, Table, DynValue>)((state, filter) =>
        {
            var hand = state.Get("hand").Table;
            if (hand == null) return DynValue.Nil;

            foreach (var pair in hand.Pairs)
            {
                var card = pair.Value.Table;
                if (card == null) continue;
                if (MatchesFilter(card, filter))
                    return pair.Value;
            }
            return DynValue.Nil;
        });

        // Utility: find all matching cards
        script.Globals["find_cards"] = (Func<Table, Table, Table>)((state, filter) =>
        {
            var result = new Table(script);
            var hand = state.Get("hand").Table;
            if (hand == null) return result;

            int idx = 1;
            foreach (var pair in hand.Pairs)
            {
                var card = pair.Value.Table;
                if (card == null) continue;
                if (MatchesFilter(card, filter))
                    result.Set(idx++, pair.Value);
            }
            return result;
        });

        // Utility: enemy with lowest HP
        script.Globals["lowest_hp_enemy"] = (Func<Table, DynValue>)((state) =>
        {
            var enemies = state.Get("enemies").Table;
            if (enemies == null) return DynValue.Nil;

            DynValue best = DynValue.Nil;
            double bestHp = double.MaxValue;
            foreach (var pair in enemies.Pairs)
            {
                var enemy = pair.Value.Table;
                if (enemy == null) continue;
                var alive = enemy.Get("is_alive");
                if (alive.Type == DataType.Boolean && !alive.Boolean) continue;
                var hp = enemy.Get("hp").Number;
                if (hp < bestHp)
                {
                    bestHp = hp;
                    best = pair.Value;
                }
            }
            return best;
        });

        // Utility: total incoming damage from all enemies
        script.Globals["total_incoming_damage"] = (Func<Table, double>)((state) =>
        {
            var enemies = state.Get("enemies").Table;
            if (enemies == null) return 0;

            double total = 0;
            foreach (var pair in enemies.Pairs)
            {
                var enemy = pair.Value.Table;
                if (enemy == null) continue;
                var alive = enemy.Get("is_alive");
                if (alive.Type == DataType.Boolean && !alive.Boolean) continue;
                var intentType = enemy.Get("intent_type").String;
                if (intentType == "Attack")
                {
                    var dmg = enemy.Get("intent_damage").Number;
                    var hits = enemy.Get("intent_hits").Number;
                    total += dmg * hits;
                }
            }
            return total;
        });

        // Utility: check if a creature has a specific power
        script.Globals["has_power"] = (Func<Table, string, bool>)((powersTable, powerName) =>
        {
            foreach (var pair in powersTable.Pairs)
            {
                var p = pair.Value.Table;
                if (p == null) continue;
                var name = p.Get("name").String ?? p.Get("id").String;
                if (name != null && name.Contains(powerName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        });

        // Utility: get power amount
        script.Globals["get_power_amount"] = (Func<Table, string, double>)((powersTable, powerName) =>
        {
            foreach (var pair in powersTable.Pairs)
            {
                var p = pair.Value.Table;
                if (p == null) continue;
                var name = p.Get("name").String ?? p.Get("id").String;
                if (name != null && name.Contains(powerName, StringComparison.OrdinalIgnoreCase))
                    return p.Get("amount").Number;
            }
            return 0;
        });

        // Logging
        script.Globals["log"] = (Action<string>)((msg) => Log.Info($"[AutoPlay/Lua] {msg}"));
    }

    private static bool MatchesFilter(Table card, Table filter)
    {
        // Check can_play
        var canPlay = card.Get("can_play");
        if (canPlay.Type == DataType.Boolean && !canPlay.Boolean)
            return false;

        // Filter by type
        var filterType = filter.Get("type");
        if (filterType.Type == DataType.String)
        {
            var cardType = card.Get("type").String;
            if (!string.Equals(cardType, filterType.String, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // Filter by name
        var filterName = filter.Get("name");
        if (filterName.Type == DataType.String)
        {
            var cardName = card.Get("name").String ?? card.Get("id").String;
            if (cardName == null || !cardName.Contains(filterName.String, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // Filter by max cost
        var filterMaxCost = filter.Get("max_cost");
        if (filterMaxCost.Type == DataType.Number)
        {
            var cost = card.Get("cost").Number;
            if (cost > filterMaxCost.Number)
                return false;
        }

        return true;
    }

    #region Marshalling

    private Table MarshalStateToLua(BattleState state)
    {
        var t = new Table(_script);

        // Player
        var player = new Table(_script);
        player["hp"] = state.Player.Hp;
        player["max_hp"] = state.Player.MaxHp;
        player["block"] = state.Player.Block;
        player["energy"] = state.Player.Energy;
        player["max_energy"] = state.Player.MaxEnergy;
        player["powers"] = MarshalPowers(state.Player.Powers);
        t["player"] = player;

        // Enemies
        var enemies = new Table(_script);
        for (int i = 0; i < state.Enemies.Count; i++)
        {
            var e = state.Enemies[i];
            var et = new Table(_script);
            et["index"] = e.Index;
            et["name"] = e.Name;
            et["hp"] = e.Hp;
            et["max_hp"] = e.MaxHp;
            et["block"] = e.Block;
            et["intent_type"] = e.IntentType;
            et["intent_damage"] = e.IntentDamage;
            et["intent_hits"] = e.IntentHits;
            et["is_alive"] = e.IsAlive;
            et["powers"] = MarshalPowers(e.Powers);
            enemies.Set(i + 1, DynValue.NewTable(et)); // Lua arrays are 1-indexed
        }
        t["enemies"] = enemies;

        // Hand
        var hand = new Table(_script);
        for (int i = 0; i < state.Hand.Count; i++)
        {
            var c = state.Hand[i];
            var ct2 = new Table(_script);
            ct2["index"] = c.Index;
            ct2["id"] = c.Id;
            ct2["name"] = c.Name;
            ct2["cost"] = c.Cost;
            ct2["type"] = c.Type;
            ct2["target_type"] = c.TargetType;
            ct2["can_play"] = c.CanPlay;
            ct2["upgraded"] = c.Upgraded;
            hand.Set(i + 1, DynValue.NewTable(ct2));
        }
        t["hand"] = hand;

        // Potions
        var potions = new Table(_script);
        for (int i = 0; i < state.Potions.Count; i++)
        {
            var p = state.Potions[i];
            var pt = new Table(_script);
            pt["index"] = p.Index;
            pt["id"] = p.Id;
            pt["name"] = p.Name;
            pt["target_type"] = p.TargetType;
            potions.Set(i + 1, DynValue.NewTable(pt));
        }
        t["potions"] = potions;

        // Relics
        var relics = new Table(_script);
        for (int i = 0; i < state.Relics.Count; i++)
        {
            var r = state.Relics[i];
            var rt = new Table(_script);
            rt["id"] = r.Id;
            rt["name"] = r.Name;
            rt["counter"] = r.Counter;
            relics.Set(i + 1, DynValue.NewTable(rt));
        }
        t["relics"] = relics;

        // Pile counts
        t["draw_pile_count"] = state.DrawPileCount;
        t["discard_pile_count"] = state.DiscardPileCount;
        t["exhaust_pile_count"] = state.ExhaustPileCount;
        t["round"] = state.Round;

        return t;
    }

    private Table MarshalPowers(List<PowerState> powers)
    {
        var t = new Table(_script);
        for (int i = 0; i < powers.Count; i++)
        {
            var p = powers[i];
            var pt = new Table(_script);
            pt["id"] = p.Id;
            pt["name"] = p.Name;
            pt["amount"] = p.Amount;
            t.Set(i + 1, DynValue.NewTable(pt));
        }
        return t;
    }

    private static CombatAction MarshalActionFromLua(DynValue result)
    {
        if (result.Type != DataType.Table)
            return CombatAction.EndTurn();

        var table = result.Table;
        var type = table.Get("type").String ?? "end_turn";

        return type switch
        {
            "play_card" => CombatAction.PlayCard(
                (int)(table.Get("card_index").Number),
                table.Get("target_index").Type == DataType.Number
                    ? (int?)table.Get("target_index").Number
                    : null),
            "use_potion" => CombatAction.UsePotion(
                (int)(table.Get("potion_index").Number),
                table.Get("target_index").Type == DataType.Number
                    ? (int?)table.Get("target_index").Number
                    : null),
            "end_turn" => CombatAction.EndTurn(),
            _ => CombatAction.EndTurn()
        };
    }

    #endregion

    public void Dispose()
    {
        // MoonSharp Script doesn't have Dispose, but we null out the reference
        _script = null!;
    }
}
