namespace AutoPlayMod.Core;

/// <summary>
/// Interface for all play strategies (script, agentic, agent).
/// Called repeatedly until it returns EndTurn.
/// Each call receives a fresh snapshot of the current battle state.
/// </summary>
public interface IPlayStrategy
{
    /// <summary>
    /// Decide the next single action given the current battle state.
    /// Return EndTurn when done with the turn.
    /// </summary>
    Task<CombatAction> DecideAction(BattleState state);

    /// <summary>
    /// Called when a combat ends. Allows strategies to learn/adapt.
    /// </summary>
    Task OnCombatEnd(BattleState finalState, bool victory, int remainingHp) => Task.CompletedTask;

    /// <summary>
    /// Human-readable name for logging.
    /// </summary>
    string Name { get; }
}
