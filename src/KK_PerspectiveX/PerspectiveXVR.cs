using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using UnityEngine;

namespace PerspectiveX
{
    // VR support. In VR the headset owns the view rotation (overriding it makes people sick), so
    // POV here doesn't touch the camera at all. It moves the VR rig, the object the headset's
    // tracked space hangs off, so that your head rides along with the character's eyes.
    //
    // Two kinds of VR are covered, both reached only by reflection so the one DLL still loads in
    // every flavour of the game:
    //  - the VR mods (KK_MainGameVR, KKCharaStudioVR, KKS_VR main game and studio). All are built
    //    on VRGIN, each with its own copy under a different assembly name, and expose the rig as
    //    VRGIN.Core.VR.Camera.Origin with the headset at .Head.
    //  - the official VR builds (KoikatuVR.exe, KoikatsuSunshine_VR.exe). Their own
    //    VRViveCameraManager has the rig as objMove and the headset camera as objCamera.
    public partial class PerspectiveXPlugin
    {
        // how close (m) the headset has to get to the middle of a character's head to enter POV
        private const float VrEnterRadius = 0.09f;
        // how long (s) it has to stay there, so passing through a head doesn't grab you
        private const float VrEnterDwell = 0.15f;
        // how far (m) you have to lean or step out of the eyes to leave POV
        private const float VrExitDistance = 0.35f;

        private static readonly string[] VrModGuids =
        {
            "kk.vr.game", "kks.vr.game", "KKS_CharaStudioVR", "KKCharaStudioVRPlugin.KKCharaStudioVRPlugin"
        };

        // Main game and Studio share one config file but run separate VR mods, so each gets its own switch
        private ConfigEntry<bool> VrSupportMain { get; set; }
        private ConfigEntry<bool> VrSupportStudio { get; set; }
        private ConfigEntry<bool> VrHeadEntry { get; set; }
        // VR gets its own camera placement, a headset sits and feels different from a flat camera
        private ConfigEntry<float> VrForwardOffset { get; set; }
        private ConfigEntry<float> VrUpOffset { get; set; }
        private ConfigEntry<float> VrNearClip { get; set; }
        private ConfigEntry<bool> HideHeadVrMain { get; set; }
        private ConfigEntry<bool> HideHeadVrStudio { get; set; }
        private ConfigEntry<KeyboardShortcut> VrFollowCycleKey { get; set; }

        private readonly List<Type> vrginTypes = new List<Type>();
        private Type officialVrRigType;
        private PropertyInfo vrModPovActive;
        private bool vrDetectionHooked;
        private GUIStyle vrWarnStyle;

        // the rig as last found, whether or not POV is using it right now
        private Transform vrOrigin;
        private Transform vrHead;
        private Camera vrCam;
        private float nextVrRigSearch;

        private bool vrPov;
        private bool vrAlignPending;
        private bool vrFollowInit;
        private bool vrLeftHead;
        private Vector3 vrPrevTarget;
        private Vector3 vrHeadStartInRig;
        private Vector3 vrOrigPos;
        private Quaternion vrOrigRot;
        private int vrLastFrame = -1;

        // Rig position/rotation we wrote last frame; used to detect the VR mod moving the
        // rig itself (world grab / locomotion / turning).
        private Vector3 vrAppliedPos;
        private Quaternion vrAppliedRot = Quaternion.identity;
        private bool vrAppliedValid;
        private float vrDriftMove;
        private float vrDriftTurn;
        // Just left POV: the headset has to actually leave the head before POV can be
        // entered again, otherwise the player gets trapped in an exit/re-enter loop.
        private bool vrEntryLatch;
        // While in POV, dragging into another character's head switches to them.
        private ChaControl vrSwitchCandidate;
        private float vrSwitchSince;

        private ChaControl vrEntryTarget;
        private ChaControl vrEnterCandidate;
        private float vrEnterSince;
        private float nextVrCharaScan;

        // Rotation follow (by HanYuanyi): besides position, the rig can also turn with the character's
        // head. 0 = position only, 1 = + yaw, 2 = + pitch, 3 = + yaw and pitch, 4 = + roll as well.
        private const int VrFollowModeCount = 5;
        private int vrFollowMode;
        private Quaternion vrBaseHeadRot;
        private bool vrBaseValid;
        private Quaternion vrPrevFilteredDelta = Quaternion.identity;
        // where in the rig your head was when following started; the rig turns around this point
        // so the turn carries your head along instead of swinging it out of the character's
        private Vector3 vrFollowPivot;

        private void BindVrSettings()
        {
            const string vrSupportInfo = "In VR, POV puts your head at the character's eyes and you look around with the headset. " +
                "Works with KK_MainGameVR, KKCharaStudioVR, KKS_VR and the official VR games. Off = POV works like older versions, flat screen only.";
            VrSupportMain = Config.Bind("VR", "Use POV in VR (main game)", true,
                new ConfigDescription(vrSupportInfo, null, new ConfigurationManagerAttributes { CustomDrawer = DrawVrToggle, Order = 8 }));
            VrSupportStudio = Config.Bind("VR", "Use POV in VR (Studio)", true,
                new ConfigDescription(vrSupportInfo, null, new ConfigurationManagerAttributes { CustomDrawer = DrawVrToggle, Order = 7 }));
            VrHeadEntry = Config.Bind("VR", "Enter POV by moving into a head", true,
                new ConfigDescription("Put your head inside a character's head for a moment to become them, lean or step out to leave. No keyboard needed. " +
                    "The POV toggle key still works too.",
                    null, new ConfigurationManagerAttributes { CustomDrawer = DrawVrToggle, Order = 6 }));
            VrForwardOffset = Config.Bind("VR", "Forward offset", 0.05f,
                new ConfigDescription("VR only. Headset distance in front of the eyes, in meters. Negative moves you back into the head. The flat-screen offset is under Camera.",
                    new AcceptableValueRange<float>(-0.1f, 0.2f), new ConfigurationManagerAttributes { Order = 5 }));
            VrUpOffset = Config.Bind("VR", "Up offset", 0f,
                new ConfigDescription("VR only. Headset height above the eye line, in meters. The flat-screen offset is under Camera.",
                    new AcceptableValueRange<float>(-0.1f, 0.1f), new ConfigurationManagerAttributes { Order = 4 }));
            VrNearClip = Config.Bind("VR", "Near clip plane", 0.01f,
                new ConfigDescription("VR only. Lower values let very close geometry render without being cut off. 0 = leave the VR mod's own setting alone.",
                    new AcceptableValueRange<float>(0f, 0.1f), new ConfigurationManagerAttributes { Order = 3 }));
            HideHeadVrMain = Config.Bind("VR", "Hide character head (main game)", true,
                new ConfigDescription("Main game VR only. On by default because the main game VR mod doesn't hide the head itself.",
                    null, new ConfigurationManagerAttributes { Order = 2 }));
            HideHeadVrStudio = Config.Bind("VR", "Hide character head (Studio)", false,
                new ConfigDescription("Studio VR only. Off by default because KKCharaStudioVR hides the head itself, and both doing it can clash.",
                    null, new ConfigurationManagerAttributes { Order = 1 }));
            VrFollowCycleKey = Config.Bind("Keyboard shortcuts", "VR rotation follow", new KeyboardShortcut(KeyCode.V),
                "VR only. Cycles what the headset follows besides the eyes' position: position only > + yaw > + pitch > + yaw and pitch > + yaw, pitch and roll > position only. " +
                "Can also be put on a controller button under VR controller mapping.");
            BindVrControllerSettings();

            try
            {
                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                    NoteVrAssembly(asm);
                // VR mods can load after us, so keep an eye on assemblies loaded later too
                AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
                vrDetectionHooked = true;
            }
            catch (Exception)
            {
                // no VR detection means no VR POV, the flat POV is unaffected
            }
        }

        private void StopVrDetection()
        {
            if (vrDetectionHooked)
                AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
            vrDetectionHooked = false;
        }

        private void OnAssemblyLoad(object sender, AssemblyLoadEventArgs e)
        {
            NoteVrAssembly(e.LoadedAssembly);
        }

        private void NoteVrAssembly(Assembly asm)
        {
            try
            {
                string name = asm.GetName().Name;
                if (name.StartsWith("VRGIN", StringComparison.OrdinalIgnoreCase))
                {
                    Type t = asm.GetType("VRGIN.Core.VR", false);
                    if (t != null)
                        vrginTypes.Add(t);
                }
                else if (name == "Assembly-CSharp")
                {
                    // only exists in the official VR builds
                    officialVrRigType = asm.GetType("VRViveCameraManager", false);
                }
                else if (vrModPovActive == null)
                {
                    // KK_MainGameVR / KKS_VR's own H-scene POV, see IsVrModPovActive
                    Type pov = asm.GetType("KK_VR.Features.PoV", false);
                    if (pov != null)
                        vrModPovActive = pov.GetProperty("Active", BindingFlags.Public | BindingFlags.Static);
                }
            }
            catch (Exception)
            {
                // dynamic or odd assemblies, not ours to care about
            }
        }

        private bool IsVrEnabled()
        {
            return isStudio ? VrSupportStudio.Value : VrSupportMain.Value;
        }

        // head hiding has its own switch for each of the three cases
        private bool ShouldHideHead()
        {
            if (!vrPov)
                return HideHead.Value;
            return isStudio ? HideHeadVrStudio.Value : HideHeadVrMain.Value;
        }

        private bool IsVrModInstalled()
        {
            if (vrginTypes.Count > 0 || officialVrRigType != null)
                return true;
            try
            {
                for (int i = 0; i < VrModGuids.Length; i++)
                    if (Chainloader.PluginInfos.ContainsKey(VrModGuids[i]))
                        return true;
            }
            catch (Exception)
            {
            }
            return false;
        }

        private string RequiredVrModName()
        {
            // KK runs on Unity 5.6, KKS on 2019, and CharaStudio has the same process name in both
            bool kks = !Application.unityVersion.StartsWith("5.", StringComparison.Ordinal);
            if (kks)
                return "KKS_VR";
            return isStudio ? "KKCharaStudioVR" : "KK_MainGameVR";
        }

        // Drawn by ConfigurationManager in place of its normal checkbox for the VR settings. Without a
        // VR mod the setting can't do anything, so it shows as off with a short red note saying why.
        private void DrawVrToggle(ConfigEntryBase entry)
        {
            var setting = (ConfigEntry<bool>)entry;
            if (IsVrModInstalled())
            {
                setting.Value = GUILayout.Toggle(setting.Value, setting.Value ? "Enabled" : "Disabled", GUILayout.ExpandWidth(true));
                return;
            }

            if (vrWarnStyle == null)
            {
                vrWarnStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
                vrWarnStyle.normal.textColor = new Color(1f, 0.35f, 0.35f);
            }
            GUILayout.BeginHorizontal();
            bool wasEnabled = GUI.enabled;
            GUI.enabled = false;
            GUILayout.Toggle(false, "Disabled", GUILayout.ExpandWidth(false));
            GUI.enabled = wasEnabled;
            GUILayout.Label(new GUIContent("! Needs " + RequiredVrModName(), "No VR mod found. Install " + RequiredVrModName() + " to use POV in VR."),
                vrWarnStyle, GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();
        }

        // Finds the rig of whatever VR is running. Searching is throttled to once a second unless
        // forced, since this also runs every frame for head entry.
        private bool FindVrRig(bool force)
        {
            if (vrOrigin && vrHead && vrCam)
                return true;
            if (vrginTypes.Count == 0 && officialVrRigType == null)
                return false;
            if (!force && Time.unscaledTime < nextVrRigSearch)
                return false;
            nextVrRigSearch = Time.unscaledTime + 1f;

            vrOrigin = null;
            vrHead = null;
            vrCam = null;
            try
            {
                for (int i = 0; i < vrginTypes.Count; i++)
                    if (TryVrginRig(vrginTypes[i]))
                        return true;
                if (officialVrRigType != null)
                    return TryOfficialVrRig();
            }
            catch (Exception)
            {
            }
            return false;
        }

        // Only a VR mod that actually switched the game into VR sets VR.Active. Checking it first
        // matters: VRGIN's VR.Camera getter creates a brand new VR camera when there isn't one,
        // so touching it in a normal flat-screen session would spawn one.
        private bool TryVrginRig(Type vr)
        {
            PropertyInfo active = vr.GetProperty("Active", BindingFlags.Public | BindingFlags.Static);
            if (active == null || !(bool)active.GetValue(null, null))
                return false;
            Component vrCamera = vr.GetProperty("Camera", BindingFlags.Public | BindingFlags.Static).GetValue(null, null) as Component;
            if (!vrCamera)
                return false;
            Type t = vrCamera.GetType();
            // Origin is the rig root the VR mods move you around with, Head the tracked headset
            // inside it, and the camera rendering the headset view sits on VR.Camera itself
            return SetVrRig(t.GetProperty("Origin", BindingFlags.Public | BindingFlags.Instance).GetValue(vrCamera, null) as Transform,
                t.GetProperty("Head", BindingFlags.Public | BindingFlags.Instance).GetValue(vrCamera, null) as Transform,
                vrCamera.GetComponent<Camera>());
        }

        private bool TryOfficialVrRig()
        {
            UnityEngine.Object manager = FindObjectOfType(officialVrRigType);
            if (!manager)
                return false;
            GameObject move = officialVrRigType.GetField("objMove", BindingFlags.Public | BindingFlags.Instance).GetValue(manager) as GameObject;
            GameObject headset = officialVrRigType.GetField("objCamera", BindingFlags.Public | BindingFlags.Instance).GetValue(manager) as GameObject;
            if (!move || !headset)
                return false;
            return SetVrRig(move.transform, headset.transform, headset.GetComponent<Camera>());
        }

        private bool SetVrRig(Transform origin, Transform head, Camera c)
        {
            if (!origin || !head || !c)
                return false;
            vrOrigin = origin;
            vrHead = head;
            vrCam = c;
            return true;
        }

        // KK_MainGameVR and KKS_VR have an H-scene POV of their own that also moves the rig every
        // frame, and the two would fight over the headset.
        private bool IsVrModPovActive()
        {
            try
            {
                return vrModPovActive != null && (bool)vrModPovActive.GetValue(null, null);
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Returns the character whose head currently contains the headset, or null.
        private ChaControl FindHeadContaining()
        {
            if (!vrHead || charaList == null)
                return null;
            Vector3 headset = vrHead.position;
            for (int i = 0; i < charaList.Count; i++)
            {
                ChaControl c = charaList[i];
                if (!IsValidPovTarget(c) || !c.objHeadBone)
                    continue;
                Transform headT = c.objHeadBone.transform;
                Vector3 center = GetEyeMidpoint(c, headT) - headT.forward * 0.06f;
                if ((center - headset).sqrMagnitude < VrEnterRadius * VrEnterRadius)
                    return c;
            }
            return null;
        }

        // While already in POV, dragging the world so the headset enters another character's
        // head switches to that character. This used to run only while NOT in POV, so dragging
        // into a second character hid its head but kept tracking the first one.
        private void UpdateVrPovSwitch()
        {
            if (!IsVrEnabled() || !VrHeadEntry.Value || !FindVrRig(false) || IsVrModPovActive())
            {
                vrSwitchCandidate = null;
                return;
            }
            if (charaList == null || Time.unscaledTime >= nextVrCharaScan)
            {
                nextVrCharaScan = Time.unscaledTime + 1f;
                RebuildCharaList();
            }

            ChaControl inside = FindHeadContaining();
            if (!inside || inside == chara)
            {
                vrSwitchCandidate = null;
                return;
            }
            if (inside != vrSwitchCandidate)
            {
                vrSwitchCandidate = inside;
                vrSwitchSince = Time.unscaledTime;
                return;
            }
            if (Time.unscaledTime - vrSwitchSince < VrEnterDwell)
                return;

            vrSwitchCandidate = null;
            SwitchTo(inside);
            RecordVrFollowBase();   // re-base the follow pivot on the new character's head
        }

        // Head entry: while not in POV, watch for the headset sitting inside a character's head.
        private void UpdateVrHeadEntry()
        {
            if (!IsVrEnabled() || !VrHeadEntry.Value || !FindVrRig(false) || IsVrModPovActive())
            {
                vrEnterCandidate = null;
                return;
            }

            if (charaList == null || Time.unscaledTime >= nextVrCharaScan)
            {
                nextVrCharaScan = Time.unscaledTime + 1f;
                RebuildCharaList();
            }

            ChaControl inside = null;
            Vector3 headset = vrHead.position;
            for (int i = 0; i < charaList.Count; i++)
            {
                ChaControl c = charaList[i];
                if (!IsValidPovTarget(c) || !c.objHeadBone)
                    continue;
                // the middle of the head, a bit behind the eyes, so leaning in close to a face
                // (kissing) doesn't count, only actually being inside the head does
                Transform headT = c.objHeadBone.transform;
                Vector3 center = GetEyeMidpoint(c, headT) - headT.forward * 0.06f;
                if ((center - headset).sqrMagnitude < VrEnterRadius * VrEnterRadius)
                {
                    inside = c;
                    break;
                }
            }

            // Right after leaving POV the headset is often still inside the character's head
            // (especially when POV was entered by dragging the world), so without this the mod
            // would re-enter 0.15 s later and the player could not get out.
            if (vrEntryLatch)
            {
                vrEnterCandidate = null;
                if (inside == null)
                    vrEntryLatch = false;
                return;
            }

            if (inside != vrEnterCandidate)
            {
                vrEnterCandidate = inside;
                vrEnterSince = Time.unscaledTime;
                return;
            }
            if (inside && Time.unscaledTime - vrEnterSince >= VrEnterDwell)
            {
                vrEnterCandidate = null;
                vrEntryTarget = inside;
                EnablePov();
            }
        }

        private void BeginVrPov()
        {
            // remember where the rig was so leaving POV with the toggle key puts you back there
            vrOrigPos = vrOrigin.position;
            vrOrigRot = vrOrigin.rotation;
            vrFollowInit = false;
            vrLeftHead = false;
            vrLastFrame = -1;
            vrEnterCandidate = null;
            ResetVrFollow();
        }

        private void ResetVrFollow()
        {
            vrFollowMode = 0;
            vrBaseValid = false;
            vrPrevFilteredDelta = Quaternion.identity;
        }

        private void RecordVrFollowBase()
        {
            vrBaseValid = chara && chara.objHeadBone && vrOrigin && vrHead;
            if (!vrBaseValid)
                return;
            vrBaseHeadRot = chara.objHeadBone.transform.rotation;
            vrPrevFilteredDelta = Quaternion.identity;
            vrFollowPivot = vrOrigin.InverseTransformPoint(vrHead.position);
        }

        private void CycleVrFollow()
        {
            if (!chara || !chara.objHeadBone || !vrOrigin || !vrHead)
                return;
            int next = (vrFollowMode + 1) % VrFollowModeCount;
            if (next != 0 && !vrBaseValid)
            {
                RecordVrFollowBase();
                if (!vrBaseValid)
                {
                    Logger.LogMessage("VR rotation follow: no character or VR rig");
                    return;
                }
            }
            if (next != 0)
            {
                // pick up from how the head is turned right now, so switching modes doesn't jerk the view
                vrPrevFilteredDelta = FilterFollowRotation(chara.objHeadBone.transform.rotation * Quaternion.Inverse(vrBaseHeadRot), next);
                vrFollowPivot = vrOrigin.InverseTransformPoint(vrHead.position);
            }
            vrFollowMode = next;
            Logger.LogMessage("VR rotation follow: " + VrFollowModeName(next));
        }

        private static string VrFollowModeName(int mode)
        {
            switch (mode)
            {
                case 1: return "position + yaw";
                case 2: return "position + pitch";
                case 3: return "position + yaw + pitch";
                case 4: return "position + yaw + pitch + roll";
                default: return "position only";
            }
        }

        // Keeps only the parts of the head's turn that the mode follows. The turn is split into yaw,
        // pitch and roll as the character sees it rather than along the world axes, so pitch is a nod
        // whichever way the character happens to face.
        private Quaternion FilterFollowRotation(Quaternion delta, int mode)
        {
            if (mode == 0)
                return Quaternion.identity;
            if (mode >= 4)
                return delta;
            Quaternion frame = GetFacingFrame();
            Vector3 e = (Quaternion.Inverse(frame) * delta * frame).eulerAngles;
            Quaternion kept;
            if (mode == 1)
                kept = Quaternion.Euler(0f, e.y, 0f);
            else if (mode == 2)
                kept = Quaternion.Euler(e.x, 0f, 0f);
            else
                kept = Quaternion.Euler(e.x, e.y, 0f);
            return frame * kept * Quaternion.Inverse(frame);
        }

        private void UpdateVrFollowRotation()
        {
            if (vrFollowMode == 0 || !vrBaseValid || !chara.objHeadBone)
                return;
            Quaternion filtered = FilterFollowRotation(chara.objHeadBone.transform.rotation * Quaternion.Inverse(vrBaseHeadRot), vrFollowMode);
            // a locked camera holds still; the turn it skipped is dropped rather than caught up on unlock
            if (!camLocked)
            {
                Quaternion step = filtered * Quaternion.Inverse(vrPrevFilteredDelta);
                Vector3 pivotBefore = vrOrigin.TransformPoint(vrFollowPivot);
                vrOrigin.rotation = step * vrOrigin.rotation;
                vrOrigin.position += pivotBefore - vrOrigin.TransformPoint(vrFollowPivot);
            }
            vrPrevFilteredDelta = filtered;
        }

        private bool CheckVrPovStillValid()
        {
            if (!vrOrigin || !vrHead || !vrCam)
                return false;
            if (IsVrModPovActive())
            {
                Logger.LogMessage("The VR mod's own POV took over the headset, PerspectiveX POV turned off");
                return false;
            }
            return !vrLeftHead;
        }

        // the VR camera culls once per eye; move the rig only on the first one, the second eye
        // then renders from the same spot
        private bool IsFirstVrCullThisFrame()
        {
            if (Time.frameCount == vrLastFrame || !vrOrigin || !vrHead)
                return false;
            vrLastFrame = Time.frameCount;
            return true;
        }

        private void PlaceVrRig()
        {
            DetectExternalRigMove();

            if (vrAlignPending)
            {
                // turn the rig around the headset so you face where the character's head faces
                Vector3 charaFwd = chara.objHeadBone.transform.forward;
                Vector3 headFwd = vrHead.forward;
                if (Mathf.Abs(charaFwd.y) < 0.99f && Mathf.Abs(headFwd.y) < 0.99f)
                {
                    float delta = Mathf.DeltaAngle(Mathf.Atan2(headFwd.x, headFwd.z) * Mathf.Rad2Deg,
                        Mathf.Atan2(charaFwd.x, charaFwd.z) * Mathf.Rad2Deg);
                    vrOrigin.RotateAround(vrHead.position, Vector3.up, delta);
                }
                vrAlignPending = false;
                vrFollowInit = false;
                // turning the head from here on is measured from the newly faced direction
                if (vrFollowMode != 0)
                    RecordVrFollowBase();
            }

            UpdateVrFollowRotation();

            if (!vrFollowInit)
            {
                // start exactly at the eyes
                vrOrigin.position += smoothPos - vrHead.position;
                vrHeadStartInRig = vrOrigin.InverseTransformPoint(vrHead.position);
                vrFollowInit = true;
            }
            else
            {
                // Carry the rig along with the eyes and leave your own head movement alone.
                // Nothing pulls you back toward the eyes: dragging your real head motion around
                // makes people sick, it undid controller moves (like nudging the view out of a
                // body it clips into) and it made head shakes drift.
                vrOrigin.position += smoothPos - vrPrevTarget;

                // Leaving POV is about your real head only: how far it has moved inside the play
                // space since POV started. Rig moves from the VR mod's controls (moving, turning)
                // are left out, so fixing up the view with the controller doesn't kick you out.
                Vector3 leaned = vrOrigin.TransformVector(vrOrigin.InverseTransformPoint(vrHead.position) - vrHeadStartInRig);
                if (leaned.sqrMagnitude > VrExitDistance * VrExitDistance)
                    vrLeftHead = true;
            }
            vrPrevTarget = smoothPos;
            if (VrNearClip.Value > 0f)
                vrCam.nearClipPlane = VrNearClip.Value;

            // Remember where we put the rig this frame, so the next frame can tell if
            // something else moved it.
            vrAppliedPos = vrOrigin.position;
            vrAppliedRot = vrOrigin.rotation;
            vrAppliedValid = true;
        }

        // The VR mod's world grab / locomotion / turning moves the rig directly, bypassing us.
        // Once the player has moved it that way, leaving POV should return to where they moved
        // to, not to where they were before entering.
        private void DetectExternalRigMove()
        {
            if (!vrAppliedValid || !vrOrigin)
                return;

            float moved = Vector3.Distance(vrOrigin.position, vrAppliedPos);
            float turned = Quaternion.Angle(vrOrigin.rotation, vrAppliedRot);
            if (moved < 0.0005f && turned < 0.05f)
                return;

            vrDriftMove += moved;
            vrDriftTurn += turned;
            if (vrDriftMove < 0.03f && vrDriftTurn < 5f)
                return;

            vrOrigPos = vrOrigin.position;
            vrOrigRot = vrOrigin.rotation;
            vrDriftMove = 0f;
            vrDriftTurn = 0f;
        }

        private void RestoreVrRig()
        {
            if (!vrPov || !vrOrigin)
                return;
            // stepping out of the head leaves you where you stepped to; only the toggle key
            // takes you back to where you were before
            if (vrLeftHead)
            {
                vrLeftHead = false;
                Logger.LogMessage("Left POV");
                return;
            }
            try
            {
                vrOrigin.SetPositionAndRotation(vrOrigPos, vrOrigRot);
            }
            catch (Exception)
            {
            }
        }
    }
}
