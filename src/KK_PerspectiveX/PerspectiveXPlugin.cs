using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityStandardAssets.ImageEffects;

namespace PerspectiveX
{
    [BepInPlugin(GUID, PluginName, Version)]
    [BepInIncompatibility("keelhauled.realpov")]
    public partial class PerspectiveXPlugin : BaseUnityPlugin
    {
        public const string GUID = "bucky.kk.perspectivex";
        public const string PluginName = "PerspectiveX";
        public const string Version = "1.4.1";

        private ConfigEntry<KeyboardShortcut> ToggleKey { get; set; }
        private ConfigEntry<KeyboardShortcut> CyclePrevKey { get; set; }
        private ConfigEntry<KeyboardShortcut> CycleNextKey { get; set; }
        private ConfigEntry<KeyboardShortcut> FpsModeKey { get; set; }
        private ConfigEntry<KeyboardShortcut> RollLeftKey { get; set; }
        private ConfigEntry<KeyboardShortcut> RollRightKey { get; set; }
        private ConfigEntry<KeyboardShortcut> RollResetKey { get; set; }
        private ConfigEntry<KeyboardShortcut> CamLockKey { get; set; }
        private ConfigEntry<KeyboardShortcut> PanelKey { get; set; }
        private ConfigEntry<float> DefaultFov { get; set; }
        private ConfigEntry<bool> ScrollFovEnabled { get; set; }
        private ConfigEntry<string> ViewPresets { get; set; }
        private ConfigEntry<float> MouseSensitivity { get; set; }
        private ConfigEntry<float> PositionSmoothing { get; set; }
        private ConfigEntry<float> HeadSway { get; set; }
        private ConfigEntry<float> ForwardOffset { get; set; }
        private ConfigEntry<float> UpOffset { get; set; }
        private ConfigEntry<float> PitchLimit { get; set; }
        private ConfigEntry<float> NearClip { get; set; }
        private ConfigEntry<bool> HideHead { get; set; }
        private ConfigEntry<bool> AlignWithBody { get; set; }
        private ConfigEntry<bool> LookRelativeToBody { get; set; }
        private ConfigEntry<bool> CameraShiftDrag { get; set; }
        private ConfigEntry<KeyboardShortcut> ShiftResetKey { get; set; }
        private ConfigEntry<bool> EnableInFreeRoam { get; set; }

        private const int ViewSlotCount = 3;
        private ConfigEntry<KeyboardShortcut>[] saveSlotKeys;
        private ConfigEntry<KeyboardShortcut>[] loadSlotKeys;
        private ConfigEntry<string>[] viewSlots;

        private const int CustomPresetCount = 5;
        private const int PanelWindowId = 0x0BCC1;
        private const string PresetNameControl = "PerspectiveXPresetName";
        // head renderers are re-scanned on this interval so accessories/outfits swapped
        // while POV is active still get hidden
        private const float HeadRescanInterval = 0.5f;
        private const float HiddenHeadScanInterval = 1f;
        // how long the panel may swallow input with a completely frozen cursor before
        // the dead man's switch gives the input back (~3 seconds at 60 fps)
        private const int InputSuppressFrameLimit = 180;
        private ConfigEntry<string>[] customPresets;
        private bool panelVisible;
        private Rect panelRect;
        private bool panelRectInit;
        private string newPresetName = "";
        private bool presetNameFocused;
        private int inputSuppressFrames;
        private Vector3 lastSuppressMousePos;
        private int hiddenHeadChars;
        private float nextHiddenHeadScan;
        private GUIStyle panelHeaderStyle;
        private GUIStyle panelHintStyle;
        private bool panelStylesInit;
        private object toolbarToggle;         // KKAPI ToolbarToggle, held via reflection
        private PropertyInfo toolbarToggleValue;

        private readonly bool isStudio = Paths.ProcessName == "CharaStudio";

        private bool povEnabled;
        private ChaControl chara;
        private List<ChaControl> charaList;
        private HFlag hFlag;

        private Camera cam;
        private MonoBehaviour disabledCameraControl;
        // the game camera's own "don't take input right now" hooks, borrowed while the panel is up
        private MonoBehaviour hookedCameraControl;
        private CameraCondHook noCtrlHook;
        private CameraCondHook zoomHook;
        // main-game free-roam is driven by the bundled StrayTech CameraSystem singleton
        // (a manager object, not a component on the camera), suppressed via reflection so
        // the single DLL keeps working on KKS even if that type isn't present there
        private UnityEngine.Object roamCamSystem;
        private PropertyInfo roamCamShouldUpdate;
        private bool roamCamPrevShouldUpdate;
        // free-roam's own Space-toggled first-person view (ActionGame.CameraStateDefinitionChange),
        // which hides the whole body via chaCtrl.visibleAll; suppressed while our POV owns the camera
        private MonoBehaviour freeRoamViewSwitcher;
        private bool freeRoamViewSwitcherWasEnabled;
        private DepthOfField dof;
        private bool dofChanged;
        private float dofOrigSize;
        private float dofOrigAperture;
        private Vector3 dofOrigFocalPos;
        private float origFov;
        private float origNearClip;
        // renderers we switched to shadows-only to hide the head, with their previous mode
        private readonly Dictionary<Renderer, bool> hiddenRenderers = new Dictionary<Renderer, bool>();
        private readonly List<Renderer> deadRenderers = new List<Renderer>();
        private float nextHeadRescan;
        private bool hiddenHeadNoticeShown;

        private float yaw;
        private float pitch;
        private float fov;
        private float manualRoll;
        private bool camLocked;
        private float bodyRoll;
        private bool bodyRollInit;
        private bool dragging;
        private bool fpsMode;
        // single source of truth for "we asked the game to pin the cursor", see SetCursorLock
        private bool cursorLocked;
        private Vector3 smoothPos;
        private bool smoothPosInit;
        private Quaternion swayBaseline;
        private bool swayInit;
        // right-drag camera shift, level, in the character's facing frame; see ShiftCamera
        private const float CameraShiftSpeed = 0.006f;
        private const float MaxCameraShift = 0.3f;
        private bool panning;
        private Vector3 cameraShift;
        private Transform torsoBone;
        private bool torsoSearched;
        private Quaternion torsoFrame = Quaternion.identity;

        private void Awake()
        {
            ToggleKey = Config.Bind("Keyboard shortcuts", "Toggle POV", new KeyboardShortcut(KeyCode.Backspace));
            CyclePrevKey = Config.Bind("Keyboard shortcuts", "Previous POV character", new KeyboardShortcut(KeyCode.LeftArrow, KeyCode.LeftControl, KeyCode.LeftShift),
                "Switch the POV to the previous character while POV is active.");
            CycleNextKey = Config.Bind("Keyboard shortcuts", "Next POV character", new KeyboardShortcut(KeyCode.RightArrow, KeyCode.LeftControl, KeyCode.LeftShift),
                "Switch the POV to the next character while POV is active.");
            FpsModeKey = Config.Bind("Keyboard shortcuts", "Toggle FPS mouse look", new KeyboardShortcut(KeyCode.L, KeyCode.LeftControl),
                "Optional hands-free mode: locks and hides the cursor so the mouse looks around directly, like an FPS. Press again to get the cursor back. The normal mode is dragging with the left mouse button.");
            RollLeftKey = Config.Bind("Keyboard shortcuts", "Roll camera left", new KeyboardShortcut(KeyCode.Comma),
                "Hold to tilt the camera counterclockwise.");
            RollRightKey = Config.Bind("Keyboard shortcuts", "Roll camera right", new KeyboardShortcut(KeyCode.Period),
                "Hold to tilt the camera clockwise.");
            RollResetKey = Config.Bind("Keyboard shortcuts", "Reset camera roll", new KeyboardShortcut(KeyCode.Slash),
                "Reset the manual camera tilt back to level. In VR it puts you back on the eyes facing where the character faces, and turns rotation follow off.");
            CamLockKey = Config.Bind("Keyboard shortcuts", "Lock camera in place", new KeyboardShortcut(KeyCode.Semicolon),
                "Freeze the camera where it is so it stops following the head, e.g. during caress animations that throw the head around. Looking around still works. Press again to unlock.");
            PanelKey = Config.Bind("Keyboard shortcuts", "Toggle presets panel", new KeyboardShortcut(KeyCode.P, KeyCode.LeftControl),
                "Show/hide the in-game panel with view presets and saved views. In Studio the panel can also be opened with the PerspectiveX button in the bottom-left toolbar.");
            saveSlotKeys = new ConfigEntry<KeyboardShortcut>[ViewSlotCount];
            loadSlotKeys = new ConfigEntry<KeyboardShortcut>[ViewSlotCount];
            viewSlots = new ConfigEntry<string>[ViewSlotCount];
            for (int i = 0; i < ViewSlotCount; i++)
            {
                KeyCode number = KeyCode.Alpha1 + i;
                saveSlotKeys[i] = Config.Bind("Keyboard shortcuts", $"Save view slot {i + 1}", new KeyboardShortcut(number, KeyCode.LeftControl, KeyCode.LeftShift),
                    "Save the current POV view (FOV, look direction, tilt, camera offsets) into this slot. Slots are kept between game sessions.");
                loadSlotKeys[i] = Config.Bind("Keyboard shortcuts", $"Load view slot {i + 1}", new KeyboardShortcut(number, KeyCode.LeftControl),
                    "Recall the view saved in this slot.");
                viewSlots[i] = Config.Bind("Saved views", $"Slot {i + 1}", "",
                    new ConfigDescription("Saved automatically by the save-view-slot shortcuts, no need to edit by hand.",
                        null, new ConfigurationManagerAttributes { IsAdvanced = true }));
            }
            DefaultFov = Config.Bind("General", "Field of view", 60f,
                new ConfigDescription("Vertical FOV. Can also be adjusted with the scroll wheel while in POV.", new AcceptableValueRange<float>(20f, 120f)));
            ScrollFovEnabled = Config.Bind("General", "Scroll wheel adjusts FOV", true,
                "Turn off if you keep changing the FOV by accident while scrolling in POV.");
            ViewPresets = Config.Bind("General", "View presets", "",
                new ConfigDescription("One-click setups for FOV, position smoothing and forward offset. Pick one, then fine-tune the sliders if you feel like it.",
                    null, new ConfigurationManagerAttributes { CustomDrawer = DrawViewPresets, HideDefaultButton = true }));
            customPresets = new ConfigEntry<string>[CustomPresetCount];
            for (int i = 0; i < CustomPresetCount; i++)
            {
                customPresets[i] = Config.Bind("Custom presets", $"Custom preset {i + 1}", "",
                    new ConfigDescription("Managed from the in-game presets panel, no need to edit by hand.",
                        null, new ConfigurationManagerAttributes { IsAdvanced = true }));
            }
            TryAddStudioToolbarButton();
            MouseSensitivity = Config.Bind("General", "Mouse sensitivity", 1f,
                new ConfigDescription("", new AcceptableValueRange<float>(0.1f, 5f)));
            PositionSmoothing = Config.Bind("Comfort", "Position smoothing", 0.65f,
                new ConfigDescription("How much head motion from animations is filtered out of the camera position. 0 = camera rigidly locked to the head, 1 = very smooth and floaty.", new AcceptableValueRange<float>(0f, 1f)));
            HeadSway = Config.Bind("Comfort", "Animation sway", 0.15f,
                new ConfigDescription("How much of the head animation's rotation leaks into your view. 0 = perfectly stable FPS camera, 1 = fully follow the animation (dizzy!). Roll/tilt is always removed.", new AcceptableValueRange<float>(0f, 1f)));
            PitchLimit = Config.Bind("Comfort", "Pitch limit", 85f,
                new ConfigDescription("How far up/down you can look, in degrees. 90 is straight up/down. Above 90 you can keep going past vertical, " +
                    "e.g. to look back over the head while lying down; the view turns upside down there.", new AcceptableValueRange<float>(30f, 180f)));
            ForwardOffset = Config.Bind("Camera", "Forward offset", 0.05f,
                new ConfigDescription("Camera distance in front of the eyes, in meters. Raise it if you can see the inside of the head.", new AcceptableValueRange<float>(0f, 0.2f)));
            UpOffset = Config.Bind("Camera", "Up offset", 0f,
                new ConfigDescription("Vertical camera offset from the eye line, in meters.", new AcceptableValueRange<float>(-0.1f, 0.1f)));
            NearClip = Config.Bind("Camera", "Near clip plane", 0.01f,
                new ConfigDescription("Lower values let very close geometry render without being cut off.", new AcceptableValueRange<float>(0.001f, 0.1f)));
            HideHead = Config.Bind("General", "Hide character head", true,
                "Hide the POV character's head (incl. hair and head accessories) so nothing clips into the view. Flat screen only, VR has its own switches under VR.");
            EnableInFreeRoam = Config.Bind("General", "Use POV during free-roam", false,
                "OFF by default. When you walk around the map the game already has its own first-person view (press Space) that this mod clashes with - " +
                "with both on, the built-in view hides the body while this one hides the head, so the character can vanish and the camera gets jittery. " +
                "While this is off, Backspace does nothing during free-roam (it still works in H-scenes and Studio). " +
                "Turn it on to use PerspectiveX here anyway: it takes over and suppresses the built-in Space view, but it's less reliable than in H/Studio - " +
                "if the character disappears or the view glitches, turn this back off and use the game's own Space camera instead.");
            AlignWithBody = Config.Bind("Comfort", "Align camera with body", false,
                "Tilt the camera to follow the character's body orientation, so the view isn't kept artificially level when they're lying on their side or leaning. Fine-tune with the roll keys.");
            LookRelativeToBody = Config.Bind("Comfort", "Look relative to body", false,
                "OFF by default. Measure looking around, and the pitch limit, from the character's upper body instead of the world. " +
                "Up/down then follows the torso when they're lying down, bent over or leaning, and the view turns and tilts with it. " +
                "Off = the horizon stays level like before. Flat screen only, in VR your head does the looking.");
            CameraShiftDrag = Config.Bind("Comfort", "Right-drag moves the camera", true,
                "Hold the right mouse button and drag to slide the camera left/right and forward/back (never up or down), e.g. to center it over the body when the character's head is turned to the side. " +
                "Left drag still looks around. In FPS mouse look, hold right click to slide instead of look. Resets when you leave POV or switch character.");
            ShiftResetKey = Config.Bind("Keyboard shortcuts", "Reset camera shift", new KeyboardShortcut(KeyCode.Slash, KeyCode.LeftShift),
                "Put the camera back on the eyes after moving it with right-drag.");
            BindVrSettings();

            // apply settings that are only sampled on POV enter live as well
            DefaultFov.SettingChanged += (s, e) => fov = DefaultFov.Value;
            EventHandler headHidingChanged = (s, e) =>
            {
                if (povEnabled && chara)
                    ApplyHeadHiding();
            };
            HideHead.SettingChanged += headHidingChanged;
            HideHeadVrMain.SettingChanged += headHidingChanged;
            HideHeadVrStudio.SettingChanged += headHidingChanged;
            EnableInFreeRoam.SettingChanged += (s, e) =>
            {
                if (EnableInFreeRoam.Value)
                    Logger.LogMessage("Heads up: free-roam POV fights the game's own Space view and can make your character invisible or the camera glitchy. If it misbehaves, turn it back off and use Space instead.");
            };
            // switching mid-POV keeps you looking where you were, just measured the new way
            LookRelativeToBody.SettingChanged += (s, e) =>
            {
                if (povEnabled && chara && cam && !vrPov)
                    SetLookDirection(cam.transform.forward);
            };

            SceneManager.sceneLoaded += (scene, mode) =>
            {
                hFlag = FindObjectOfType<HFlag>();
                charaList = null;
            };
            SceneManager.sceneUnloaded += scene => charaList = null;

            Camera.onPreCull += OnCameraPreCull;
        }

        private void OnDestroy()
        {
            Camera.onPreCull -= OnCameraPreCull;
            StopVrDetection();
            ReleaseVrMappedKeys();
            if (povEnabled)
                DisablePov();
            SetCursorLock(false);
            UnhookCameraConditions();
        }

        private void Update()
        {
            UpdateVrControllers();
            UpdateHotkeys();
            // deliberately last: it blackholes input for the rest of the frame, so every
            // hotkey of ours (POV toggle, panel toggle, FPS-look toggle: the ways out of a
            // stuck state) must already have been read by the time it runs
            UpdatePanelInputSuppression();
            virtualDownKeys.Clear();
        }

        private void UpdateHotkeys()
        {
            // while OUR preset-name box has focus, typed characters must not trigger hotkeys.
            // Only ours: GUIUtility.keyboardControl is process-global, so keying off that let
            // any other plugin's focused text field (ConfigurationManager's search box, ...)
            // silently kill every PerspectiveX shortcut, POV toggle included.
            bool typingInUi = presetNameFocused;

            if (!typingInUi && IsShortcutDown(PanelKey.Value))
                SetPanelVisible(!panelVisible);

            if (panelVisible)
            {
                EnsureCameraConditionHooks();
                UpdateHiddenHeadScan();
            }

            // Safety net, above the !povEnabled early-out on purpose: a locked cursor is the
            // game warping the physical OS cursor back to one pixel every frame, desktop-wide,
            // and in Studio the only code that ever releases it is Studio.CameraControl's
            // LateUpdate, which we disable while in POV. If we are holding the lock without
            // actively looking around, drop it, whatever else went wrong.
            if (cursorLocked && !dragging && !panning && !fpsMode)
                SetCursorLock(false);

            if (!typingInUi && IsShortcutDown(ToggleKey.Value))
            {
                if (povEnabled)
                    DisablePov();
                else if (IsFreeRoam() && !EnableInFreeRoam.Value)
                    Logger.LogMessage("PerspectiveX POV is off during free-roam - it clashes with the game's own first-person view (Space). Turn on \"Use POV during free-roam\" in the plugin settings to use it here.");
                else
                    EnablePov();
                return;
            }

            if (!povEnabled)
            {
                UpdateVrHeadEntry();
                return;
            }

            if (!chara || !chara.objTop)
            {
                DisablePov();
                return;
            }

            if (vrPov && !CheckVrPovStillValid())
            {
                DisablePov();
                return;
            }

            // outfits/accessories swapped mid-POV bring in renderers we've never seen
            if (ShouldHideHead() && Time.time >= nextHeadRescan)
            {
                nextHeadRescan = Time.time + HeadRescanInterval;
                HideHeadRenderers();
            }

            if (!typingInUi && IsShortcutDown(CyclePrevKey.Value))
            {
                CycleCharacter(-1);
                return;
            }
            if (!typingInUi && IsShortcutDown(CycleNextKey.Value))
            {
                CycleCharacter(1);
                return;
            }

            // in VR the headset does the looking around, and there's no FOV to zoom
            if (vrPov)
            {
                // Dragging into another character's head switches to them (was hotkey only)
                UpdateVrPovSwitch();

                if (!typingInUi && IsShortcutDown(RollResetKey.Value))
                {
                    // back to the eyes, facing where the character faces, following position only
                    if (vrFollowMode != 0)
                    {
                        ResetVrFollow();
                        Logger.LogMessage("VR rotation follow: position only");
                    }
                    vrAlignPending = true;
                }
                if (!typingInUi && (IsShortcutDown(VrFollowCycleKey.Value) || VrFollowButtonPressed()))
                    CycleVrFollow();
                UpdateCommonPovKeys(typingInUi);
                return;
            }

            if (!typingInUi && IsShortcutDown(FpsModeKey.Value))
            {
                fpsMode = !fpsMode;
                dragging = false;
                panning = false;
                SetCursorLock(fpsMode);
            }

            if (!fpsMode)
            {
                if (!dragging && !panning && Input.GetMouseButtonDown(0) && !IsPointerOverUI() && !IsMouseOverPanel())
                {
                    dragging = true;
                    SetCursorLock(true);
                }
                if (dragging && !Input.GetMouseButton(0))
                {
                    dragging = false;
                    SetCursorLock(false);
                }
                if (!dragging && !panning && CameraShiftDrag.Value && Input.GetMouseButtonDown(1) && !IsPointerOverUI() && !IsMouseOverPanel())
                {
                    panning = true;
                    SetCursorLock(true);
                }
                if (panning && !Input.GetMouseButton(1))
                {
                    panning = false;
                    SetCursorLock(false);
                }
            }

            if (!typingInUi && IsShortcutDown(ShiftResetKey.Value))
                cameraShift = Vector3.zero;

            // in FPS mode the cursor is already captured, so holding right click pans instead of looking
            if (panning || (fpsMode && CameraShiftDrag.Value && Input.GetMouseButton(1)))
            {
                ShiftCamera(Input.GetAxis("Mouse X"), Input.GetAxis("Mouse Y"));
            }
            else if (fpsMode || dragging)
            {
                float dx = Input.GetAxis("Mouse X") * MouseSensitivity.Value;
                float dy = -Input.GetAxis("Mouse Y") * MouseSensitivity.Value;

                // rotate the mouse delta by the current camera roll so the controls
                // stay screen-relative when the view is tilted
                float rollRad = (manualRoll + (UseBodyRoll() ? bodyRoll : 0f)) * Mathf.Deg2Rad;
                float rollCos = Mathf.Cos(rollRad);
                float rollSin = Mathf.Sin(rollRad);

                // past vertical (pitch limit above 90) the view is upside down, so turning
                // around the world up axis moves the picture the other way on screen
                float yawDir = Mathf.Cos(pitch * Mathf.Deg2Rad) < 0f ? -1f : 1f;

                yaw += (dx * rollCos + dy * rollSin) * yawDir;
                if (yaw > 180f) yaw -= 360f;
                else if (yaw < -180f) yaw += 360f;
                pitch = Mathf.Clamp(pitch + dy * rollCos - dx * rollSin, -PitchLimit.Value, PitchLimit.Value);
            }

            if (ScrollFovEnabled.Value)
            {
                float scroll = Input.GetAxis("Mouse ScrollWheel");
                if (scroll != 0f && (fpsMode || dragging || panning || (!IsPointerOverUI() && !IsMouseOverPanel())))
                    fov = Mathf.Clamp(fov - scroll * 15f, 20f, 120f);
            }

            if (typingInUi)
                return;

            bool rollLeft = RollLeftKey.Value.IsPressed();
            bool rollRight = RollRightKey.Value.IsPressed();
            if (IsShortcutDown(RollResetKey.Value))
            {
                manualRoll = 0f;
            }
            else if (rollLeft != rollRight)
            {
                manualRoll += (rollLeft ? 1f : -1f) * 60f * Time.deltaTime;
                if (manualRoll > 180f) manualRoll -= 360f;
                else if (manualRoll < -180f) manualRoll += 360f;
            }

            UpdateCommonPovKeys(typingInUi);
        }

        // Right-drag: slide the camera flat along the ground, never up or down. Mouse left/right moves
        // it left/right and mouse up/down moves it forward/back, both as seen from where you're
        // looking. Stored relative to the character's facing rather than the head, so when the head is
        // turned to the side you can pull the view back over the body and it stays there while the
        // head keeps moving.
        private void ShiftCamera(float mouseX, float mouseY)
        {
            if (!cam)
                return;
            Vector3 right = Vector3.ProjectOnPlane(cam.transform.right, Vector3.up);
            if (right.sqrMagnitude < 0.0001f)
                return; // view rolled onto its side, no sensible flat left/right
            right.Normalize();
            Vector3 forward = Vector3.Cross(right, Vector3.up);
            float scale = MouseSensitivity.Value * CameraShiftSpeed;
            Vector3 move = right * (mouseX * scale) + forward * (mouseY * scale);
            Vector3 shift = cameraShift + Quaternion.Inverse(GetFacingFrame()) * move;
            shift.y = 0f;
            cameraShift = Vector3.ClampMagnitude(shift, MaxCameraShift);
        }

        // the character's facing, turned around the vertical axis only (camera shift, VR rotation follow)
        private Quaternion GetFacingFrame()
        {
            return Quaternion.Euler(0f, chara.transform.eulerAngles.y, 0f);
        }

        // POV keys that work the same on a flat screen and in VR
        private void UpdateCommonPovKeys(bool typingInUi)
        {
            if (typingInUi)
                return;

            if (IsShortcutDown(CamLockKey.Value))
            {
                camLocked = !camLocked;
                if (!camLocked)
                    swayInit = false; // re-baseline so the sway doesn't jolt on unlock
                Logger.LogMessage(camLocked ? "Camera locked in place" : "Camera unlocked");
            }

            for (int i = 0; i < ViewSlotCount; i++)
            {
                if (IsShortcutDown(saveSlotKeys[i].Value))
                    SaveViewSlot(i);
                else if (IsShortcutDown(loadSlotKeys[i].Value))
                    LoadViewSlot(i);
            }
        }

        private static bool IsPointerOverUI()
        {
            return GUIUtility.hotControl != 0 ||
                   (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject());
        }

        // The panel is an IMGUI window, which uGUI can't see at all (EventSystem's
        // IsPointerOverGameObject only knows about uGUI), so clicks on it would otherwise also
        // land on whatever game UI happens to sit underneath. Input.ResetInputAxes() is the
        // blunt instrument ConfigurationManager uses for the same problem, and it is genuinely
        // dangerous: it is process-wide (it took out other plugins' hotkeys, including
        // ConfigurationManager's own F1) and it used to be armed purely by "is the cursor
        // inside this rectangle". If the game had pinned the OS cursor at a pixel the panel was
        // later dragged over, that condition became permanently true and swallowed every key
        // the user could have escaped with. So: never suppress while the cursor is pinned, and
        // give up entirely if we have been suppressing for seconds with a cursor that has not
        // moved by a single pixel, the exact signature of that softlock.
        private void UpdatePanelInputSuppression()
        {
            if (!panelVisible || fpsMode || dragging || panning || IsCursorPinned() || !IsMouseOverPanel())
            {
                inputSuppressFrames = 0;
                return;
            }

            Vector3 mouse = Input.mousePosition;
            if (inputSuppressFrames == 0 || mouse != lastSuppressMousePos)
            {
                lastSuppressMousePos = mouse;
                inputSuppressFrames = 1;
            }
            else if (++inputSuppressFrames > InputSuppressFrameLimit)
            {
                return;
            }

            Input.ResetInputAxes();
        }

        // GameCursor.isLock is public static and process-global; while it is set the game warps
        // the physical cursor back to a snapshotted screen pixel every frame via USER32.
        private static bool IsCursorPinned()
        {
            try
            {
                return GameCursor.isLock;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // The repair button only lives in the panel, which a user with a damaged scene has no
        // reason to open, so say it out loud once per session when we notice the damage.
        private void WarnAboutHiddenHeadsOnce()
        {
            if (!isStudio || hiddenHeadNoticeShown)
                return;
            hiddenHeadNoticeShown = true;
            hiddenHeadChars = ScanHiddenHeads(false);
            if (hiddenHeadChars > 0)
                Logger.LogMessage(hiddenHeadChars + " character(s) in this scene have their head hidden by the scene file itself - open the PerspectiveX panel (" + PanelKey.Value + ") to bring it back.");
        }

        private void UpdateHiddenHeadScan()
        {
            if (!isStudio || Time.time < nextHiddenHeadScan)
                return;
            nextHiddenHeadScan = Time.time + HiddenHeadScanInterval;
            hiddenHeadChars = ScanHiddenHeads(false);
        }

        private void OnCameraPreCull(Camera renderingCam)
        {
            if (!povEnabled || cam == null || renderingCam != cam)
                return;
            if (!chara || !chara.objHeadBone)
                return;
            if (vrPov && !IsFirstVrCullThisFrame())
                return;

            Transform headT = chara.objHeadBone.transform;
            Quaternion headRot = headT.rotation;
            Vector3 offset = vrPov
                ? new Vector3(0f, VrUpOffset.Value, VrForwardOffset.Value)
                : new Vector3(0f, UpOffset.Value, ForwardOffset.Value);
            Vector3 targetPos = GetEyeMidpoint(chara, headT) + headRot * offset;
            if (cameraShift != Vector3.zero)
                targetPos += GetFacingFrame() * cameraShift;

            float dt = Time.deltaTime;
            float smoothing = PositionSmoothing.Value;
            if (!smoothPosInit)
            {
                smoothPos = targetPos;
                smoothPosInit = true;
            }
            else if (!camLocked) // while locked the camera stays where it is
            {
                if (smoothing <= 0f)
                {
                    smoothPos = targetPos;
                }
                else
                {
                    float followSpeed = Mathf.Lerp(70f, 4f, smoothing);
                    smoothPos = Vector3.Lerp(smoothPos, targetPos, 1f - Mathf.Exp(-followSpeed * dt));
                }
            }

            if (vrPov)
            {
                PlaceVrRig();
                return;
            }

            float roll = manualRoll;
            if (UseBodyRoll())
            {
                // roll of the head bone around its own forward axis, smoothed so
                // animation wobble doesn't rock the view; degenerates when the head
                // points straight up/down, so keep the last known roll there
                Vector3 headFwd2 = headRot * Vector3.forward;
                if (!camLocked && Mathf.Abs(headFwd2.y) < 0.99f)
                {
                    Vector3 levelUp = Vector3.ProjectOnPlane(Vector3.up, headFwd2).normalized;
                    Vector3 headUp = Vector3.ProjectOnPlane(headRot * Vector3.up, headFwd2).normalized;
                    // Vector3.SignedAngle doesn't exist in this Unity version
                    float targetRoll = Mathf.Atan2(Vector3.Dot(Vector3.Cross(levelUp, headUp), headFwd2),
                        Vector3.Dot(levelUp, headUp)) * Mathf.Rad2Deg;
                    if (!bodyRollInit)
                    {
                        bodyRoll = targetRoll;
                        bodyRollInit = true;
                    }
                    else
                    {
                        bodyRoll = Mathf.LerpAngle(bodyRoll, targetRoll, 1f - Mathf.Exp(-3f * dt));
                    }
                }
                roll += bodyRoll;
            }
            else
            {
                bodyRollInit = false;
            }

            Quaternion finalRot = UpdateLookFrame(dt) * Quaternion.Euler(pitch, yaw, roll);

            float sway = HeadSway.Value;
            if (sway > 0f && !camLocked)
            {
                Vector3 headFwd = headRot * Vector3.forward;
                if (Mathf.Abs(headFwd.y) < 0.999f)
                {
                    // head rotation with roll removed, split into a slow-moving baseline and
                    // the fast animation component; only the fast part sways the view
                    Quaternion headNoRoll = Quaternion.LookRotation(headFwd, Vector3.up);
                    if (!swayInit)
                    {
                        swayBaseline = headNoRoll;
                        swayInit = true;
                    }
                    swayBaseline = Quaternion.Slerp(swayBaseline, headNoRoll, 1f - Mathf.Exp(-2.5f * dt));
                    Quaternion animDelta = headNoRoll * Quaternion.Inverse(swayBaseline);
                    finalRot = Quaternion.Slerp(Quaternion.identity, animDelta, sway) * finalRot;
                }
            }

            cam.transform.SetPositionAndRotation(smoothPos, finalRot);
            cam.fieldOfView = fov;
            cam.nearClipPlane = NearClip.Value;
        }

        private static Vector3 GetEyeMidpoint(ChaControl c, Transform headT)
        {
            var eyeLookCtrl = c.eyeLookCtrl;
            if (eyeLookCtrl != null && eyeLookCtrl.eyeLookScript != null)
            {
                var eyeObjs = eyeLookCtrl.eyeLookScript.eyeObjs;
                if (eyeObjs != null && eyeObjs.Length >= 2 &&
                    eyeObjs[0] != null && eyeObjs[1] != null &&
                    eyeObjs[0].eyeTransform && eyeObjs[1].eyeTransform)
                {
                    return Vector3.Lerp(eyeObjs[0].eyeTransform.position, eyeObjs[1].eyeTransform.position, 0.5f);
                }
            }
            return headT.position + headT.rotation * new Vector3(0f, 0.06f, 0.08f);
        }

        // Keep this exactly parameterless: other plugins (TimelinePOVSwitchX) invoke it and patch it
        // by reflection, and an extra parameter, even an optional one, breaks their calls. VR head
        // entry hands its character over through vrEntryTarget instead.
        private void EnablePov()
        {
            ChaControl target = vrEntryTarget;
            vrEntryTarget = null;

            bool vr = IsVrEnabled() && FindVrRig(true);
            if (vr && IsVrModPovActive())
            {
                Logger.LogMessage("Can't enter POV: the VR mod's own POV is active, turn that off first");
                return;
            }

            Camera mainCam = vr ? vrCam : Camera.main;
            if (!mainCam)
            {
                Logger.LogMessage("Can't enter POV: no main camera found");
                return;
            }

            if (!target)
                target = FindPovCharacter();
            if (!target)
            {
                Logger.LogMessage(isStudio
                    ? "Can't enter POV: select a character in the workspace first"
                    : "Can't enter POV: no suitable character found");
                return;
            }

            chara = target;
            cam = mainCam;
            vrPov = vr;

            if (vr)
            {
                // the headset drives the view, so none of the flat-screen camera takeover below applies
                BeginVrPov();
            }
            else
            {
                disabledCameraControl = cam.GetComponent<CameraControl_Ver2>();
                if (!disabledCameraControl)
                    disabledCameraControl = cam.GetComponent<global::Studio.CameraControl>();
                if (disabledCameraControl)
                    disabledCameraControl.enabled = false;
                // Unconditionally, and after switching the camera control off: dragging the Studio
                // camera pins the OS cursor through GameCursor, and Studio.CameraControl.LateUpdate
                // is the only code that ever releases it again. Entering POV mid-drag disables that
                // LateUpdate and would strand the physical cursor on one pixel, desktop-wide, with
                // no way back short of killing the game.
                SetCursorLock(false);
                FreezeRoamCamera();

                dof = cam.GetComponent<DepthOfField>();
                if (dof && dof.enabled && dof.focalTransform)
                {
                    dofOrigSize = dof.focalSize;
                    dofOrigAperture = dof.aperture;
                    dofOrigFocalPos = dof.focalTransform.localPosition;
                    dof.focalTransform.localPosition = new Vector3(0f, 0f, 0.25f);
                    dof.focalSize = 0.9f;
                    dof.aperture = 0.6f;
                    dofChanged = true;
                }
            }

            origFov = cam.fieldOfView;
            origNearClip = cam.nearClipPlane;
            if (fov <= 0f)
                fov = DefaultFov.Value;

            ApplyHeadHiding();
            nextHeadRescan = Time.time + HeadRescanInterval;
            WarnAboutHiddenHeadsOnce();
            SuppressFreeRoamViewSwitcher();

            InitViewFromHead();
            manualRoll = 0f;
            camLocked = false;
            dragging = false;
            panning = false;
            fpsMode = false;
            povEnabled = true;
        }

        private void DisablePov()
        {
            povEnabled = false;

            // Everything in here restores game state we took over, and any single step can throw
            // (a character destroyed under us, KKS API drift, ...). The cursor release in the
            // finally block is the one that absolutely must happen: leaving it out is a
            // desktop-wide cursor lock the user can only escape by killing the game.
            try
            {
                RestoreHeadRenderers();
                chara = null;

                if (cam)
                {
                    if (!vrPov)
                        cam.fieldOfView = origFov;
                    cam.nearClipPlane = origNearClip;
                }
                RestoreVrRig();
                if (disabledCameraControl)
                    disabledCameraControl.enabled = true;
                RestoreRoamCamera();
                RestoreFreeRoamViewSwitcher();

                if (dofChanged && dof)
                {
                    dof.focalSize = dofOrigSize;
                    dof.aperture = dofOrigAperture;
                    if (dof.focalTransform)
                        dof.focalTransform.localPosition = dofOrigFocalPos;
                }
            }
            finally
            {
                SetCursorLock(false);
                chara = null;
                disabledCameraControl = null;
                dof = null;
                dofChanged = false;
                cam = null;
                vrPov = false;
                ResetVrFollow();
                vrEntryLatch = true;   // must leave the head before POV can be entered again
                dragging = false;
                panning = false;
                fpsMode = false;
                camLocked = false;
            }
        }

        // A borrowed slot in one of the game camera's condition delegates, with whatever was in it
        // before us (Studio itself puts a delegate in noCtrlCondition during StudioScene init).
        private sealed class CameraCondHook
        {
            public FieldInfo Field;
            public Delegate Prev;
            public Delegate Ours;
        }

        // Both game camera controllers ask a pair of public delegates for permission before
        // reading the mouse: noCtrlCondition/NoCtrlCondition ("true = don't drag") gates the
        // orbit drag, zoomCondition/ZoomCondition ("true = allowed") gates the scroll zoom.
        // Without this, left-clicking a button on our IMGUI panel also orbits the Studio camera,
        // and orbiting pins the OS cursor at that pixel, which is by construction inside the
        // panel, i.e. a direct route into the cursor softlock. Studio.CameraControl only guards
        // its own drag with EventSystem.IsPointerOverGameObject(), which is uGUI-only and cannot
        // see IMGUI windows. Field lookup is reflective (the two controllers differ in casing,
        // and KKS could drift). If it isn't there we simply don't get the guard.
        private void EnsureCameraConditionHooks()
        {
            if (hookedCameraControl)
                return;
            MonoBehaviour ctrl = null;
            try
            {
                Camera mainCam = Camera.main;
                if (!mainCam)
                    return;
                ctrl = mainCam.GetComponent<CameraControl_Ver2>();
                if (!ctrl)
                    ctrl = mainCam.GetComponent<global::Studio.CameraControl>();
            }
            catch (Exception)
            {
                return;
            }
            if (!ctrl)
                return;
            hookedCameraControl = ctrl;
            noCtrlHook = HookCameraCondition(ctrl, "noCtrlCondition", "CameraNoCtrlCondition");
            zoomHook = HookCameraCondition(ctrl, "zoomCondition", "CameraZoomCondition");
        }

        private CameraCondHook HookCameraCondition(MonoBehaviour ctrl, string fieldName, string methodName)
        {
            try
            {
                FieldInfo field = ctrl.GetType().GetField(fieldName,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (field == null || !typeof(Delegate).IsAssignableFrom(field.FieldType))
                    return null;
                MethodInfo method = GetType().GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
                Delegate ours = Delegate.CreateDelegate(field.FieldType, this, method, false);
                if (ours == null)
                    return null;
                var hook = new CameraCondHook { Field = field, Prev = (Delegate)field.GetValue(ctrl), Ours = ours };
                field.SetValue(ctrl, ours);
                return hook;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void UnhookCameraConditions()
        {
            if (hookedCameraControl)
            {
                RestoreCameraCondition(noCtrlHook);
                RestoreCameraCondition(zoomHook);
            }
            noCtrlHook = null;
            zoomHook = null;
            hookedCameraControl = null;
        }

        private void RestoreCameraCondition(CameraCondHook hook)
        {
            if (hook == null)
                return;
            try
            {
                var current = (Delegate)hook.Field.GetValue(hookedCameraControl);
                hook.Field.SetValue(hookedCameraControl,
                    current == hook.Ours ? hook.Prev : Delegate.Remove(current, hook.Ours));
            }
            catch (Exception)
            {
            }
        }

        // Chained by hand rather than Delegate.Combine: invoking a multicast delegate returns
        // only the LAST handler's value, so combining onto Studio's existing noCtrlCondition
        // would silently discard Studio's own answer.
        private bool CameraNoCtrlCondition()
        {
            return InvokePrevCondition(noCtrlHook, false) || IsMouseOverPanel();
        }

        private bool CameraZoomCondition()
        {
            return InvokePrevCondition(zoomHook, true) && !IsMouseOverPanel();
        }

        private static bool InvokePrevCondition(CameraCondHook hook, bool fallback)
        {
            if (hook == null || hook.Prev == null)
                return fallback;
            try
            {
                return (bool)hook.Prev.DynamicInvoke(null);
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        // The main-game free-roam camera isn't a CameraControl_Ver2 (that's H) or Studio.CameraControl
        // (that's studio). It's driven by the game's bundled StrayTech CameraSystem, a singleton that
        // lives on its own manager object and writes the MainCamera transform every Update/LateUpdate.
        // Because it isn't a component on the camera, cam.GetComponent<...> never finds it, so it keeps
        // running and fights our onPreCull override → the jittery, teleporting, uncontrollable free-roam
        // camera. Its ShouldUpdate flag is the game's own pause switch: false makes DoCameraUpdate early-out
        // (no transform writes) and also makes it ignore new camera-state registrations, so the freeze holds
        // even across map-camera-change triggers while POV is active. Reached by reflection so a missing/renamed
        // StrayTech type on Koikatsu Sunshine can't break POV entry; if absent we simply do nothing extra.
        private void FreezeRoamCamera()
        {
            roamCamSystem = null;
            roamCamShouldUpdate = null;
            try
            {
                Type camSysType = Type.GetType("StrayTech.CameraSystem, Assembly-CSharp");
                if (camSysType == null)
                    return;
                UnityEngine.Object instance = FindObjectOfType(camSysType);
                if (!instance)
                    return;
                PropertyInfo shouldUpdate = camSysType.GetProperty("ShouldUpdate", BindingFlags.Public | BindingFlags.Instance);
                if (shouldUpdate == null)
                    return;
                roamCamPrevShouldUpdate = (bool)shouldUpdate.GetValue(instance, null);
                shouldUpdate.SetValue(instance, false, null);
                roamCamSystem = instance;
                roamCamShouldUpdate = shouldUpdate;
            }
            catch (Exception)
            {
                // StrayTech API drift on KKS must never break entering POV
                roamCamSystem = null;
                roamCamShouldUpdate = null;
            }
        }

        private void RestoreRoamCamera()
        {
            if (roamCamShouldUpdate != null && roamCamSystem)
            {
                try
                {
                    roamCamShouldUpdate.SetValue(roamCamSystem, roamCamPrevShouldUpdate, null);
                }
                catch (Exception)
                {
                    // ditto: never let camera-system restore break POV exit
                }
            }
            roamCamSystem = null;
            roamCamShouldUpdate = null;
        }

        // True only in the main-game free-roam map. That mode has its own Space-toggled first-person
        // view which hides the whole body; combined with our head-hiding the character vanishes and the
        // two cameras fight, so POV there is opt-in (EnableInFreeRoam) and Backspace is otherwise ignored.
        // Studio and H-scenes have no such switcher, so POV keeps working there unconditionally. hFlag
        // (non-null in H) is an extra guard so an H-scene can never be misread as free-roam.
        private bool IsFreeRoam()
        {
            return !isStudio && hFlag == null && FindFreeRoamViewSwitcher() != null;
        }

        private MonoBehaviour FindFreeRoamViewSwitcher()
        {
            try
            {
                // ActionGame.CameraStateDefinitionChange lives only in the free-roam ActionScene and
                // drives its TPS/FPS view + body visibility. Reflection so a missing/renamed type on
                // Koikatsu Sunshine can't break anything - there it just reads as "not free-roam".
                Type t = Type.GetType("ActionGame.CameraStateDefinitionChange, Assembly-CSharp");
                if (t == null)
                    return null;
                return FindObjectOfType(t) as MonoBehaviour;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // Stops the free-roam view switcher while our POV owns the camera: disabling the component halts
        // both the Space TPS/FPS toggle and its per-frame distance-based body hiding (its Rx streams are
        // gated on base.enabled), and we force the body visible so only the head stays hidden (by us).
        private void SuppressFreeRoamViewSwitcher()
        {
            freeRoamViewSwitcher = null;
            MonoBehaviour switcher = FindFreeRoamViewSwitcher();
            if (!switcher)
                return;
            try
            {
                freeRoamViewSwitcherWasEnabled = switcher.enabled;
                switcher.enabled = false;
                freeRoamViewSwitcher = switcher;
                SetVisibleAll(chara, true);
            }
            catch (Exception)
            {
                // free-roam handling must never break POV entry
            }
        }

        private void RestoreFreeRoamViewSwitcher()
        {
            if (freeRoamViewSwitcher)
            {
                try
                {
                    // re-enabling it hands body/camera management back to the game, which re-derives
                    // visibleAll on its own next frame - no need to restore that ourselves
                    freeRoamViewSwitcher.enabled = freeRoamViewSwitcherWasEnabled;
                }
                catch (Exception)
                {
                }
            }
            freeRoamViewSwitcher = null;
        }

        private static void SetVisibleAll(ChaControl c, bool value)
        {
            if (!c)
                return;
            try
            {
                // visibleAll sits on a ChaControl base class; reflection keeps it safe if KKS differs
                PropertyInfo p = c.GetType().GetProperty("visibleAll", BindingFlags.Public | BindingFlags.Instance);
                if (p != null && p.CanWrite)
                    p.SetValue(c, value, null);
                else
                {
                    FieldInfo f = c.GetType().GetField("visibleAll", BindingFlags.Public | BindingFlags.Instance);
                    if (f == null)
                        return;
                    f.SetValue(c, value);
                }
                c.LateUpdateForce(); // apply the visibility change this frame
            }
            catch (Exception)
            {
            }
        }

        private void CycleCharacter(int direction)
        {
            if (charaList == null || charaList.Count == 0)
                RebuildCharaList();
            int count = charaList.Count;
            if (count == 0)
                return;

            int start = chara ? charaList.IndexOf(chara) : -1;
            for (int step = 1; step <= count; step++)
            {
                int i = ((start + direction * step) % count + count) % count;
                ChaControl c = charaList[i];
                if (c == chara)
                    continue;
                if (!IsValidPovTarget(c))
                    continue;
                SwitchTo(c);
                return;
            }
        }

        private void SwitchTo(ChaControl next)
        {
            RestoreHeadRenderers();

            chara = next;
            ApplyHeadHiding();
            // when we've taken over free-roam, keep the newly selected body visible too
            // (the built-in view switcher that would normally manage this is suppressed)
            if (freeRoamViewSwitcher)
                SetVisibleAll(chara, true);
            InitViewFromHead();
            camLocked = false;
        }

        // Head hiding used to go through the game's own chara.fileStatus.visibleHeadAlways flag,
        // which turned out to be a serious data-corruption bug: visibleHeadAlways is a SERIALIZED
        // member of ChaFileStatus, and Studio writes it into the scene file
        // (SceneInfo.Save -> OICharInfo.Save -> ChaFile.SaveCharaFile -> GetStatusBytes). Any scene
        // saved (or autosaved) while POV was active came back with a permanently invisible head,
        // with no Studio UI anywhere to undo it. Renderer.enabled is pure runtime state instead:
        // nothing serializes it, and the only game code that writes it (RayChara/LookHit/
        // CollisionCamera see-through helpers, Studio's camera/background gizmos) never targets the
        // head, and a re-enable would be undone by the periodic rescan anyway. It also leaves the
        // GameObjects active, so the head bones keep animating (deactivating them is what makes
        // KK_StudioPOV's camera drift into the body) and the KK/KKS restore divergence from the old
        // flag approach (GitHub issue #2) can't come back either.
        //
        // 1.3.3 used shadowCastingMode = ShadowsOnly here, to keep the head's shadow while hiding
        // the mesh. That backfired: in POV the head sits ON the camera, so it stays a shadow caster
        // right at the near plane, and the directional light's camera-fitted shadow cascades then
        // smear a dark blob across the view that swings around with every mouse move (reported by
        // Koji on Discord). Disabling the renderer drops it out of the shadow map too.
        private void ApplyHeadHiding()
        {
            if (ShouldHideHead())
                HideHeadRenderers();
            else
                RestoreHeadRenderers();
        }

        private void HideHeadRenderers()
        {
            if (!chara)
                return;

            // drop renderers destroyed since the last pass (outfit/accessory changes) so a long
            // POV session with lots of wardrobe edits doesn't grow the map forever
            deadRenderers.Clear();
            foreach (var kv in hiddenRenderers)
                if (!kv.Key)
                    deadRenderers.Add(kv.Key);
            for (int i = 0; i < deadRenderers.Count; i++)
                hiddenRenderers.Remove(deadRenderers[i]);
            deadRenderers.Clear();

            try
            {
                // same object set the game's own visibleHeadAlways=false path hides:
                // head, hair, the extended tongue and head-parented accessories
                CollectHeadRenderers(chara.objHead);
                GameObject[] hair = chara.objHair;
                if (hair != null)
                    for (int i = 0; i < hair.Length; i++)
                        CollectHeadRenderers(hair[i]);
                CollectHeadRenderers(chara.objTongueEx);

                GameObject[] acc = chara.objAccessory;
                ChaFileAccessory.PartsInfo[] parts = chara.nowCoordinate?.accessory?.parts;
                if (acc != null && parts != null)
                    for (int i = 0; i < acc.Length && i < parts.Length; i++)
                        if (parts[i] != null && parts[i].partsOfHead)
                            CollectHeadRenderers(acc[i]);
            }
            catch (Exception)
            {
                // KKS API drift must never break POV; worst case some of the head stays visible
            }
        }

        private void CollectHeadRenderers(GameObject root)
        {
            if (!root)
                return;
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer r = renderers[i];
                if (!r)
                    continue;
                if (!hiddenRenderers.ContainsKey(r))
                {
                    // Already switched off by something else (a hidden accessory, or another mod's
                    // own head hiding such as KKCharaStudioVR's): leave it to whoever did that.
                    // Remembering it as "off" and putting that back on exit left heads hidden for
                    // good whenever the other mod showed the head again while we were in POV.
                    if (!r.enabled)
                        continue;
                    hiddenRenderers[r] = true;
                }
                // re-applied on every rescan, not just on first sight, so anything else that
                // switches a head renderer back on gets corrected within half a second
                r.enabled = false;
            }
        }

        private void RestoreHeadRenderers()
        {
            foreach (var kv in hiddenRenderers)
            {
                Renderer r = kv.Key;
                if (!r)
                    continue; // renderer was destroyed (outfit change, character unloaded)
                try
                {
                    r.enabled = kv.Value;
                }
                catch (Exception)
                {
                }
            }
            hiddenRenderers.Clear();
        }

        // Repair for scenes already damaged by the old flag-based head hiding (see above): the
        // scene file itself carries visibleHeadAlways=false, so loading it shows a headless
        // character forever and uninstalling the mod doesn't help. Studio never sets this flag
        // itself, so in Studio a false value can only be that damage. Fixing it needs a re-save
        // of the scene to stick, which is why this is a deliberate button and not automatic.
        private int ScanHiddenHeads(bool repair)
        {
            int count = 0;
            foreach (ChaControl c in FindObjectsOfType<ChaControl>())
            {
                if (!c)
                    continue;
                try
                {
                    if (c.fileStatus == null || c.fileStatus.visibleHeadAlways)
                        continue;
                    count++;
                    if (!repair)
                        continue;
                    c.fileStatus.visibleHeadAlways = true;
                    c.LateUpdateForce();
                }
                catch (Exception)
                {
                }
            }
            return count;
        }

        // rendered by ConfigurationManager inside the settings window (CustomDrawer)
        private void DrawViewPresets(ConfigEntryBase entry)
        {
            if (GUILayout.Button("Cozy (60)", GUILayout.ExpandWidth(true)))
                ApplyViewPreset(60f, 0.65f, 0.05f);
            if (GUILayout.Button("Natural (90)", GUILayout.ExpandWidth(true)))
                ApplyViewPreset(90f, 0.5f, 0.02f);
            if (GUILayout.Button("Action (110)", GUILayout.ExpandWidth(true)))
                ApplyViewPreset(110f, 0.35f, 0f);
        }

        private void ApplyViewPreset(float fovValue, float smoothing, float forwardOffset)
        {
            DefaultFov.Value = fovValue; // its SettingChanged handler also updates the live fov
            PositionSmoothing.Value = smoothing;
            ForwardOffset.Value = forwardOffset;
        }

        private void SaveViewSlot(int slot)
        {
            viewSlots[slot].Value = string.Join(";", new[]
            {
                fov.ToString(CultureInfo.InvariantCulture),
                yaw.ToString(CultureInfo.InvariantCulture),
                pitch.ToString(CultureInfo.InvariantCulture),
                manualRoll.ToString(CultureInfo.InvariantCulture),
                ForwardOffset.Value.ToString(CultureInfo.InvariantCulture),
                UpOffset.Value.ToString(CultureInfo.InvariantCulture)
            });
            Logger.LogMessage($"View saved to slot {slot + 1}");
        }

        private void LoadViewSlot(int slot)
        {
            string data = viewSlots[slot].Value;
            if (string.IsNullOrEmpty(data))
            {
                Logger.LogMessage($"View slot {slot + 1} is empty - save the current view into it with {saveSlotKeys[slot].Value}");
                return;
            }
            try
            {
                string[] parts = data.Split(';');
                fov = Mathf.Clamp(float.Parse(parts[0], CultureInfo.InvariantCulture), 20f, 120f);
                yaw = float.Parse(parts[1], CultureInfo.InvariantCulture);
                pitch = Mathf.Clamp(float.Parse(parts[2], CultureInfo.InvariantCulture), -PitchLimit.Value, PitchLimit.Value);
                manualRoll = float.Parse(parts[3], CultureInfo.InvariantCulture);
                ForwardOffset.Value = float.Parse(parts[4], CultureInfo.InvariantCulture);
                UpOffset.Value = float.Parse(parts[5], CultureInfo.InvariantCulture);
                Logger.LogMessage($"View slot {slot + 1} loaded");
            }
            catch (Exception)
            {
                Logger.LogMessage($"View slot {slot + 1} couldn't be read, save it again");
            }
        }

        private void SetPanelVisible(bool visible)
        {
            if (panelVisible == visible)
                return;
            panelVisible = visible;
            if (!visible)
                presetNameFocused = false;
            else
                nextHiddenHeadScan = 0f;
            if (toolbarToggle == null || toolbarToggleValue == null)
                return;
            try
            {
                // keep the studio toolbar button's pressed/green state in sync when the
                // panel is opened/closed via hotkey or its close button
                if ((bool)toolbarToggleValue.GetValue(toolbarToggle, null) != visible)
                    toolbarToggleValue.SetValue(toolbarToggle, visible, null);
            }
            catch (Exception)
            {
                // KKAPI API drift must never break the panel itself
            }
        }

        private bool IsMouseOverPanel()
        {
            if (!panelVisible || !panelRectInit)
                return false;
            // IMGUI rects are top-left based, Input.mousePosition is bottom-left based
            Vector2 mouse = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            return panelRect.Contains(mouse);
        }

        private void OnGUI()
        {
            if (!panelVisible)
            {
                presetNameFocused = false;
                return;
            }

            if (!panelStylesInit)
            {
                panelHeaderStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
                panelHintStyle = new GUIStyle(GUI.skin.label) { fontSize = 11, wordWrap = true };
                panelHintStyle.normal.textColor = new Color(1f, 1f, 1f, 0.65f);
                panelStylesInit = true;
            }
            if (!panelRectInit)
            {
                // near the bottom-left studio toolbar by default; draggable afterwards
                panelRect = new Rect(70f, Mathf.Max(40f, Screen.height - 520f), 340f, 0f);
                panelRectInit = true;
            }

            panelRect = GUILayout.Window(PanelWindowId, panelRect, DrawPanel, PluginName, GUILayout.Width(340f));
            panelRect.x = Mathf.Clamp(panelRect.x, 0f, Mathf.Max(0f, Screen.width - panelRect.width));
            panelRect.y = Mathf.Clamp(panelRect.y, 0f, Mathf.Max(0f, Screen.height - panelRect.height));
        }

        private void DrawPanel(int windowId)
        {
            if (GUI.Button(new Rect(panelRect.width - 23f, 3f, 20f, 16f), "X"))
            {
                SetPanelVisible(false);
                return;
            }

            GUILayout.Label("View presets", panelHeaderStyle);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Cozy (60)"))
                ApplyViewPreset(60f, 0.65f, 0.05f);
            if (GUILayout.Button("Natural (90)"))
                ApplyViewPreset(90f, 0.5f, 0.02f);
            if (GUILayout.Button("Action (110)"))
                ApplyViewPreset(110f, 0.35f, 0f);
            GUILayout.EndHorizontal();

            GUILayout.Space(8f);
            GUILayout.Label("Custom presets", panelHeaderStyle);
            int firstEmpty = -1;
            for (int i = 0; i < CustomPresetCount; i++)
            {
                string data = customPresets[i].Value;
                if (string.IsNullOrEmpty(data))
                {
                    if (firstEmpty < 0)
                        firstEmpty = i;
                    continue;
                }
                string[] parts = data.Split(';');
                GUILayout.BeginHorizontal();
                GUILayout.Label(parts[0], GUILayout.ExpandWidth(true));
                if (GUILayout.Button("Apply", GUILayout.Width(56f)))
                    ApplyCustomPreset(parts);
                if (GUILayout.Button("Update", GUILayout.Width(62f)))
                    customPresets[i].Value = SerializeCurrentPreset(parts[0]);
                if (GUILayout.Button("X", GUILayout.Width(26f)))
                    customPresets[i].Value = "";
                GUILayout.EndHorizontal();
            }
            if (firstEmpty >= 0)
            {
                GUILayout.BeginHorizontal();
                // named so we can tell OUR text field having focus from any other plugin's
                GUI.SetNextControlName(PresetNameControl);
                newPresetName = GUILayout.TextField(newPresetName, 24, GUILayout.ExpandWidth(true));
                if (GUILayout.Button("Save current", GUILayout.Width(96f)))
                {
                    string name = newPresetName.Trim();
                    if (name.Length == 0)
                        name = "Preset " + (firstEmpty + 1);
                    customPresets[firstEmpty].Value = SerializeCurrentPreset(name);
                    newPresetName = "";
                    GUIUtility.keyboardControl = 0;
                }
                GUILayout.EndHorizontal();
                GUILayout.Label("Type a name and save the current FOV, smoothing and camera offsets as your own preset.", panelHintStyle);
            }
            else
            {
                GUILayout.Label("All " + CustomPresetCount + " custom slots are used - delete one (X) to add another.", panelHintStyle);
            }

            GUILayout.Space(8f);
            GUILayout.Label("Saved views", panelHeaderStyle);
            for (int i = 0; i < ViewSlotCount; i++)
            {
                bool hasData = !string.IsNullOrEmpty(viewSlots[i].Value);
                GUILayout.BeginHorizontal();
                GUILayout.Label("Slot " + (i + 1) + DescribeViewSlot(i, hasData), GUILayout.ExpandWidth(true));
                GUI.enabled = povEnabled;
                if (GUILayout.Button("Save", GUILayout.Width(56f)))
                    SaveViewSlot(i);
                GUI.enabled = povEnabled && hasData;
                if (GUILayout.Button("Load", GUILayout.Width(56f)))
                    LoadViewSlot(i);
                GUI.enabled = true;
                GUILayout.EndHorizontal();
            }
            if (!povEnabled)
                GUILayout.Label("Views store the exact look direction and zoom - enter POV (" + ToggleKey.Value + ") to use them.", panelHintStyle);

            if (isStudio && hiddenHeadChars > 0)
            {
                GUILayout.Space(8f);
                GUILayout.Label("Scene repair", panelHeaderStyle);
                if (GUILayout.Button("Bring back hidden heads (" + hiddenHeadChars + ")"))
                {
                    int repaired = ScanHiddenHeads(true);
                    hiddenHeadChars = 0;
                    Logger.LogMessage(repaired > 0
                        ? "Heads restored on " + repaired + " character(s) - save the scene to keep it"
                        : "No hidden heads found");
                }
                GUILayout.Label("Versions up to 1.3.2 could save a hidden head into the scene file if you saved while in POV. This puts it back - save the scene afterwards.", panelHintStyle);
            }

            presetNameFocused = GUI.GetNameOfFocusedControl() == PresetNameControl;

            GUI.DragWindow();
        }

        private string DescribeViewSlot(int slot, bool hasData)
        {
            if (!hasData)
                return "  (empty)";
            try
            {
                string[] parts = viewSlots[slot].Value.Split(';');
                return "  (FOV " + Mathf.RoundToInt(float.Parse(parts[0], CultureInfo.InvariantCulture)) + ")";
            }
            catch (Exception)
            {
                return "";
            }
        }

        private string SerializeCurrentPreset(string name)
        {
            float currentFov = povEnabled ? fov : DefaultFov.Value;
            return string.Join(";", new[]
            {
                name.Replace(";", ","),
                currentFov.ToString(CultureInfo.InvariantCulture),
                PositionSmoothing.Value.ToString(CultureInfo.InvariantCulture),
                ForwardOffset.Value.ToString(CultureInfo.InvariantCulture),
                UpOffset.Value.ToString(CultureInfo.InvariantCulture)
            });
        }

        private void ApplyCustomPreset(string[] parts)
        {
            try
            {
                ApplyViewPreset(
                    Mathf.Clamp(float.Parse(parts[1], CultureInfo.InvariantCulture), 20f, 120f),
                    float.Parse(parts[2], CultureInfo.InvariantCulture),
                    float.Parse(parts[3], CultureInfo.InvariantCulture));
                if (parts.Length > 4)
                    UpOffset.Value = float.Parse(parts[4], CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                Logger.LogMessage("This preset couldn't be read, delete it and save it again");
            }
        }

        private void TryAddStudioToolbarButton()
        {
            if (!isStudio)
                return;
            try
            {
                // KKAPI's assembly is named KKAPI in Koikatsu but KKSAPI in Koikatsu Sunshine,
                // so a compile-time reference would break the one-DLL-for-both-games setup;
                // resolved via reflection instead. Queues internally until the studio loads.
                Type buttons = Type.GetType("KKAPI.Studio.UI.CustomToolbarButtons, KKAPI")
                            ?? Type.GetType("KKAPI.Studio.UI.CustomToolbarButtons, KKSAPI");
                if (buttons == null)
                    return; // no KKAPI: the panel hotkey still works
                MethodInfo add = buttons.GetMethod("AddLeftToolbarToggle", BindingFlags.Public | BindingFlags.Static);
                if (add == null)
                    return;
                toolbarToggle = add.Invoke(null, new object[] { MakePanelIcon(), false, (Action<bool>)SetPanelVisible });
                if (toolbarToggle != null)
                    toolbarToggleValue = toolbarToggle.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
            }
            catch (Exception e)
            {
                Logger.LogWarning("Couldn't add the Studio toolbar button (" + e.Message + "), use the panel hotkey instead");
            }
        }

        private static Texture2D MakePanelIcon()
        {
            // Pixel-matched to the actual vanilla toolbar sprites (values sampled from
            // "sp_sn_11_00_03" / the undo button in CharaStudio sharedassets1.assets):
            // a two-tone glossy plate: flat light-grey top half, quick falloff around
            // the middle, flat dark bottom half, a 1px lighter slightly-translucent
            // border, and a white anti-aliased glyph. KKAPI swaps the whole button
            // sprite for this texture, so the plate must be baked in.
            const int size = 32;
            var tex = new Texture2D(size, size, TextureFormat.ARGB32, false);
            for (int y = 0; y < size; y++) // SetPixel y = 0 is the BOTTOM row
            {
                int rowFromTop = size - 1 - y;
                float t = Mathf.InverseLerp(13f, 19f, rowFromTop);
                float fill = Mathf.Lerp(103f, 70f, t) / 255f;
                float border = Mathf.Lerp(131f, 116f, t) / 255f;
                for (int x = 0; x < size; x++)
                {
                    bool edgeX = x == 0 || x == size - 1;
                    bool edgeY = rowFromTop == 0 || rowFromTop == size - 1;
                    float shade = edgeX || edgeY ? border : fill;
                    float alpha = edgeX && edgeY ? 122f / 255f : edgeX || edgeY ? 236f / 255f : 1f;

                    // white eye glyph: almond outline (intersection of two discs, pointed
                    // left/right corners; R=(h+w^2/h)/2 with w=12, h=7.5) + solid pupil,
                    // ~1px smoothstep anti-aliasing
                    float px = x + 0.5f - 16f;
                    float py = y + 0.5f - 16f;
                    const float R = 13.35f, off = 5.85f;
                    float d1 = new Vector2(px, py + off).magnitude - R;
                    float d2 = new Vector2(px, py - off).magnitude - R;
                    float ring = Mathf.Abs(Mathf.Max(d1, d2)) - 1.6f;
                    float pupil = Mathf.Sqrt(px * px + py * py) - 3.9f;
                    float glyphA = Mathf.Clamp01(0.5f - Mathf.Min(ring, pupil));
                    shade = Mathf.Lerp(shade, 1f, glyphA);

                    tex.SetPixel(x, y, new Color(shade, shade, shade, alpha));
                }
            }
            tex.Apply();
            return tex;
        }

        private void InitViewFromHead()
        {
            torsoBone = null; // new character, or a fresh start on the same one
            torsoSearched = false;
            cameraShift = Vector3.zero;
            if (chara.objHeadBone)
                SetLookDirection(chara.objHeadBone.transform.forward);
            smoothPosInit = false;
            swayInit = false;
            bodyRollInit = false;
            vrAlignPending = true; // only used in VR
        }

        // Points the view along a world direction, as yaw/pitch in whatever frame they're measured in.
        private void SetLookDirection(Vector3 worldDir)
        {
            Vector3 f = Quaternion.Inverse(ResetLookFrame()) * worldDir;
            yaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
            pitch = Mathf.Clamp(-Mathf.Asin(Mathf.Clamp(f.y, -1f, 1f)) * Mathf.Rad2Deg, -PitchLimit.Value, PitchLimit.Value);
        }

        // With "Look relative to body" the torso already carries the body's tilt, so the
        // head-based roll from "Align camera with body" would tilt the view twice.
        private bool UseBodyRoll()
        {
            return AlignWithBody.Value && !LookRelativeToBody.Value;
        }

        // The frame yaw/pitch are measured in: the world (identity), or with "Look relative to body"
        // the upper torso, smoothed like the camera position so breathing and animation bob don't
        // shake the view. Holds still while the camera is locked.
        private Quaternion UpdateLookFrame(float dt)
        {
            if (!LookRelativeToBody.Value)
                return Quaternion.identity;
            if (!torsoBone)
                return ResetLookFrame();
            if (!camLocked)
            {
                float smoothing = PositionSmoothing.Value;
                torsoFrame = smoothing <= 0f
                    ? torsoBone.rotation
                    : Quaternion.Slerp(torsoFrame, torsoBone.rotation, 1f - Mathf.Exp(-Mathf.Lerp(70f, 4f, smoothing) * dt));
            }
            return torsoFrame;
        }

        private Quaternion ResetLookFrame()
        {
            if (!LookRelativeToBody.Value || !chara)
                return Quaternion.identity;
            Transform torso = GetTorsoBone();
            torsoFrame = torso ? torso.rotation : Quaternion.identity;
            return torsoFrame;
        }

        private Transform GetTorsoBone()
        {
            if (!torsoBone && !torsoSearched && chara)
            {
                // once per character: a rig without the bone just falls back to the world frame
                torsoSearched = true;
                torsoBone = FindTorsoBone(chara);
            }
            return torsoBone;
        }

        // cf_j_spine03 is the upper chest, the same bone KK_VR anchors to. Looked up by name as a
        // fallback in case a rig nests it differently.
        private static Transform FindTorsoBone(ChaControl c)
        {
            if (!c || !c.objBodyBone)
                return null;
            Transform root = c.objBodyBone.transform;
            Transform bone = root.Find("cf_n_height/cf_j_hips/cf_j_spine01/cf_j_spine02/cf_j_spine03");
            if (bone)
                return bone;
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                if (t.name == "cf_j_spine03")
                    return t;
            return null;
        }

        private void RebuildCharaList()
        {
            charaList = FindObjectsOfType<ChaControl>().Where(c => c).ToList();
        }

        private bool IsValidPovTarget(ChaControl c)
        {
            if (!c || !c.objTop || !c.objTop.activeInHierarchy)
                return false;
            // in these H modes the male is not posed, entering his POV makes no sense
            if (c.sex == 0 && hFlag &&
                ((int)hFlag.mode == 0 || hFlag.mode == HFlag.EMode.lesbian || hFlag.mode == HFlag.EMode.masturbation))
                return false;
            return true;
        }

        private ChaControl FindPovCharacter()
        {
            if (isStudio)
            {
                var selected = Singleton<global::Studio.GuideObjectManager>.Instance.selectObjectKey
                    .Select(x => global::Studio.Studio.GetCtrlInfo(x) as global::Studio.OCIChar)
                    .Where(x => x != null)
                    .ToList();
                if (selected.Count > 0)
                    return selected[0].charInfo;
            }

            RebuildCharaList();
            return charaList.FirstOrDefault(IsValidPovTarget);
        }

        // Always applies, never short-circuits on cursorLocked: the game locks the cursor behind
        // our back too (Studio.CameraControl does it whenever its camera is dragged), so an
        // unlock request has to go through even when we don't think we hold the lock.
        private void SetCursorLock(bool locked)
        {
            cursorLocked = locked;
            try
            {
                if (Singleton<GameCursor>.IsInstance())
                {
                    Singleton<GameCursor>.Instance.SetCursorLock(locked);
                    return;
                }
            }
            catch (Exception)
            {
                // fall through to the plain Unity cursor below
            }
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }
    }
}

// Read via reflection (matched by type name) by the ConfigurationManager plugin, which both
// KK and KKS repacks ship. No hard dependency: without ConfigurationManager these are ignored
// and the preset buttons simply don't appear anywhere.
internal sealed class ConfigurationManagerAttributes
{
    public System.Action<BepInEx.Configuration.ConfigEntryBase> CustomDrawer;
    public bool? HideDefaultButton;
    public bool? IsAdvanced;
    public int? Order;
}
