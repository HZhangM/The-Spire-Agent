using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.CommonUi;

namespace AutoPlayMod.Patches;

/// <summary>
/// Capture keyboard input for the auto-play toggle hotkey.
/// Patches NHotkeyManager._UnhandledInput to intercept our key
/// before the game processes it.
/// </summary>
[HarmonyPatch]
public static class InputPatch
{
    [HarmonyPatch(typeof(NHotkeyManager), "_UnhandledInput")]
    [HarmonyPrefix]
    public static void OnUnhandledInput(InputEvent inputEvent)
    {
        if (inputEvent is not InputEventKey keyEvent) return;
        if (!keyEvent.Pressed || keyEvent.Echo) return;

        var entry = ModEntry.Instance;
        if (entry == null) return;

        if (keyEvent.Keycode == entry.Config.GetToggleKey())
        {
            entry.AutoPlayer.Toggle();
        }
    }
}
