using System.Collections.Generic;
using System.Linq;
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
        public const string Version = "1.2.1";

        private ConfigEntry<KeyboardShortcut> ToggleKey { get; set; }
        private ConfigEntry<KeyboardShortcut> CyclePrevKey { get; set; }
        private ConfigEntry<KeyboardShortcut> CycleNextKey { get; set; }
        private ConfigEntry<KeyboardShortcut> FpsModeKey { get; set; }
        private ConfigEntry<KeyboardShortcut> RollLeftKey { get; set; }
        private ConfigEntry<KeyboardShortcut> RollRightKey { get; set; }
        private ConfigEntry<KeyboardShortcut> RollResetKey { get; set; }
        private ConfigEntry<float> DefaultFov { get; set; }
        private ConfigEntry<float> MouseSensitivity { get; set; }
        private ConfigEntry<float> PositionSmoothing { get; set; }
        private ConfigEntry<float> HeadSway { get; set; }
        private ConfigEntry<float> ForwardOffset { get; set; }
        private ConfigEntry<float> UpOffset { get; set; }
        private ConfigEntry<float> PitchLimit { get; set; }
        private ConfigEntry<float> NearClip { get; set; }
        private ConfigEntry<bool> HideHead { get; set; }
        private ConfigEntry<bool> AlignWithBody { get; set; }

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

        private float yaw;
        private float pitch;
        private float fov;
        private float manualRoll;
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
            DefaultFov = Config.Bind("General", "Field of view", 60f,
                new ConfigDescription("Vertical FOV. Can also be adjusted with the scroll wheel while in POV.", new AcceptableValueRange<float>(20f, 120f)));
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
                    chara.fileStatus.visibleHeadAlways = !HideHead.Value && origVisibleHeadAlways;
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
            if (ToggleKey.Value.IsDown())
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

            if (CyclePrevKey.Value.IsDown())
            {
                CycleCharacter(-1);
                return;
            }
            if (CycleNextKey.Value.IsDown())
            {
                CycleCharacter(1);
                return;
            }

            if (FpsModeKey.Value.IsDown())
            {
                fpsMode = !fpsMode;
                dragging = false;
                SetCursorLock(fpsMode);
            }

            if (!fpsMode)
            {
                if (!dragging && Input.GetMouseButtonDown(0) && !IsPointerOverUI())
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

            float scroll = Input.GetAxis("Mouse ScrollWheel");
            if (scroll != 0f && (fpsMode || dragging || !IsPointerOverUI()))
                fov = Mathf.Clamp(fov - scroll * 15f, 20f, 120f);

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
            if (!smoothPosInit || smoothing <= 0f)
            {
                smoothPos = targetPos;
                smoothPosInit = true;
            }
            else
            {
                float followSpeed = Mathf.Lerp(70f, 4f, smoothing);
                smoothPos = Vector3.Lerp(smoothPos, targetPos, 1f - Mathf.Exp(-followSpeed * dt));
            }

            float roll = manualRoll;
            if (AlignWithBody.Value)
            {
                // roll of the head bone around its own forward axis, smoothed so
                // animation wobble doesn't rock the view; degenerates when the head
                // points straight up/down, so keep the last known roll there
                Vector3 headFwd2 = headRot * Vector3.forward;
                if (Mathf.Abs(headFwd2.y) < 0.99f)
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
            if (sway > 0f)
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

            origVisibleHeadAlways = chara.fileStatus.visibleHeadAlways;
            if (HideHead.Value)
                chara.fileStatus.visibleHeadAlways = false;

            InitViewFromHead();
            manualRoll = 0f;
            dragging = false;
            fpsMode = false;
            povEnabled = true;
        }

        private void DisablePov()
        {
            povEnabled = false;

            if (chara)
                chara.fileStatus.visibleHeadAlways = origVisibleHeadAlways;
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
            if (chara)
                chara.fileStatus.visibleHeadAlways = origVisibleHeadAlways;

            chara = next;
            origVisibleHeadAlways = chara.fileStatus.visibleHeadAlways;
            if (HideHead.Value)
                chara.fileStatus.visibleHeadAlways = false;
            InitViewFromHead();
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
