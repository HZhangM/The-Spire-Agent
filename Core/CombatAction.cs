namespace AutoPlayMod.Core;

/// <summary>
/// A single action the auto-player can take during combat.
/// Returned by strategy scripts / LLM agents.
/// </summary>
public class CombatAction
{
    public CombatActionType Type { get; set; }
    public int CardIndex { get; set; }
    public int PotionIndex { get; set; }
    public int? TargetIndex { get; set; }

    public static CombatAction PlayCard(int cardIndex, int? targetIndex = null) => new()
    {
        Type = CombatActionType.PlayCard,
        CardIndex = cardIndex,
        TargetIndex = targetIndex
    };

    public static CombatAction UsePotion(int potionIndex, int? targetIndex = null) => new()
    {
        Type = CombatActionType.UsePotion,
        PotionIndex = potionIndex,
        TargetIndex = targetIndex
    };

    public static CombatAction EndTurn() => new()
    {
        Type = CombatActionType.EndTurn
    };

    public override string ToString() => Type switch
    {
        CombatActionType.PlayCard => $"PlayCard(card={CardIndex}, target={TargetIndex})",
        CombatActionType.UsePotion => $"UsePotion(potion={PotionIndex}, target={TargetIndex})",
        CombatActionType.EndTurn => "EndTurn",
        _ => $"Unknown({Type})"
    };
}

public enum CombatActionType
{
    PlayCard,
    UsePotion,
    EndTurn
}
