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
    public class PerspectiveXPlugin : BaseUnityPlugin
    {
        public const string GUID = "bucky.kk.perspectivex";
        public const string PluginName = "PerspectiveX";
        public const string Version = "1.3.0";

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

        private const int ViewSlotCount = 3;
        private ConfigEntry<KeyboardShortcut>[] saveSlotKeys;
        private ConfigEntry<KeyboardShortcut>[] loadSlotKeys;
        private ConfigEntry<string>[] viewSlots;

        private const int CustomPresetCount = 5;
        private const int PanelWindowId = 0x0BCC1;
        private ConfigEntry<string>[] customPresets;
        private bool panelVisible;
        private Rect panelRect;
        private bool panelRectInit;
        private string newPresetName = "";
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
        private DepthOfField dof;
        private bool dofChanged;
        private float dofOrigSize;
        private float dofOrigAperture;
        private Vector3 dofOrigFocalPos;
        private float origFov;
        private float origNearClip;
        private bool origVisibleHeadAlways;
        private bool origHeadActive;
        private bool[] origHairActive;

        private float yaw;
        private float pitch;
        private float fov;
        private float manualRoll;
        private bool camLocked;
        private float bodyRoll;
        private bool bodyRollInit;
        private bool dragging;
        private bool fpsMode;
        private Vector3 smoothPos;
        private bool smoothPosInit;
        private Quaternion swayBaseline;
        private bool swayInit;

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
                "Reset the manual camera tilt back to level.");
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
                new ConfigDescription("How far up/down you can look, in degrees.", new AcceptableValueRange<float>(30f, 89f)));
            ForwardOffset = Config.Bind("Camera", "Forward offset", 0.05f,
                new ConfigDescription("Camera distance in front of the eyes, in meters. Raise it if you can see the inside of the head.", new AcceptableValueRange<float>(0f, 0.2f)));
            UpOffset = Config.Bind("Camera", "Up offset", 0f,
                new ConfigDescription("Vertical camera offset from the eye line, in meters.", new AcceptableValueRange<float>(-0.1f, 0.1f)));
            NearClip = Config.Bind("Camera", "Near clip plane", 0.02f,
                new ConfigDescription("Lower values let very close geometry render without being cut off.", new AcceptableValueRange<float>(0.01f, 0.1f)));
            HideHead = Config.Bind("General", "Hide character head", true,
                "Hide the POV character's head (incl. hair and head accessories) so nothing clips into the view.");
            AlignWithBody = Config.Bind("Comfort", "Align camera with body", false,
                "Tilt the camera to follow the character's body orientation, so the view isn't kept artificially level when they're lying on their side or leaning. Fine-tune with the roll keys.");

            // apply settings that are only sampled on POV enter live as well
            DefaultFov.SettingChanged += (s, e) => fov = DefaultFov.Value;
            HideHead.SettingChanged += (s, e) =>
            {
                if (povEnabled && chara)
                    RestoreHeadVisibility(chara, !HideHead.Value && origVisibleHeadAlways);
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
            if (povEnabled)
                DisablePov();
        }

        private void Update()
        {
            // while an IMGUI text field has focus (our preset name box, ConfigurationManager's
            // search box, ...) typed characters must not trigger hotkeys
            bool typingInUi = GUIUtility.keyboardControl != 0;

            if (!typingInUi && PanelKey.Value.IsDown())
                SetPanelVisible(!panelVisible);

            // keep clicks/scroll on the panel from reaching the game, same trick
            // ConfigurationManager uses (cursor is free whenever the panel is usable)
            if (panelVisible && !fpsMode && !dragging && IsMouseOverPanel())
                Input.ResetInputAxes();

            if (!typingInUi && ToggleKey.Value.IsDown())
            {
                if (povEnabled)
                    DisablePov();
                else
                    EnablePov();
                return;
            }

            if (!povEnabled)
                return;

            if (!chara || !chara.objTop)
            {
                DisablePov();
                return;
            }

            if (!typingInUi && CyclePrevKey.Value.IsDown())
            {
                CycleCharacter(-1);
                return;
            }
            if (!typingInUi && CycleNextKey.Value.IsDown())
            {
                CycleCharacter(1);
                return;
            }

            if (!typingInUi && FpsModeKey.Value.IsDown())
            {
                fpsMode = !fpsMode;
                dragging = false;
                SetCursorLock(fpsMode);
            }

            if (!fpsMode)
            {
                if (!dragging && Input.GetMouseButtonDown(0) && !IsPointerOverUI() && !IsMouseOverPanel())
                {
                    dragging = true;
                    SetCursorLock(true);
                }
                if (dragging && !Input.GetMouseButton(0))
                {
                    dragging = false;
                    SetCursorLock(false);
                }
            }

            if (fpsMode || dragging)
            {
                float dx = Input.GetAxis("Mouse X") * MouseSensitivity.Value;
                float dy = -Input.GetAxis("Mouse Y") * MouseSensitivity.Value;

                // rotate the mouse delta by the current camera roll so the controls
                // stay screen-relative when the view is tilted
                float rollRad = (manualRoll + (AlignWithBody.Value ? bodyRoll : 0f)) * Mathf.Deg2Rad;
                float rollCos = Mathf.Cos(rollRad);
                float rollSin = Mathf.Sin(rollRad);

                yaw += dx * rollCos + dy * rollSin;
                if (yaw > 180f) yaw -= 360f;
                else if (yaw < -180f) yaw += 360f;
                pitch = Mathf.Clamp(pitch + dy * rollCos - dx * rollSin, -PitchLimit.Value, PitchLimit.Value);
            }

            if (ScrollFovEnabled.Value)
            {
                float scroll = Input.GetAxis("Mouse ScrollWheel");
                if (scroll != 0f && (fpsMode || dragging || (!IsPointerOverUI() && !IsMouseOverPanel())))
                    fov = Mathf.Clamp(fov - scroll * 15f, 20f, 120f);
            }

            if (typingInUi)
                return;

            bool rollLeft = RollLeftKey.Value.IsPressed();
            bool rollRight = RollRightKey.Value.IsPressed();
            if (RollResetKey.Value.IsDown())
            {
                manualRoll = 0f;
            }
            else if (rollLeft != rollRight)
            {
                manualRoll += (rollLeft ? 1f : -1f) * 60f * Time.deltaTime;
                if (manualRoll > 180f) manualRoll -= 360f;
                else if (manualRoll < -180f) manualRoll += 360f;
            }

            if (CamLockKey.Value.IsDown())
            {
                camLocked = !camLocked;
                if (!camLocked)
                    swayInit = false; // re-baseline so the sway doesn't jolt on unlock
                Logger.LogMessage(camLocked ? "Camera locked in place" : "Camera unlocked");
            }

            for (int i = 0; i < ViewSlotCount; i++)
            {
                if (saveSlotKeys[i].Value.IsDown())
                    SaveViewSlot(i);
                else if (loadSlotKeys[i].Value.IsDown())
                    LoadViewSlot(i);
            }
        }

        private static bool IsPointerOverUI()
        {
            return GUIUtility.hotControl != 0 ||
                   (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject());
        }

        private void OnCameraPreCull(Camera renderingCam)
        {
            if (!povEnabled || cam == null || renderingCam != cam)
                return;
            if (!chara || !chara.objHeadBone)
                return;

            Transform headT = chara.objHeadBone.transform;
            Quaternion headRot = headT.rotation;
            Vector3 targetPos = GetEyeMidpoint(headT) + headRot * new Vector3(0f, UpOffset.Value, ForwardOffset.Value);

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

            float roll = manualRoll;
            if (AlignWithBody.Value)
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

            Quaternion finalRot = Quaternion.Euler(pitch, yaw, roll);

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

        private Vector3 GetEyeMidpoint(Transform headT)
        {
            var eyeLookCtrl = chara.eyeLookCtrl;
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

        private void EnablePov()
        {
            Camera mainCam = Camera.main;
            if (!mainCam)
            {
                Logger.LogMessage("Can't enter POV: no main camera found");
                return;
            }

            ChaControl target = FindPovCharacter();
            if (!target)
            {
                Logger.LogMessage(isStudio
                    ? "Can't enter POV: select a character in the workspace first"
                    : "Can't enter POV: no suitable character found");
                return;
            }

            chara = target;
            cam = mainCam;

            disabledCameraControl = cam.GetComponent<CameraControl_Ver2>();
            if (!disabledCameraControl)
                disabledCameraControl = cam.GetComponent<global::Studio.CameraControl>();
            if (disabledCameraControl)
                disabledCameraControl.enabled = false;

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

            origFov = cam.fieldOfView;
            origNearClip = cam.nearClipPlane;
            if (fov <= 0f)
                fov = DefaultFov.Value;

            CaptureAndHideHead();

            InitViewFromHead();
            manualRoll = 0f;
            camLocked = false;
            dragging = false;
            fpsMode = false;
            povEnabled = true;
        }

        private void DisablePov()
        {
            povEnabled = false;

            RestoreHeadVisibility(chara, origVisibleHeadAlways);
            chara = null;

            if (cam)
            {
                cam.fieldOfView = origFov;
                cam.nearClipPlane = origNearClip;
            }
            if (disabledCameraControl)
                disabledCameraControl.enabled = true;
            disabledCameraControl = null;

            if (dofChanged && dof)
            {
                dof.focalSize = dofOrigSize;
                dof.aperture = dofOrigAperture;
                if (dof.focalTransform)
                    dof.focalTransform.localPosition = dofOrigFocalPos;
            }
            dof = null;
            dofChanged = false;
            cam = null;

            if (dragging || fpsMode)
                SetCursorLock(false);
            dragging = false;
            fpsMode = false;
            camLocked = false;
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
            RestoreHeadVisibility(chara, origVisibleHeadAlways);

            chara = next;
            CaptureAndHideHead();
            InitViewFromHead();
            camLocked = false;
        }

        private void CaptureAndHideHead()
        {
            origVisibleHeadAlways = chara.fileStatus.visibleHeadAlways;
            origHeadActive = chara.objHead && chara.objHead.activeSelf;
            GameObject[] hair = chara.objHair;
            origHairActive = hair == null ? null : hair.Select(h => h && h.activeSelf).ToArray();
            if (HideHead.Value)
                chara.fileStatus.visibleHeadAlways = false;
        }

        private void RestoreHeadVisibility(ChaControl c, bool visible)
        {
            if (!c)
                return;
            c.fileStatus.visibleHeadAlways = visible;
            if (!visible)
                return;
            try
            {
                // KK re-applies visibleHeadAlways every frame in LateUpdateForce/UpdateVisible,
                // but KKS doesn't re-show the head from the flag alone — force one refresh,
                // then reactivate whatever was active on POV enter and is still left inactive.
                c.LateUpdateForce();
                if (origHeadActive && c.objHead && !c.objHead.activeSelf)
                    c.objHead.SetActive(true);
                GameObject[] hair = c.objHair;
                if (hair != null && origHairActive != null)
                    for (int i = 0; i < hair.Length && i < origHairActive.Length; i++)
                        if (origHairActive[i] && hair[i] && !hair[i].activeSelf)
                            hair[i].SetActive(true);
            }
            catch
            {
                // KKS API drift must never break POV exit; the flag restore above already
                // covers KK.
            }
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
                return;

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
                Logger.LogWarning("Couldn't add the studio toolbar button (" + e.Message + "), use the panel hotkey instead");
            }
        }

        private static Texture2D MakePanelIcon()
        {
            // Pixel-matched to the actual vanilla toolbar sprites (values sampled from
            // "sp_sn_11_00_03" / the undo button in CharaStudio sharedassets1.assets):
            // a two-tone glossy plate — flat light-grey top half, quick falloff around
            // the middle, flat dark bottom half — a 1px lighter slightly-translucent
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
            if (chara.objHeadBone)
            {
                Vector3 f = chara.objHeadBone.transform.forward;
                yaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
                pitch = Mathf.Clamp(-Mathf.Asin(Mathf.Clamp(f.y, -1f, 1f)) * Mathf.Rad2Deg, -PitchLimit.Value, PitchLimit.Value);
            }
            smoothPosInit = false;
            swayInit = false;
            bodyRollInit = false;
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

        private static void SetCursorLock(bool locked)
        {
            if (Singleton<GameCursor>.IsInstance())
            {
                Singleton<GameCursor>.Instance.SetCursorLock(locked);
            }
            else
            {
                Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
                Cursor.visible = !locked;
            }
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
}
