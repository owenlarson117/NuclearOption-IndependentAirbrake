using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Rewired;
using UnityEngine;
using TMPro;
using System.Collections.Generic;

// IndependentAirbrake — Nuclear Option
// Vanilla deploys the airbrake only while throttle == 0 (pull past idle). This mod gives the
// airbrake its own control (keyboard key and/or any joystick button) so it works at any throttle.
[BepInPlugin("com.local.independentairbrake", "IndependentAirbrake", "1.0.0")]
public class AirbrakePlugin : BaseUnityPlugin
{
    internal static ManualLogSource Log;

    internal static ConfigEntry<bool> cfgEnabled;
    internal static ConfigEntry<string> cfgMode;
    internal static ConfigEntry<bool> cfgIdleAirbrake;
    internal static ConfigEntry<KeyboardShortcut> cfgKey;
    internal static ConfigEntry<string> cfgJoyName;
    internal static ConfigEntry<int> cfgJoyButton;
    internal static ConfigEntry<KeyboardShortcut> cfgBindKey;

    // Current airbrake request from the player's dedicated control.
    internal static bool brakeRequested;
    // True while the local aircraft's airbrake is actually (partly) open.
    internal static bool localBrakeOpen;
    internal static ConfigEntry<bool> cfgHudText;

    void Awake()
    {
        Log = Logger;

        cfgEnabled = Config.Bind("General", "Enabled", true, "Enable the independent airbrake control");
        cfgMode = Config.Bind("General", "Mode", "Hold",
            new ConfigDescription("Hold = airbrake out while the control is held. Toggle = press to open, press again to close.",
                new AcceptableValueList<string>("Hold", "Toggle")));
        cfgIdleAirbrake = Config.Bind("General", "AirbrakeAtIdleThrottle", false,
            "Also deploy the airbrake when throttle is at 0 (vanilla behaviour). false = airbrake only from its own control.");

        cfgHudText = Config.Bind("General", "ShowHudText", true,
            "Show AIRBRAKE in the HUD throttle gauge (where the throttle % is) while the airbrake is deployed");

        cfgKey = Config.Bind("Controls", "AirbrakeKey", new KeyboardShortcut(KeyCode.None),
            "Keyboard key for the airbrake (optional)");
        cfgJoyName = Config.Bind("Controls", "JoystickName", "",
            "Joystick (partial name, case-insensitive) that has the airbrake button. Set automatically by the bind key.");
        cfgJoyButton = Config.Bind("Controls", "JoystickButton", -1,
            "0-based button index on that joystick. -1 = no joystick button. Set automatically by the bind key.");
        cfgBindKey = Config.Bind("Controls", "BindJoystickButtonKey", new KeyboardShortcut(KeyCode.F8),
            "Press this, then press the joystick button you want to use for the airbrake (Esc cancels).");

        var harmony = new Harmony("com.local.independentairbrake");
        harmony.PatchAll();

        var go = new GameObject("IndependentAirbrake_Runtime");
        DontDestroyOnLoad(go);
        go.hideFlags = HideFlags.HideAndDontSave;
        go.AddComponent<AirbrakeRunner>();

        Logger.LogInfo("IndependentAirbrake loaded.");
    }

    internal static Aircraft GetLocalAircraft()
    {
        try { return GameManager.GetLocalAircraft(out var ac) ? ac : null; }
        catch { return null; }
    }

    // Mark the game version so modded clients only match with each other in multiplayer
    // (same approach SplitThrottle uses).
    [HarmonyPatch(typeof(Application), "get_version")]
    static class VersionPatch
    {
        const string MARKER = "+IndependentAirbrake";
        static void Postfix(ref string __result)
        {
            if (__result != null && !__result.Contains(MARKER)) __result += MARKER;
        }
    }

    // Airbrake.Update opens the brake while controlInputs.throttle == 0 and closes it otherwise.
    // For the local player's aircraft, feed it the dedicated airbrake control instead, restoring
    // the real throttle right after so nothing else sees the change.
    [HarmonyPatch(typeof(Airbrake), "Update")]
    static class AirbrakeUpdatePatch
    {
        static readonly FieldInfo f_inputs = AccessTools.Field(typeof(Airbrake), "controlInputs");
        static readonly FieldInfo f_aircraft = AccessTools.Field(typeof(Airbrake), "attachedAircraft");
        static readonly FieldInfo f_open = AccessTools.Field(typeof(Airbrake), "openAmount");
        static bool loggedOpen;

        static void Prefix(Airbrake __instance, out float __state)
        {
            __state = float.NaN;
            if (!cfgEnabled.Value) return;
            try
            {
                var ac = f_aircraft.GetValue(__instance) as Aircraft;
                if (ac == null || ac != GetLocalAircraft()) return;
                if (!(f_inputs.GetValue(__instance) is ControlInputs inputs)) return;

                __state = inputs.throttle;
                bool idle = inputs.throttle == 0f && cfgIdleAirbrake.Value;
                // 0 => brake opens; any non-zero value => brake closes.
                inputs.throttle = (brakeRequested || idle) ? 0f : 1f;
            }
            catch { __state = float.NaN; }
        }

        static void Postfix(Airbrake __instance, float __state)
        {
            if (float.IsNaN(__state)) return;
            try
            {
                if (f_inputs.GetValue(__instance) is ControlInputs inputs) inputs.throttle = __state;
                bool open = (float)f_open.GetValue(__instance) > 0f;
                localBrakeOpen = open;
                if (open != loggedOpen)
                {
                    loggedOpen = open;
                    Log.LogInfo(open ? "Airbrake deploying" : "Airbrake retracted");
                }
            }
            catch { }
        }
    }

    // Vanilla only writes "AIRBRAKE" into the throttle gauge when throttle == 0. Show it whenever
    // the local airbrake is actually open, and restore the normal reading once it closes.
    [HarmonyPatch(typeof(ThrottleGauge), "Refresh")]
    [HarmonyPriority(Priority.Last)]
    static class ThrottleGaugeAirbrakeText
    {
        static readonly FieldInfo f_reading = AccessTools.Field(typeof(ThrottleGauge), "throttleReading");
        static readonly FieldInfo f_aircraft = AccessTools.Field(typeof(ThrottleGauge), "aircraft");
        static readonly FieldInfo f_prev = AccessTools.Field(typeof(ThrottleGauge), "throttlePrev");
        static readonly HashSet<ThrottleGauge> showing = new HashSet<ThrottleGauge>();

        static void Postfix(ThrottleGauge __instance)
        {
            try
            {
                var ac = f_aircraft.GetValue(__instance) as Aircraft;
                bool want = cfgEnabled.Value && cfgHudText.Value && localBrakeOpen
                            && ac != null && ac == GetLocalAircraft();
                if (want)
                {
                    if (f_reading.GetValue(__instance) is TextMeshProUGUI txt && txt.text != "AIRBRAKE")
                        txt.text = "AIRBRAKE";
                    showing.Add(__instance);
                }
                else if (showing.Remove(__instance))
                {
                    f_prev.SetValue(__instance, -1f); // force the gauge to redraw its normal text next frame
                }
            }
            catch { }
        }
    }
}

public class AirbrakeRunner : MonoBehaviour
{
    bool binding;
    float bindingStarted;
    string bindMessage;
    float bindMessageUntil;
    bool toggleState;
    bool prevPressed;
    Aircraft lastAircraft;

    void Update()
    {
        try
        {
            if (AirbrakePlugin.cfgBindKey.Value.IsDown() && !binding)
            {
                binding = true;
                bindingStarted = Time.unscaledTime;
            }
            if (binding) { PollBinding(); AirbrakePlugin.brakeRequested = false; return; }

            var ac = AirbrakePlugin.GetLocalAircraft();
            if (ac != lastAircraft) { lastAircraft = ac; toggleState = false; prevPressed = false; AirbrakePlugin.localBrakeOpen = false; }

            bool pressed = KeyHeld() || JoyHeld();
            if (AirbrakePlugin.cfgMode.Value == "Toggle")
            {
                if (pressed && !prevPressed) toggleState = !toggleState;
                AirbrakePlugin.brakeRequested = toggleState;
            }
            else
            {
                AirbrakePlugin.brakeRequested = pressed;
            }
            prevPressed = pressed;
        }
        catch (Exception e)
        {
            AirbrakePlugin.Log?.LogWarning($"Airbrake input error: {e.Message}");
        }
    }

    static bool KeyHeld()
    {
        var k = AirbrakePlugin.cfgKey.Value;
        return k.MainKey != KeyCode.None && k.IsPressed();
    }

    static bool JoyHeld()
    {
        int idx = AirbrakePlugin.cfgJoyButton.Value;
        string name = AirbrakePlugin.cfgJoyName.Value;
        if (idx < 0 || string.IsNullOrEmpty(name) || !ReInput.isReady) return false;
        foreach (var j in ReInput.controllers.Joysticks)
        {
            if (j == null || j.name == null) continue;
            if (j.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (idx < j.buttonCount && j.GetButton(idx)) return true;
        }
        return false;
    }

    void PollBinding()
    {
        if (Input.GetKeyDown(KeyCode.Escape) || Time.unscaledTime - bindingStarted > 10f)
        {
            binding = false;
            Flash("Airbrake bind cancelled");
            return;
        }
        if (!ReInput.isReady) return;
        foreach (var j in ReInput.controllers.Joysticks)
        {
            if (j == null) continue;
            for (int b = 0; b < j.buttonCount; b++)
            {
                if (!j.GetButtonDown(b)) continue;
                AirbrakePlugin.cfgJoyName.Value = j.name;
                AirbrakePlugin.cfgJoyButton.Value = b;
                binding = false;
                Flash($"Airbrake bound to {j.name} button {b}");
                AirbrakePlugin.Log.LogInfo($"Airbrake bound to '{j.name}' button {b}");
                return;
            }
        }
    }

    void Flash(string msg)
    {
        bindMessage = msg;
        bindMessageUntil = Time.unscaledTime + 3f;
    }

    void OnGUI()
    {
        string msg = null;
        if (binding)
            msg = $"Press the joystick button for AIRBRAKE...  (Esc to cancel, {10f - (Time.unscaledTime - bindingStarted):0}s)";
        else if (bindMessage != null && Time.unscaledTime < bindMessageUntil)
            msg = bindMessage;
        if (msg == null) return;

        var style = new GUIStyle(GUI.skin.box) { fontSize = 16, alignment = TextAnchor.MiddleCenter };
        float w = 560, h = 40;
        GUI.Box(new Rect((Screen.width - w) / 2f, Screen.height * 0.2f, w, h), msg, style);
    }
}
