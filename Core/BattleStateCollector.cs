using AutoPlayMod.Agent;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;

namespace AutoPlayMod.Core;

/// <summary>
/// Collects a BattleState snapshot from the live game objects.
/// </summary>
public static class BattleStateCollector
{
    public static BattleState? Collect()
    {
        if (!CombatManager.Instance.IsInProgress)
            return null;

        var combatState = CombatManager.Instance.DebugOnlyGetState();
        if (combatState == null)
            return null;

        var player = combatState.Players.FirstOrDefault();
        if (player == null)
            return null;

        var pcs = player.PlayerCombatState!;
        var state = new BattleState
        {
            Round = combatState.RoundNumber,
            DrawPileCount = pcs.DrawPile.Cards.Count,
            DiscardPileCount = pcs.DiscardPile.Cards.Count,
            ExhaustPileCount = pcs.ExhaustPile.Cards.Count,
            Player = CollectPlayer(player),
            Enemies = CollectEnemies(combatState),
            Hand = CollectHand(pcs),
            Potions = CollectPotions(player),
            Relics = CollectRelics(player),
        };
        return state;
    }

    private static PlayerState CollectPlayer(Player player)
    {
        var creature = player.Creature;
        var pcs = player.PlayerCombatState!;
        return new PlayerState
        {
            Hp = creature.CurrentHp,
            MaxHp = creature.MaxHp,
            Block = creature.Block,
            Energy = pcs.Energy,
            MaxEnergy = pcs.MaxEnergy,
            Powers = CollectPowers(creature),
        };
    }

    private static List<EnemyState> CollectEnemies(CombatState combatState)
    {
        var enemies = new List<EnemyState>();
        int idx = 0;
        foreach (var creature in combatState.Enemies)
        {
            var es = new EnemyState
            {
                Index = idx,
                Name = creature.Monster?.GetType().Name ?? "Unknown",
                Hp = creature.CurrentHp,
                MaxHp = creature.MaxHp,
                Block = creature.Block,
                IsAlive = creature.IsAlive,
                Powers = CollectPowers(creature),
            };

            // Collect intent info
            if (creature.Monster?.NextMove != null)
            {
                var move = creature.Monster.NextMove;
                foreach (var intent in move.Intents)
                {
                    es.IntentType = ClassifyIntent(intent);
                    if (intent is AttackIntent atk)
                    {
                        // DamageCalc is a Func<decimal> that returns base damage per hit
                        var baseDmg = atk.DamageCalc?.Invoke() ?? 0;
                        es.IntentDamage = (int)baseDmg;
                        // Repeats: SingleAttack=1, MultiAttack=N, base=0
                        es.IntentHits = Math.Max(1, atk.Repeats);
                    }
                    // Take the first meaningful intent
                    break;
                }
            }

            enemies.Add(es);
            idx++;
        }
        return enemies;
    }

    private static string ClassifyIntent(AbstractIntent intent) => intent switch
    {
        MultiAttackIntent => "Attack",
        SingleAttackIntent => "Attack",
        AttackIntent => "Attack",
        DefendIntent => "Defend",
        BuffIntent => "Buff",
        CardDebuffIntent => "Debuff",
        DebuffIntent => "Debuff",
        HealIntent => "Heal",
        SummonIntent => "Summon",
        StunIntent => "Stun",
        SleepIntent => "Sleep",
        EscapeIntent => "Escape",
        _ => "Unknown"
    };

    private static List<CardState> CollectHand(PlayerCombatState pcs)
    {
        var cards = new List<CardState>();
        int idx = 0;
        foreach (var card in pcs.Hand.Cards)
        {
            // Get card description with resolved values (damage/block numbers)
            string desc = "";
            try { desc = JsonUtils.CleanText(card.GetDescriptionForPile(PileType.None) ?? ""); }
            catch { }

            cards.Add(new CardState
            {
                Index = idx,
                Id = card.GetType().Name,
                Name = card.Title ?? card.GetType().Name,
                Description = desc,
                Cost = card.EnergyCost?.GetAmountToSpend() ?? 0,
                Type = card.Type.ToString(),
                TargetType = card.TargetType.ToString(),
                CanPlay = card.CanPlay(),
            });
            idx++;
        }
        return cards;
    }

    private static List<PotionState> CollectPotions(Player player)
    {
        var potions = new List<PotionState>();
        int idx = 0;
        foreach (var slot in player.PotionSlots)
        {
            if (slot != null)
            {
                string potionDesc = "";
                try { potionDesc = JsonUtils.SafeLocText(slot.Description); }
                catch { }

                potions.Add(new PotionState
                {
                    Index = idx,
                    Id = slot.GetType().Name,
                    Name = JsonUtils.SafeLocText(slot.Title, slot.GetType().Name),
                    Description = potionDesc,
                    TargetType = slot.TargetType.ToString(),
                });
            }
            idx++;
        }
        return potions;
    }

    private static List<RelicState> CollectRelics(Player player)
    {
        var relics = new List<RelicState>();
        foreach (var relic in player.Relics)
        {
            string relicDesc = "";
            try { relicDesc = JsonUtils.SafeLocText(relic.Description); }
            catch { }

            relics.Add(new RelicState
            {
                Id = relic.GetType().Name,
                Name = JsonUtils.SafeLocText(relic.Title, relic.GetType().Name),
                Description = relicDesc,
                Counter = relic.DisplayAmount,
            });
        }
        return relics;
    }

    private static List<PowerState> CollectPowers(Creature creature)
    {
        var powers = new List<PowerState>();
        foreach (var power in creature.Powers)
        {
            string powerDesc = "";
            try
            {
                // Mimic the game's HoverTips logic for full descriptions:
                // If HasSmartDescription && IsMutable, use SmartDescription with variables injected.
                // Otherwise use Description with dumb variables.
                if (power.HasSmartDescription && power.IsMutable && power.Owner?.CombatState != null)
                {
                    var loc = power.SmartDescription;
                    loc.Add("Amount", power.Amount);
                    loc.Add("OnPlayer", power.Owner.IsPlayer);
                    loc.Add("IsMultiplayer", power.Owner.CombatState.Players.Count > 1);
                    loc.Add("PlayerCount", power.Owner.CombatState.Players.Count);
                    try
                    {
                        loc.Add("OwnerName", power.Owner.IsPlayer
                            ? power.Owner.Player?.Character?.Title
                            : power.Owner.Monster?.Title);
                    }
                    catch { }
                    power.DynamicVars.AddTo(loc);
                    powerDesc = JsonUtils.SafeFormattedLocText(loc);
                }
                else
                {
                    powerDesc = JsonUtils.SafeLocText(power.Description);
                }
            }
            catch
            {
                // Final fallback
                try { powerDesc = JsonUtils.SafeLocText(power.Description); }
                catch { powerDesc = power.GetType().Name; }
            }

            powers.Add(new PowerState
            {
                Id = power.GetType().Name,
                Name = JsonUtils.SafeLocText(power.Title, power.GetType().Name),
                Description = powerDesc,
                Amount = power.Amount,
            });
        }
        return powers;
    }

    /// <summary>Collect cards in a specific pile (for agent query tools).</summary>
    public static List<CardState> CollectPile(string pileName)
    {
        if (!CombatManager.Instance.IsInProgress) return [];
        var combatState = CombatManager.Instance.DebugOnlyGetState();
        var player = combatState?.Players.FirstOrDefault();
        if (player?.PlayerCombatState == null) return [];

        var pcs = player.PlayerCombatState;
        var pile = pileName.ToLowerInvariant() switch
        {
            "draw" => pcs.DrawPile.Cards,
            "discard" => pcs.DiscardPile.Cards,
            "exhaust" => pcs.ExhaustPile.Cards,
            "hand" => pcs.Hand.Cards,
            _ => null
        };
        if (pile == null) return [];

        var cards = new List<CardState>();
        int idx = 0;
        foreach (var card in pile)
        {
            string desc = "";
            try { desc = JsonUtils.CleanText(card.GetDescriptionForPile(PileType.None) ?? ""); }
            catch { }

            cards.Add(new CardState
            {
                Index = idx,
                Id = card.GetType().Name,
                Name = card.Title ?? card.GetType().Name,
                Description = desc,
                Cost = card.EnergyCost?.GetAmountToSpend() ?? 0,
                Type = card.Type.ToString(),
                TargetType = card.TargetType.ToString(),
                Upgraded = card.IsUpgraded,
            });
            idx++;
        }
        return cards;
    }
}
