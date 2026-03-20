using AutoPlayMod.Core;
using MegaCrit.Sts2.Core.Logging;

namespace AutoPlayMod.Scripting;

/// <summary>
/// A simple hardcoded strategy that doesn't depend on MoonSharp.
/// Used as fallback and to verify the mod framework works.
/// Logic: block lethal → play powers → play attacks → play skills → end turn.
/// </summary>
public class SimpleStrategy : IPlayStrategy
{
    public string Name => "SimpleStrategy";

    public Task<CombatAction> DecideAction(BattleState state)
    {
        var p = state.Player;

        // No energy → end turn
        if (p.Energy <= 0)
            return Task.FromResult(CombatAction.EndTurn());

        // Calculate incoming damage
        int incoming = 0;
        foreach (var e in state.Enemies)
        {
            if (e.IsAlive && e.IntentType == "Attack")
                incoming += e.IntentDamage * e.IntentHits;
        }
        bool isLethal = incoming > p.Hp + p.Block;

        // Find playable cards by type
        CardState? bestPower = null, bestAttack = null, bestSkill = null, bestAny = null;
        foreach (var card in state.Hand)
        {
            if (!card.CanPlay) continue;
            bestAny ??= card;
            switch (card.Type)
            {
                case "Power": bestPower ??= card; break;
                case "Attack": bestAttack ??= card; break;
                case "Skill": bestSkill ??= card; break;
            }
        }

        // If no playable cards → end turn
        if (bestAny == null)
            return Task.FromResult(CombatAction.EndTurn());

        // Pick which card to play
        CardState pick;
        if (isLethal && bestSkill != null)
            pick = bestSkill;       // Block lethal first
        else if (!isLethal && bestPower != null)
            pick = bestPower;       // Play powers when safe
        else if (bestAttack != null)
            pick = bestAttack;      // Attack
        else if (bestSkill != null)
            pick = bestSkill;       // Skills
        else
            pick = bestAny;         // Anything

        // Resolve target
        int? target = null;
        if (pick.TargetType == "AnyEnemy")
        {
            // Pick lowest HP alive enemy
            int lowestHp = int.MaxValue;
            foreach (var e in state.Enemies)
            {
                if (e.IsAlive && e.Hp < lowestHp)
                {
                    lowestHp = e.Hp;
                    target = e.Index;
                }
            }
        }

        Log.Info($"[AutoPlay] Playing: {pick.Name} (cost={pick.Cost}, type={pick.Type}) -> target={target}");
        return Task.FromResult(CombatAction.PlayCard(pick.Index, target));
    }
}
