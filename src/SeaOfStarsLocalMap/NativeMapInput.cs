using System;
using Rewired;
using UnityEngine;

namespace SeaOfStarsLocalMap;

/// <summary>One cached input sample shared by the native HUD prefix and the overlay Update.</summary>
internal readonly struct NativeMapInputFrame
{
    public readonly bool ToggleRequested, CloseRequested, RecenterRequested, ControllerActive;
    public readonly bool ToggleHeld, CloseHeld;
    public readonly float ZoomAxis;
    public readonly Vector2 PanVector;

    public NativeMapInputFrame(bool toggle, bool close, bool recenter, bool controllerActive,
        bool toggleHeld, bool closeHeld, float zoomAxis, Vector2 panVector)
    {
        ToggleRequested = toggle; CloseRequested = close; RecenterRequested = recenter;
        ControllerActive = controllerActive; ToggleHeld = toggleHeld; CloseHeld = closeHeld;
        ZoomAxis = zoomAxis; PanVector = panVector;
    }
}

/// <summary>
/// Uses the game's remappable HUD/back actions and Rewired's platform-neutral gamepad template.
/// It does not change controller maps, consume game inputs, toggle UI, or install patches.
/// </summary>
internal static class NativeMapInput
{
    public const string ToggleAction = "UI_PingHud"; // Installed Rewired action 87, UI category 2.
    public const string BackAction = "UI_Back";      // Installed Rewired action 6, UI category 2.
    private const float StickDeadZone = .18f;
    private const float TriggerDeadZone = .08f;
    private static int sampledFrame = -1;
    private static NativeMapInputFrame current;
    private static Player openingPlayer;
    private static bool loggedActionFailure, loggedTemplateFailure;

    public static Player OpeningPlayer => openingPlayer;

    /// <summary>
    /// ToggleRequested is shared, not consumed: the integration must handle a toggle only once
    /// per Time.frameCount if both InGameHud.ProcessInputs and the overlay Update call Read().
    /// Keyboard M and mouse controls are intentionally handled by the overlay.
    /// </summary>
    public static NativeMapInputFrame Read()
    {
        if (sampledFrame == Time.frameCount) return current;
        sampledFrame = Time.frameCount;
        bool toggle = false, close = false, recenter = false, controllerActive = false;
        bool toggleHeld = false, closeHeld = false;
        float zoom = 0f;
        Vector2 pan = Vector2.zero;
        var manager = global::InputManager.Instance;
        if (manager == null) return current = default;
        Player player = openingPlayer ?? manager.FirstPlayer;
        try
        {
            // This is the same party-aware query used by InGameHud.ProcessInputs.
            var party = SinglePlayerPlusManager.Instance;
            Player togglingPlayer;
            if (party != null && party.GetButtonDown<UIInputs>(ToggleAction, out togglingPlayer))
            {
                toggle = true;
                if (togglingPlayer != null) openingPlayer = player = togglingPlayer;
            }
            else if (party == null && player != null && player.GetButtonDown(ToggleAction))
            {
                toggle = true;
                openingPlayer = player;
            }
            if (player != null)
            {
                toggleHeld = player.GetButton(ToggleAction);
                close = player.GetButtonDown(BackAction);
                closeHeld = player.GetButton(BackAction);
                var last = manager.GetLastUsedController(player);
                controllerActive = last != null && last.type == ControllerType.Joystick;
            }
        }
        catch (Exception error)
        {
            if (!loggedActionFailure)
            {
                StaticMapPlugin.Logger.LogWarning("Native map action input: " + error.Message);
                loggedActionFailure = true;
            }
        }
        try
        {
            if (player != null)
            {
                // Query the opening player's controller, never an unrelated global joystick.
                var controller = player.controllers.GetLastActiveController(ControllerType.Joystick);
                if (controller == null && player.controllers.joystickCount > 0)
                    controller = player.controllers.Joysticks[0];
                var pad = controller?.GetTemplate<IGamepadTemplate>();
                if (pad != null)
                {
                    var stick = pad.rightStick;
                    if (stick != null)
                    {
                        pan = ApplyStickDeadZone(stick.value);
                        recenter = stick.press != null && stick.press.justPressed;
                    }
                    float left = pad.leftTrigger != null ? ApplyTriggerDeadZone(pad.leftTrigger.value) : 0f;
                    float right = pad.rightTrigger != null ? ApplyTriggerDeadZone(pad.rightTrigger.value) : 0f;
                    zoom = right - left;
                    if (pan.sqrMagnitude > 0f || zoom != 0f || recenter) controllerActive = true;
                }
            }
        }
        catch (Exception error)
        {
            if (!loggedTemplateFailure)
            {
                StaticMapPlugin.Logger.LogWarning("Native map gamepad template: " + error.Message);
                loggedTemplateFailure = true;
            }
        }
        return current = new NativeMapInputFrame(toggle, close, recenter, controllerActive,
            toggleHeld, closeHeld, zoom, pan);
    }

    /// <summary>Call on a level/save change or when choosing a new keyboard-opened map session.</summary>
    public static void ResetPlayer()
    {
        openingPlayer = null;
        // Keep the current frame sample intact so a release/reset cannot re-read the same press.
    }

    private static Vector2 ApplyStickDeadZone(Vector2 value)
    {
        float length = value.magnitude;
        if (!float.IsFinite(length) || length <= StickDeadZone) return Vector2.zero;
        float strength = Math.Min(1f, (length - StickDeadZone) / (1f - StickDeadZone));
        return value / length * strength;
    }

    private static float ApplyTriggerDeadZone(float value)
    {
        if (!float.IsFinite(value) || value <= TriggerDeadZone) return 0f;
        return Math.Min(1f, (value - TriggerDeadZone) / (1f - TriggerDeadZone));
    }
}
