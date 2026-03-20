using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Rooms;

namespace AutoPlayMod.Patches;

/// <summary>
/// Subscribe to CombatManager events when combat is set up.
/// Uses CombatSetUp event to wire up turn/end listeners.
/// </summary>
[HarmonyPatch]
public static class CombatPatches
{
    private static bool _subscribed;

    /// <summary>
    /// Patch CombatManager constructor or SetUp to subscribe to events.
    /// We hook into SetUpForCombat to attach event listeners on each combat.
    /// </summary>
    [HarmonyPatch(typeof(CombatManager), nameof(CombatManager.SetUpCombat))]
    [HarmonyPostfix]
    public static void OnSetUpForCombat(CombatManager __instance)
    {
        try
        {
            if (_subscribed) return;
            _subscribed = true;

            // Subscribe to turn start: fires when any side's turn starts
            __instance.TurnStarted += OnTurnStarted;

            // Subscribe to combat end
            __instance.CombatEnded += OnCombatEnded;
            __instance.CombatWon += OnCombatWon;

            Log.Info("[AutoPlay] Subscribed to combat events");
        }
        catch (Exception ex)
        {
            Log.Error($"[AutoPlay] Failed to subscribe: {ex.Message}");
        }
    }

    private static void OnTurnStarted(CombatState state)
    {
        // Only trigger on player turn
        if (state.CurrentSide != CombatSide.Player) return;

        try
        {
            ModEntry.Instance?.AutoPlayer.OnPlayerTurnStart();
        }
        catch (Exception ex)
        {
            Log.Error($"[AutoPlay] OnTurnStarted error: {ex.Message}");
        }
    }

    private static void OnCombatEnded(CombatRoom room)
    {
        _subscribed = false;
        try
        {
            // CombatEnded fires for any end (victory or defeat)
            // Check via CombatWon event instead for victory specifically
            ModEntry.Instance?.AutoPlayer.OnCombatEnd(false);
        }
        catch (Exception ex)
        {
            Log.Error($"[AutoPlay] OnCombatEnded error: {ex.Message}");
        }
    }

    private static void OnCombatWon(CombatRoom room)
    {
        _subscribed = false;
        try
        {
            ModEntry.Instance?.AutoPlayer.OnCombatEnd(true);
        }
        catch (Exception ex)
        {
            Log.Error($"[AutoPlay] OnCombatWon error: {ex.Message}");
        }
    }
}
