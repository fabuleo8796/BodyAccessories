using System;
using System.Collections.Generic;
using UnityEngine.InputSystem;

namespace BodyAccessories;

/// Switches the game's controls off while the panel is open, so typing and clicking don't flip the phone or open
/// the game's menus (StickerSwap's GameInputPause, as in Tripod Cam). Controls come back once Enter, Esc, the left
/// mouse button and the panel key are let go, so the closing press never leaks into the game.
internal static class GameControls
{
    static readonly List<InputAction> paused = new();
    static bool waitingForRelease;

    public static bool Active { get; private set; }

    public static void Pause()
    {
        if (Active && !waitingForRelease) return;
        waitingForRelease = false;
        Active = true;
        try
        {
            var enabled = new Il2CppSystem.Collections.Generic.List<InputAction>();
            InputSystem.ListEnabledActions(enabled);
            for (var i = 0; i < enabled.Count; i++)
            {
                var action = enabled[i];
                if (action == null || IsPointer(action)) continue;
                action.Disable();
                paused.Add(action);
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Couldn't pause the game's controls: {e.Message}");
        }
    }

    public static void Resume()
    {
        if (Active) waitingForRelease = true;
    }

    public static void Tick(Keyboard keyboard, Mouse mouse)
    {
        if (!waitingForRelease) return;
        if (keyboard != null && (keyboard.enterKey.isPressed || keyboard.numpadEnterKey.isPressed || keyboard.escapeKey.isPressed
                                 || keyboard[Plugin.OpenKey.Value].isPressed)) return;
        if (mouse != null && mouse.leftButton.isPressed) return;
        foreach (var action in paused)
        {
            try { action?.Enable(); } catch { /* the action's owner may be gone */ }
        }
        paused.Clear();
        waitingForRelease = false;
        Active = false;
    }

    /// The UI's pointing, clicking and scrolling stay on; everything else pauses.
    static bool IsPointer(InputAction action)
    {
        var map = action.actionMap;
        if (map != null && map.name != "UI") return false;
        var name = (action.name ?? "").ToLowerInvariant();
        return name.Contains("point") || name.Contains("click") || name.Contains("scroll") || name.Contains("tracked");
    }
}
