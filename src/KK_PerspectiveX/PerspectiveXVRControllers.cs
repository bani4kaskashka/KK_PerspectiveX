using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PerspectiveX
{
    public enum VrFollowButton
    {
        [Description("None")] None,
        [Description("Trigger")] Trigger,
        [Description("Grip")] Grip,
        [Description("Touchpad / Thumbstick press")] Touchpad,
        [Description("Application menu")] ApplicationMenu,
        [Description("A (right hand) / X (left hand)")] A,
        [Description("B (right hand) / Y (left hand)")] B
    }

    // the names end up in the config file, so don't rename them
    public enum VrMappedKey
    {
        [Description("None")] None,
        [Description("Left mouse button")] LButton,
        [Description("Right mouse button")] RButton,
        [Description("Middle mouse button")] MButton,
        [Description("Mouse wheel scroll up")] ScrollUp,
        [Description("Mouse wheel scroll down")] ScrollDown,
        [Description("Keyboard Tab")] Tab,
        [Description("Keyboard Enter")] Return,
        [Description("Keyboard Esc")] Escape,
        [Description("Keyboard Space")] Space,
        [Description("Keyboard Home")] Home,
        [Description("Keyboard End")] End,
        [Description("Keyboard arrow left")] Left,
        [Description("Keyboard arrow up")] Up,
        [Description("Keyboard arrow right")] Right,
        [Description("Keyboard arrow down")] Down,
        [Description("Keyboard Ins")] Insert,
        [Description("Keyboard Del")] Delete,
        [Description("Keyboard Page Up")] Prior,
        [Description("Keyboard Page Down")] Next,
        [Description("Keyboard Backspace")] Back,
        [Description("Keyboard Shift")] Shift,
        [Description("Keyboard Ctrl")] Control,
        [Description("Keyboard Alt")] Menu,
        [Description("Keyboard Pause")] Pause,
        [Description("Keyboard F1")] F1,
        [Description("Keyboard F2")] F2,
        [Description("Keyboard F3")] F3,
        [Description("Keyboard F4")] F4,
        [Description("Keyboard F5")] F5,
        [Description("Keyboard F6")] F6,
        [Description("Keyboard F7")] F7,
        [Description("Keyboard F8")] F8,
        [Description("Keyboard F9")] F9,
        [Description("Keyboard F10")] F10,
        [Description("Keyboard F11")] F11,
        [Description("Keyboard F12")] F12,
        [Description("Keyboard A")] A,
        [Description("Keyboard B")] B,
        [Description("Keyboard C")] C,
        [Description("Keyboard D")] D,
        [Description("Keyboard E")] E,
        [Description("Keyboard F")] F,
        [Description("Keyboard G")] G,
        [Description("Keyboard H")] H,
        [Description("Keyboard I")] I,
        [Description("Keyboard J")] J,
        [Description("Keyboard K")] K,
        [Description("Keyboard L")] L,
        [Description("Keyboard M")] M,
        [Description("Keyboard N")] N,
        [Description("Keyboard O")] O,
        [Description("Keyboard P")] P,
        [Description("Keyboard Q")] Q,
        [Description("Keyboard R")] R,
        [Description("Keyboard S")] S,
        [Description("Keyboard T")] T,
        [Description("Keyboard U")] U,
        [Description("Keyboard V")] V,
        [Description("Keyboard W")] W,
        [Description("Keyboard X")] X,
        [Description("Keyboard Y")] Y,
        [Description("Keyboard Z")] Z,
        [Description("Keyboard 0")] D0,
        [Description("Keyboard 1")] D1,
        [Description("Keyboard 2")] D2,
        [Description("Keyboard 3")] D3,
        [Description("Keyboard 4")] D4,
        [Description("Keyboard 5")] D5,
        [Description("Keyboard 6")] D6,
        [Description("Keyboard 7")] D7,
        [Description("Keyboard 8")] D8,
        [Description("Keyboard 9")] D9,
        [Description("Keyboard Numpad 0")] NumPad0,
        [Description("Keyboard Numpad 1")] NumPad1,
        [Description("Keyboard Numpad 2")] NumPad2,
        [Description("Keyboard Numpad 3")] NumPad3,
        [Description("Keyboard Numpad 4")] NumPad4,
        [Description("Keyboard Numpad 5")] NumPad5,
        [Description("Keyboard Numpad 6")] NumPad6,
        [Description("Keyboard Numpad 7")] NumPad7,
        [Description("Keyboard Numpad 8")] NumPad8,
        [Description("Keyboard Numpad 9")] NumPad9
    }

    // VR controller mapping (by HanYuanyi): controller buttons that press a keyboard key or mouse
    // button, so toggling POV, switching characters and anything else on a hotkey works from the
    // controllers. The buttons are read from SteamVR's OpenVR API, by reflection like the rest of the
    // VR code, so on anything that isn't running through OpenVR this simply finds nothing and idles.
    public partial class PerspectiveXPlugin
    {
        // OpenVR's button bits (EVRButtonId)
        private const ulong VrButtonAppMenu = 1UL << 1;
        private const ulong VrButtonGrip = 1UL << 2;
        private const ulong VrButtonA = 1UL << 7;
        private const ulong VrButtonTouchpad = 1UL << 32;
        private const ulong VrButtonTrigger = 1UL << 33;
        // device 0 is the headset, controllers come after it
        private const uint VrMaxDevices = 16;
        // the main game's H scene, same name GCSuspend checks for
        private const string HSceneName = "HProc";

        private enum VrPad { None, Up, Down, Left, Right, Center }

        private static readonly string[] VrMapNames = { "Trigger", "Grip", "Up", "Down", "Left", "Right", "Center" };
        private static readonly KeyCode[] ModifierKeys =
        {
            KeyCode.LeftShift, KeyCode.RightShift, KeyCode.LeftControl, KeyCode.RightControl,
            KeyCode.LeftAlt, KeyCode.RightAlt, KeyCode.LeftCommand, KeyCode.RightCommand
        };

        private ConfigEntry<VrFollowButton> VrFollowCycleButton { get; set; }
        private ConfigEntry<VrMappedKey>[] vrKeyMaps;
        // what each binding is currently holding down, so it gets released even if the setting
        // changes in the middle of a press
        private readonly VrMappedKey[] vrKeyHeld = new VrMappedKey[VrMapNames.Length];
        private readonly bool[] vrKeyInjected = new bool[VrMapNames.Length];

        private object openVrSystem;
        private Type openVrType;
        private bool openVrSearched;
        private float nextOpenVrSearch;
        private MethodInfo getControllerState;
        private object[] controllerStateArgs;
        private FieldInfo buttonPressedField;
        private FieldInfo padAxisField;
        private FieldInfo padXField;
        private FieldInfo padYField;
        private ulong vrButtons;
        private ulong vrButtonsPrev;
        private VrPad vrPad;

        private bool appFocused = true;
        // keys pressed from a controller, see SendMappedKey
        private readonly HashSet<KeyCode> virtualHeldKeys = new HashSet<KeyCode>();
        private readonly HashSet<KeyCode> virtualDownKeys = new HashSet<KeyCode>();
        // when each key was last sent through Windows, see IsShortcutDown
        private readonly Dictionary<KeyCode, float> injectedKeyTimes = new Dictionary<KeyCode, float>();

        private void BindVrControllerSettings()
        {
            VrFollowCycleButton = Config.Bind("VR controller mapping", "Rotation follow button", VrFollowButton.None,
                new ConfigDescription("VR only. Optional controller button that cycles rotation follow, same as its key under Keyboard shortcuts. Works on either hand.",
                    null, new ConfigurationManagerAttributes { Order = 10 }));

            string[] info =
            {
                "Press this key or mouse button while the trigger is held. Works on either hand. The VR mod still sees the trigger too.",
                "Press this key or mouse button while the grip is held. Works on either hand. The VR mod still sees the grip too.",
                "Press this key or mouse button while the touchpad / thumbstick is pressed down in the up direction.",
                "Press this key or mouse button while the touchpad / thumbstick is pressed down in the down direction.",
                "Press this key or mouse button while the touchpad / thumbstick is pressed down in the left direction.",
                "Press this key or mouse button while the touchpad / thumbstick is pressed down in the right direction.",
                "Press this key or mouse button while the touchpad / thumbstick is pressed down in the middle."
            };
            vrKeyMaps = new ConfigEntry<VrMappedKey>[VrMapNames.Length];
            for (int i = 0; i < VrMapNames.Length; i++)
            {
                // out of the box: touchpad down cycles rotation follow (Insert = its default hotkey),
                // touchpad left presses Backspace (= the default POV toggle key)
                VrMappedKey def = VrMapNames[i] == "Down" ? VrMappedKey.Insert
                    : VrMapNames[i] == "Left" ? VrMappedKey.Back
                    : VrMappedKey.None;
                vrKeyMaps[i] = Config.Bind("VR controller mapping", VrMapNames[i], def,
                    new ConfigDescription(info[i] + " In the main game this only works in H scenes.", null, new ConfigurationManagerAttributes { Order = 9 - i }));
            }
        }

        private void OnApplicationFocus(bool focus)
        {
            appFocused = focus;
        }

        // Runs first thing every frame, before the hotkeys are read.
        private void UpdateVrControllers()
        {
            vrButtonsPrev = vrButtons;
            vrButtons = 0;
            vrPad = VrPad.None;
            bool vr = IsVrEnabled() && FindVrRig(false) && FindOpenVr();
            if (vr)
                ReadVrControllers();

            // The rotation-follow button above keeps working either way (it drives POV state
            // directly, no key involved). Only the key injection is gated, because that is the
            // half that can collide with the VR mod's own mapping.
            //
            // `vr` first so the scene lookup behind VrKeyMappingActive() doesn't run every frame
            // in flat-screen play, where the controller mapping cannot do anything anyway.
            if (!vr || !VrKeyMappingActive())
            {
                ReleaseVrMappedKeys();
                return;
            }

            for (int i = 0; i < vrKeyMaps.Length; i++)
                SetMappedKey(i, IsVrBindingActive(i) ? vrKeyMaps[i].Value : VrMappedKey.None);
        }

        // Studio always. In the main game only once the H scene is actually loaded: KK_MainGameVR
        // maps the controller to keys there too, and two plugins injecting a key for the same
        // physical button is exactly how one press ends up firing two different keys (its "2"
        // toggles the game's own look tracking, ours sends Backspace). Same detection GCSuspend
        // uses - the H scene is the loaded scene named "HProc".
        private bool VrKeyMappingActive()
        {
            if (isStudio)
                return true;
            try
            {
                return SceneManager.GetSceneByName(HSceneName).isLoaded;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private bool IsVrBindingActive(int i)
        {
            switch (i)
            {
                case 0: return (vrButtons & VrButtonTrigger) != 0;
                case 1: return (vrButtons & VrButtonGrip) != 0;
                case 2: return vrPad == VrPad.Up;
                case 3: return vrPad == VrPad.Down;
                case 4: return vrPad == VrPad.Left;
                case 5: return vrPad == VrPad.Right;
                default: return vrPad == VrPad.Center;
            }
        }

        private bool VrFollowButtonPressed()
        {
            ulong mask = VrButtonMask(VrFollowCycleButton.Value);
            return mask != 0 && (vrButtons & mask) != 0 && (vrButtonsPrev & mask) == 0;
        }

        private static ulong VrButtonMask(VrFollowButton button)
        {
            switch (button)
            {
                case VrFollowButton.Trigger: return VrButtonTrigger;
                case VrFollowButton.Grip: return VrButtonGrip;
                case VrFollowButton.Touchpad: return VrButtonTouchpad;
                // B / Y is the application menu button as far as OpenVR is concerned
                case VrFollowButton.ApplicationMenu:
                case VrFollowButton.B: return VrButtonAppMenu;
                case VrFollowButton.A: return VrButtonA;
                default: return 0;
            }
        }

        private bool FindOpenVr()
        {
            if (openVrSystem != null)
                return true;
            if (Time.unscaledTime < nextOpenVrSearch)
                return false;
            nextOpenVrSearch = Time.unscaledTime + 1f;
            try
            {
                if (!openVrSearched)
                {
                    // the VR mod is already loaded by the time its rig exists, so one scan is enough
                    openVrSearched = true;
                    foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        openVrType = asm.GetType("Valve.VR.OpenVR", false);
                        if (openVrType != null)
                            break;
                    }
                }
                if (openVrType == null)
                    return false;

                // null until the VR mod has started OpenVR, so this is retried
                object system = openVrType.GetProperty("System", BindingFlags.Public | BindingFlags.Static).GetValue(null, null);
                if (system == null)
                    return false;
                Type stateType = openVrType.Assembly.GetType("Valve.VR.VRControllerState_t", false);
                MethodInfo get = system.GetType().GetMethod("GetControllerState");
                if (stateType == null || get == null)
                    return false;
                buttonPressedField = stateType.GetField("ulButtonPressed");
                if (buttonPressedField == null)
                    return false;
                padAxisField = stateType.GetField("rAxis0");
                if (padAxisField != null)
                {
                    padXField = padAxisField.FieldType.GetField("x");
                    padYField = padAxisField.FieldType.GetField("y");
                }
                // older SteamVR plugins don't take the struct size
                controllerStateArgs = get.GetParameters().Length >= 3
                    ? new object[] { 0u, Activator.CreateInstance(stateType), (uint)Marshal.SizeOf(stateType) }
                    : new object[] { 0u, Activator.CreateInstance(stateType) };
                getControllerState = get;
                openVrSystem = system;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void ReadVrControllers()
        {
            try
            {
                for (uint i = 1; i < VrMaxDevices; i++)
                {
                    controllerStateArgs[0] = i;
                    if (!(bool)getControllerState.Invoke(openVrSystem, controllerStateArgs))
                        continue;
                    object state = controllerStateArgs[1];
                    ulong pressed = (ulong)buttonPressedField.GetValue(state);
                    vrButtons |= pressed;
                    if ((pressed & VrButtonTouchpad) != 0)
                    {
                        // a direction on either hand wins over the other one's middle press
                        VrPad pad = ReadPad(state);
                        if (pad != VrPad.Center || vrPad == VrPad.None)
                            vrPad = pad;
                    }
                }
            }
            catch (Exception)
            {
                // the API went away under us (VR shut down), look for it again later
                openVrSystem = null;
            }
        }

        private VrPad ReadPad(object state)
        {
            if (padAxisField == null || padXField == null || padYField == null)
                return VrPad.Center;
            object axis = padAxisField.GetValue(state);
            float x = (float)padXField.GetValue(axis);
            float y = (float)padYField.GetValue(axis);
            if (x * x + y * y < 0.25f)
                return VrPad.Center;
            if (Mathf.Abs(y) > Mathf.Abs(x))
                return y > 0f ? VrPad.Up : VrPad.Down;
            return x > 0f ? VrPad.Right : VrPad.Left;
        }

        private void SetMappedKey(int binding, VrMappedKey key)
        {
            if (key == vrKeyHeld[binding])
                return;
            if (vrKeyHeld[binding] != VrMappedKey.None)
                SendMappedKey(binding, vrKeyHeld[binding], false);
            vrKeyHeld[binding] = key;
            if (key != VrMappedKey.None)
                SendMappedKey(binding, key, true);
        }

        private void ReleaseVrMappedKeys()
        {
            for (int i = 0; i < vrKeyHeld.Length; i++)
                SetMappedKey(i, VrMappedKey.None);
        }

        // With the game window focused the press goes through Windows, so the game and every other
        // mod see a real key. Without focus it would land in whatever window has it, so nothing is
        // sent and the key only counts for our own shortcuts (IsShortcutDown).
        private void SendMappedKey(int binding, VrMappedKey key, bool down)
        {
            if (down)
                vrKeyInjected[binding] = appFocused;
            bool inject = vrKeyInjected[binding];

            switch (key)
            {
                case VrMappedKey.LButton:
                    if (inject) mouse_event(down ? MouseLeftDown : MouseLeftUp, 0, 0, 0, UIntPtr.Zero);
                    return;
                case VrMappedKey.RButton:
                    if (inject) mouse_event(down ? MouseRightDown : MouseRightUp, 0, 0, 0, UIntPtr.Zero);
                    return;
                case VrMappedKey.MButton:
                    if (inject) mouse_event(down ? MouseMiddleDown : MouseMiddleUp, 0, 0, 0, UIntPtr.Zero);
                    return;
                case VrMappedKey.ScrollUp:
                case VrMappedKey.ScrollDown:
                    if (inject && down)
                        mouse_event(MouseWheel, 0, 0, unchecked((uint)(key == VrMappedKey.ScrollUp ? WheelDelta : -WheelDelta)), UIntPtr.Zero);
                    return;
            }

            if (!GetMappedKeyCodes(key, out byte vk, out KeyCode code, out bool extended))
                return;
            if (inject)
            {
                uint flags = (down ? 0u : KeyUp) | (extended ? KeyExtended : 0u);
                keybd_event(vk, (byte)MapVirtualKey(vk, 0), flags, UIntPtr.Zero);
                if (down)
                    injectedKeyTimes[code] = Time.unscaledTime;
            }
            // Register the virtual key whether or not we injected (this is what 1.3.5 did).
            // The injection happens inside our own Update, so Unity's input snapshot for this
            // frame is already fixed and our own shortcuts cannot see the injected key - the
            // virtual key is what keeps them working.
            if (down)
            {
                virtualHeldKeys.Add(code);
                virtualDownKeys.Add(code);
            }
            else
            {
                virtualHeldKeys.Remove(code);
            }
        }

        // KeyboardShortcut.IsDown, plus keys pressed from a controller while the game wasn't focused
        private bool IsShortcutDown(KeyboardShortcut shortcut)
        {
            if (shortcut.IsDown())
            {
                // if Unity does pick up a key we sent through Windows it shows up a frame or two
                // later, and the virtual press already counted it, so don't fire twice (toggling
                // POV on and straight back off)
                return !injectedKeyTimes.TryGetValue(shortcut.MainKey, out float sentAt) || Time.unscaledTime - sentAt > 0.25f;
            }
            if (virtualDownKeys.Count == 0 || !virtualDownKeys.Contains(shortcut.MainKey))
                return false;
            foreach (KeyCode m in shortcut.Modifiers)
                if (!IsKeyHeld(m))
                    return false;
            // same rule as KeyboardShortcut: an extra modifier held makes it a different shortcut
            for (int i = 0; i < ModifierKeys.Length; i++)
                if (IsKeyHeld(ModifierKeys[i]) && !shortcut.Modifiers.Contains(ModifierKeys[i]))
                    return false;
            return true;
        }

        private bool IsKeyHeld(KeyCode k)
        {
            return Input.GetKey(k) || virtualHeldKeys.Contains(k);
        }

        private static bool GetMappedKeyCodes(VrMappedKey key, out byte vk, out KeyCode code, out bool extended)
        {
            extended = false;
            if (key >= VrMappedKey.A && key <= VrMappedKey.Z)
            {
                int n = key - VrMappedKey.A;
                vk = (byte)(0x41 + n);
                code = KeyCode.A + n;
                return true;
            }
            if (key >= VrMappedKey.D0 && key <= VrMappedKey.D9)
            {
                int n = key - VrMappedKey.D0;
                vk = (byte)(0x30 + n);
                code = KeyCode.Alpha0 + n;
                return true;
            }
            if (key >= VrMappedKey.NumPad0 && key <= VrMappedKey.NumPad9)
            {
                int n = key - VrMappedKey.NumPad0;
                vk = (byte)(0x60 + n);
                code = KeyCode.Keypad0 + n;
                return true;
            }
            if (key >= VrMappedKey.F1 && key <= VrMappedKey.F12)
            {
                int n = key - VrMappedKey.F1;
                vk = (byte)(0x70 + n);
                code = KeyCode.F1 + n;
                return true;
            }

            // the arrows and the Ins/Del/Home/End/Page block are "extended" keys; without the flag
            // Windows reads them as the numpad keys of the same name
            extended = true;
            switch (key)
            {
                case VrMappedKey.Left: vk = 0x25; code = KeyCode.LeftArrow; return true;
                case VrMappedKey.Up: vk = 0x26; code = KeyCode.UpArrow; return true;
                case VrMappedKey.Right: vk = 0x27; code = KeyCode.RightArrow; return true;
                case VrMappedKey.Down: vk = 0x28; code = KeyCode.DownArrow; return true;
                case VrMappedKey.Home: vk = 0x24; code = KeyCode.Home; return true;
                case VrMappedKey.End: vk = 0x23; code = KeyCode.End; return true;
                case VrMappedKey.Insert: vk = 0x2D; code = KeyCode.Insert; return true;
                case VrMappedKey.Delete: vk = 0x2E; code = KeyCode.Delete; return true;
                case VrMappedKey.Prior: vk = 0x21; code = KeyCode.PageUp; return true;
                case VrMappedKey.Next: vk = 0x22; code = KeyCode.PageDown; return true;
            }
            extended = false;
            switch (key)
            {
                case VrMappedKey.Tab: vk = 0x09; code = KeyCode.Tab; return true;
                case VrMappedKey.Return: vk = 0x0D; code = KeyCode.Return; return true;
                case VrMappedKey.Escape: vk = 0x1B; code = KeyCode.Escape; return true;
                case VrMappedKey.Space: vk = 0x20; code = KeyCode.Space; return true;
                case VrMappedKey.Back: vk = 0x08; code = KeyCode.Backspace; return true;
                case VrMappedKey.Shift: vk = 0x10; code = KeyCode.LeftShift; return true;
                case VrMappedKey.Control: vk = 0x11; code = KeyCode.LeftControl; return true;
                case VrMappedKey.Menu: vk = 0x12; code = KeyCode.LeftAlt; return true;
                case VrMappedKey.Pause: vk = 0x13; code = KeyCode.Pause; return true;
            }
            vk = 0;
            code = KeyCode.None;
            return false;
        }

        private const uint KeyExtended = 0x1;
        private const uint KeyUp = 0x2;
        private const uint MouseLeftDown = 0x2;
        private const uint MouseLeftUp = 0x4;
        private const uint MouseRightDown = 0x8;
        private const uint MouseRightUp = 0x10;
        private const uint MouseMiddleDown = 0x20;
        private const uint MouseMiddleUp = 0x40;
        private const uint MouseWheel = 0x800;
        private const int WheelDelta = 120;

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint uCode, uint uMapType);
    }
}
