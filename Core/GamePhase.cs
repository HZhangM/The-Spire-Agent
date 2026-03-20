using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace AutoPlayMod.Core;

/// <summary>
/// All possible game phases the agent can encounter.
/// Each phase has a well-defined set of valid actions.
/// </summary>
public enum GamePhase
{
    /// <summary>Player's turn in combat — can play cards, use potions, end turn.</summary>
    CombatPlayerTurn,

    /// <summary>Mid-combat hand card selection (Armaments upgrade, Gambler's Brew discard, etc.).</summary>
    CombatHandSelect,

    /// <summary>Mid-combat overlay card selection (Headbutt, Seek, etc.).</summary>
    CombatOverlaySelect,

    /// <summary>Post-combat rewards screen — claim rewards then proceed.</summary>
    RewardsScreen,

    /// <summary>Card reward selection — pick a card or skip.</summary>
    CardRewardSelect,

    /// <summary>Card grid selection — upgrade, remove, transform, enchant from deck.</summary>
    CardGridSelect,

    /// <summary>Map screen — choose next node.</summary>
    MapScreen,

    /// <summary>Event room — choose an option.</summary>
    EventRoom,

    /// <summary>Rest site — rest, upgrade, etc.</summary>
    RestSite,

    /// <summary>Shop room — buy or leave.</summary>
    Shop,

    /// <summary>Treasure room — open chest.</summary>
    TreasureRoom,

    /// <summary>Unknown or generic overlay screen.</summary>
    GenericOverlay,

    /// <summary>Nothing to do — waiting for game animation, enemy turn, etc.</summary>
    Idle,
}

/// <summary>
/// Reads game state to determine the current phase.
/// Single source of truth for "what should the agent do now?"
/// </summary>
public static class GamePhaseDetector
{
    /// <summary>
    /// Detect the current game phase by reading game state.
    /// Priority order matters — higher priority phases are checked first.
    /// </summary>
    public static GamePhase Detect()
    {
        // 1. Mid-combat hand selection (highest priority — blocking combat)
        var hand = NPlayerHand.Instance;
        if (hand != null && hand.IsInCardSelection)
            return GamePhase.CombatHandSelect;

        // 2. Overlay screens (rewards, card selection, etc.)
        var stack = NOverlayStack.Instance;
        if (stack != null && stack.ScreenCount > 0)
        {
            var screen = stack.Peek();
            if (screen != null)
            {
                return ClassifyOverlay(screen);
            }
        }

        // 3. Combat player turn
        if (CombatManager.Instance.IsInProgress)
        {
            var combatState = CombatManager.Instance.DebugOnlyGetState();
            if (combatState?.CurrentSide == CombatSide.Player)
                return GamePhase.CombatPlayerTurn;
            // Enemy turn or between turns — idle
            return GamePhase.Idle;
        }

        // 4. Map screen
        var mapScreen = NMapScreen.Instance;
        if (mapScreen != null && mapScreen.IsOpen && !mapScreen.IsTraveling)
        {
            return GamePhase.MapScreen;
        }

        // 5. Room-level interactions
        var runState = RunManager.Instance?.DebugOnlyGetState();
        var room = runState?.CurrentRoom;
        if (room != null)
        {
            return room.RoomType switch
            {
                RoomType.Event => GamePhase.EventRoom,
                RoomType.RestSite => GamePhase.RestSite,
                RoomType.Shop => GamePhase.Shop,
                RoomType.Treasure => GamePhase.TreasureRoom,
                _ => GamePhase.Idle,
            };
        }

        return GamePhase.Idle;
    }

    private static GamePhase ClassifyOverlay(IOverlayScreen screen)
    {
        return screen switch
        {
            NRewardsScreen => GamePhase.RewardsScreen,
            NCardRewardSelectionScreen => GamePhase.CardRewardSelect,
            // NSimpleCardSelectScreen is a subclass of NCardGridSelectionScreen — check it first
            NSimpleCardSelectScreen => CombatManager.Instance.IsInProgress
                ? GamePhase.CombatOverlaySelect
                : GamePhase.CardGridSelect,
            NCardGridSelectionScreen => GamePhase.CardGridSelect,
            _ => GamePhase.GenericOverlay,
        };
    }

    /// <summary>
    /// Check if the current phase is a combat-related phase.
    /// </summary>
    public static bool IsCombatPhase(GamePhase phase) => phase switch
    {
        GamePhase.CombatPlayerTurn => true,
        GamePhase.CombatHandSelect => true,
        GamePhase.CombatOverlaySelect => true,
        _ => false,
    };
}
