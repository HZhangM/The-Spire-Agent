using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;

namespace AutoPlayMod.Core;

/// <summary>
/// Executes CombatActions against the live game.
/// Translates our abstract actions into STS2 API calls.
/// </summary>
public static class ActionExecutor
{
    public static bool Execute(CombatAction action)
    {
        var combatState = CombatManager.Instance.DebugOnlyGetState();
        if (combatState == null) return false;

        var player = combatState.Players.FirstOrDefault();
        if (player == null) return false;

        return action.Type switch
        {
            CombatActionType.PlayCard => ExecutePlayCard(action, player, combatState),
            CombatActionType.UsePotion => ExecuteUsePotion(action, player, combatState),
            CombatActionType.EndTurn => ExecuteEndTurn(player),
            _ => false
        };
    }

    private static bool ExecutePlayCard(CombatAction action, Player player, CombatState combatState)
    {
        var hand = player.PlayerCombatState!.Hand.Cards;
        if (action.CardIndex < 0 || action.CardIndex >= hand.Count)
        {
            Log.Warn($"[AutoPlay] Invalid card index: {action.CardIndex}, hand size: {hand.Count}");
            return false;
        }

        var card = hand[action.CardIndex];
        if (!card.CanPlay())
        {
            Log.Warn($"[AutoPlay] Card '{card.GetType().Name}' cannot be played");
            return false;
        }

        Creature? target = ResolveTarget(action.TargetIndex, card.TargetType, combatState);

        // For cards that need a target but none was provided, pick the first hittable enemy
        if (card.TargetType == TargetType.AnyEnemy && target == null)
        {
            target = combatState.HittableEnemies.FirstOrDefault();
        }

        bool success = card.TryManualPlay(target);
        Log.Info($"[AutoPlay] PlayCard '{card.GetType().Name}' -> target={target?.GetType().Name ?? "none"}, success={success}");
        return success;
    }

    private static bool ExecuteUsePotion(CombatAction action, Player player, CombatState combatState)
    {
        var slots = player.PotionSlots;
        if (action.PotionIndex < 0 || action.PotionIndex >= slots.Count)
        {
            Log.Warn($"[AutoPlay] Invalid potion index: {action.PotionIndex}");
            return false;
        }

        var potion = slots[action.PotionIndex];
        if (potion == null)
        {
            Log.Warn($"[AutoPlay] Potion slot {action.PotionIndex} is empty");
            return false;
        }

        Creature? target = ResolveTarget(action.TargetIndex, potion.TargetType, combatState);

        // Default targeting for potions
        if (potion.TargetType == TargetType.AnyEnemy && target == null)
            target = combatState.HittableEnemies.FirstOrDefault();
        else if ((potion.TargetType == TargetType.Self || potion.TargetType == TargetType.AnyPlayer) && target == null)
            target = combatState.PlayerCreatures.FirstOrDefault(c => c.IsAlive);

        potion.EnqueueManualUse(target);
        Log.Info($"[AutoPlay] UsePotion '{potion.GetType().Name}' -> target={target?.GetType().Name ?? "none"}");
        return true;
    }

    private static bool ExecuteEndTurn(Player player)
    {
        Log.Info("[AutoPlay] EndTurn");
        PlayerCmd.EndTurn(player, false, null);
        return true;
    }

    private static Creature? ResolveTarget(int? targetIndex, TargetType targetType, CombatState combatState)
    {
        if (targetIndex == null) return null;

        return targetType switch
        {
            TargetType.AnyEnemy => combatState.Enemies
                .Where(e => e.IsAlive)
                .ElementAtOrDefault(targetIndex.Value),
            TargetType.AnyPlayer or TargetType.AnyAlly or TargetType.Self => combatState.PlayerCreatures
                .Where(c => c.IsAlive)
                .ElementAtOrDefault(targetIndex.Value),
            _ => null
        };
    }
}
