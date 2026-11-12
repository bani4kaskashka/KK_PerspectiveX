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
        // how closely (cosine of the angle) the headset has to face the same way as the character
        // before being inside her head counts as POV entry. 0.6 = within ~53 degrees, the same
        // threshold KKCharaStudioVR uses in its HasHead test.
        private const float VrEnterFacing = 0.6f;
        // how far (m) you have to lean or step out of the eyes to leave POV
        private const float VrExitDistance = 0.35f;
        // how far (m) the headset must be from the head centre before the head
        // becomes visible again while still in POV. Deliberately much larger than
        // VrEnterRadius: a small gap would make the head flicker while the rig
        // follows the character's head animation.
        private const float VrHeadShowDistance = 0.30f;

        private static readonly string[] VrModGuids =
        {
            "kk.vr.game", "kks.vr.game", "KKS_CharaStudioVR", "KKCharaStudioVRPlugin.KKCharaStudioVRPlugin"
        };

        // Main game and Studio share one config file but run separate VR mods, so each gets its own switch
        private ConfigEntry<bool> VrSupportMain { get; set; }
        private ConfigEntry<bool> VrSupportStudio { get; set; }
        private ConfigEntry<bool> VrHeadEntry { get; set; }
        private ConfigEntry<bool> VrExitByHeadMove { get; set; }
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
        private Func<bool> vrModPovActiveGetter;
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
        // true while the headset sits far enough from the head that the head is shown mid-POV.
        // Latching this gives the two thresholds hysteresis: show beyond VrHeadShowDistance,
        // hide again only once you are back inside VrEnterRadius - the same radius POV entry uses.
        private bool vrHeadShown;
        private Vector3 vrPrevTarget;
        private Vector3 vrHeadStartInRig;
        private Vector3 vrOrigPos;
        private Quaternion vrOrigRot;
        // Rig pose at the moment POV was entered. Same value as vrOrigPos, but never rewritten by dragging.
        // Used to restore the exit position when you drag the world while still inside her head (<30 cm).
        private Vector3 vrEntryPos;
        private Quaternion vrEntryRot = Quaternion.identity;
        // [2026-11-12] Her head rotation at the moment of alignment. Memory is saved against this
        // reference frame instead of re-reading her head rotation on exit: character animation keeps
        // changing it, so solving against the exit-time value folds "how far her head turned meanwhile"
        // into the player's adjustment every cycle, which accumulates into a view that rolls about itself.
        private Quaternion vrAlignHeadRot = Quaternion.identity;
        private bool vrAlignHeadRotValid;
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

        // ---- POV pose the player adjusted by hand, remembered per character ----
        // Read only on hotkey entry; written only when leaving POV or switching character, so drags
        // made after leaving are never recorded. Stored relative to her head rather than in world
        // space, so the offset follows her when she changes pose, turns or walks.
        private class VrUserOffset
        {
            public Quaternion rot;
            public Vector3 pos;
        }

        private readonly Dictionary<ChaControl, VrUserOffset> vrUserOffsets =
            new Dictionary<ChaControl, VrUserOffset>();
        private readonly List<ChaControl> deadOffsetKeys = new List<ChaControl>();

        // Whether this alignment should read the memory (set by EnablePov per entry type, consumed once)
        private bool vrAlignFromMemory;
        // Headset offset from her head during this POV session, in her head's frame; zero when unused
        private bool vrHasPosOffset;
        private Vector3 vrPosOffsetLocal;

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
            VrExitByHeadMove = Config.Bind("VR", "Leave POV by moving your real headset out", true,
                new ConfigDescription("VR only. Move your real headset out of the character's head to leave POV.",
                    null, new ConfigurationManagerAttributes { CustomDrawer = DrawVrToggle, Order = 5 }));
            VrForwardOffset = Config.Bind("VR", "Forward offset", 0f,
                new ConfigDescription("VR only. Headset distance in front of the eyes, in meters. Negative moves you back into the head. The flat-screen offset is under Camera.",
                    new AcceptableValueRange<float>(-0.1f, 0.2f), new ConfigurationManagerAttributes { Order = 4 }));
            VrUpOffset = Config.Bind("VR", "Up offset", 0f,
                new ConfigDescription("VR only. Headset height above the eye line, in meters. The flat-screen offset is under Camera.",
                    new AcceptableValueRange<float>(-0.1f, 0.1f), new ConfigurationManagerAttributes { Order = 3 }));
            VrNearClip = Config.Bind("VR", "Near clip plane", 0f,
                new ConfigDescription("VR only. Lower values let very close geometry render without being cut off. 0 = leave the VR mod's own setting alone.",
                    new AcceptableValueRange<float>(0f, 0.1f), new ConfigurationManagerAttributes { Order = 2 }));
            HideHeadVrMain = Config.Bind("VR", "Hide character head (main game)", true,
                new ConfigDescription("Main game VR only. On by default because the main game VR mod doesn't hide the head itself.",
                    null, new ConfigurationManagerAttributes { Order = 1 }));
            HideHeadVrStudio = Config.Bind("VR", "Hide character head (Studio)", true,
                new ConfigDescription("Studio VR only. On by default. Turn it off if the Studio VR mod already hides the head and the two clash.",
                    null, new ConfigurationManagerAttributes { Order = 0 }));
            VrFollowCycleKey = Config.Bind("Keyboard shortcuts", "VR rotation follow", new KeyboardShortcut(KeyCode.Insert),
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
                    {
                        vrModPovActive = pov.GetProperty("Active", BindingFlags.Public | BindingFlags.Static);
                        // [2026-11-12 perf] IsVrModPovActive is asked every frame. PropertyInfo.GetValue boxes the
                        // returned bool into a new object on every call (24 bytes/frame, ~2 KB/s at 90 fps), so compile
                        // it into a delegate once instead: the per-frame cost becomes a plain call with no allocation.
                        // If the delegate cannot be built we fall back to reflection; behaviour is identical.
                        vrModPovActiveGetter = MakeBoolGetter(vrModPovActive);
                    }
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

            bool on = isStudio ? HideHeadVrStudio.Value : HideHeadVrMain.Value;
            if (!on) return false;

            // Dragging the world away from the character keeps POV (and head
            // tracking) but brings the head back once the headset is clearly
            // outside it. Same centre point as the POV entry test, so the two
            // thresholds line up.
            //
            // Two thresholds, not one: coming back has to hide at the same radius
            // POV entry uses (VrEnterRadius), otherwise dragging back in hides the
            // head while you are still 20 cm away and it looks nothing like the
            // original entry. Between the two values the current state is kept,
            // which is also what stops the head flickering as the rig follows the
            // character's breathing/head animation.
            //
            // The facing test is repeated here on purpose. Entry needs distance AND facing,
            // but this re-hide path used to look at distance only, so after a drag-out the
            // head came back the moment the sphere was touched again - including when the
            // character leaned in for a kiss, which is exactly what the facing test exists
            // to prevent. Staying inside while facing her keeps the head visible instead.
            //
            // [2026-11-11] Two fixes:
            // 1) The vrFollowInit guard. It means "the rig has been placed at her eyes at least once"; it is
            //    false only for the frame or two right after entering or aligning, when the player is still
            //    outside her head, so the distance test always latches vrHeadShown to 1 - a false positive.
            //    Skipping it hides the head on the first frame, matching flat mode, which returns HideHead.Value and does none of these checks.
            // 2) The facing test compares horizontal direction only. It used a full 3D dot product, and in VR
            //    the view pitch is inherited from dragging the world. Measured in CharaStudio: yaw was already
            //    aligned (1.3 degrees off) but at 53.2 degrees of pitch dot was 0.596, 0.004 short of 0.6, so
            //    vrHeadShown never unlocked and the head stayed visible; tilting down to 43.6 degrees gave
            //    0.723 and unlocked it. With the horizontal projection pitch no longer matters, while a face-to-face kiss still points the opposite way (dot ~ -1), so the original intent is kept.
            if (vrFollowInit && chara && chara.objHead && chara.objHeadBone && vrHead)
            {
                Transform headT = chara.objHeadBone.transform;
                Vector3 center = GetEyeMidpoint(chara, headT) - headT.forward * 0.06f;
                float sqr = (center - vrHead.position).sqrMagnitude;
                if (sqr > VrHeadShowDistance * VrHeadShowDistance)
                    vrHeadShown = true;
                else if (sqr < VrEnterRadius * VrEnterRadius
                         && FacesSameWay(headT.forward, vrHead.forward))
                    vrHeadShown = false;
                if (vrHeadShown)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Facing test: compares horizontal (yaw) direction only, ignoring pitch and roll.
        /// See the comment in ShouldHideHead - a full 3D dot product pushes the threshold to 53 degrees, so the head never hides at large pitch.
        /// The projection degenerates when the view is nearly straight up/down; no facing limit is applied then, or it would get stuck at random.
        /// </summary>
        private static bool FacesSameWay(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            if (a.sqrMagnitude < 0.0001f || b.sqrMagnitude < 0.0001f)
                return true;
            return Vector3.Dot(a.normalized, b.normalized) >= VrEnterFacing;
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
                // Use the cached delegate to avoid per-frame reflection and boxing; fall back only if it failed
                Func<bool> getter = vrModPovActiveGetter;
                if (getter != null)
                    return getter();
                return vrModPovActive != null && (bool)vrModPovActive.GetValue(null, null);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Compile a static bool property getter into a delegate. Returns null on failure (caller falls back to reflection).
        /// </summary>
        private static Func<bool> MakeBoolGetter(PropertyInfo prop)
        {
            if (prop == null)
                return null;
            try
            {
                MethodInfo getter = prop.GetGetMethod();
                if (getter == null || getter.ReturnType != typeof(bool) || getter.GetParameters().Length != 0)
                    return null;
                return (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), getter);
            }
            catch (Exception)
            {
                return null;
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
                // The middle of the head, a bit behind the eyes, so you have to be inside the
                // head rather than merely near the face. Distance alone was not enough: when the
                // character leans in (a kiss) her head travels to the headset, so the sphere test
                // fires without the player moving at all.
                Transform headT = c.objHeadBone.transform;
                Vector3 center = GetEyeMidpoint(c, headT) - headT.forward * 0.06f;
                if ((center - headset).sqrMagnitude >= VrEnterRadius * VrEnterRadius)
                    continue;

                // So direction is required too: come in from behind, facing the same way she does.
                // A kiss is face to face (dot ~ -1) and never counts, and neither does standing
                // over a lying character to look at her face or the back of her head - those are
                // "look at her", not "become her". The two vectors are the same pair the entry
                // alignment uses (objHeadBone.forward vs the headset's forward), so anything that
                // passes here also needs almost no turning to line the view up afterwards.
                if (Vector3.Dot(headT.forward, vrHead.forward) < VrEnterFacing)
                    continue;

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

            ChaControl inside = FindHeadContaining();

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

        /// <summary>
        /// Store the current headset pose relative to her head as this character's memory.
        /// Called only when leaving POV or switching character, so it covers only drags made during POV;
        /// dragging afterwards in normal view never touches it.
        /// </summary>
        private void SaveVrUserOffset()
        {
            if (!chara || !chara.objHeadBone || !vrHead)
                return;
            try
            {
                Transform headBone = chara.objHeadBone.transform;

                // Only save while not yet in observation mode: use VrHeadShowDistance (30 cm) rather than
                // VrEnterRadius (9 cm). Reason: center is 6 cm back from the eyes, so standing at the eyes is
                // already 6 cm away; with 9 cm the usable budget is only 3 cm and the memory is effectively dead.
                // 30 cm lines up with "the head starts showing": beyond it you are outside observing and should not be recorded.
                Vector3 center = GetEyeMidpoint(chara, headBone) - headBone.forward * 0.06f;
                if ((center - vrHead.position).sqrMagnitude >= VrHeadShowDistance * VrHeadShowDistance)
                    return;

                PruneVrUserOffsets();

                // Use the head rotation captured at alignment (vrAlignHeadRot) as the reference frame, never the
                // current headBone.rotation - the animation never stops, and solving against the current value
                // folds that animation into the player's adjustment, accumulating into a rolling view on repeated entry (worst at one press every 0.5 s).
                Quaternion refRot = vrAlignHeadRotValid ? vrAlignHeadRot : headBone.rotation;
                Quaternion inv = Quaternion.Inverse(refRot);
                VrUserOffset o;
                if (!vrUserOffsets.TryGetValue(chara, out o))
                {
                    o = new VrUserOffset();
                    vrUserOffsets[chara] = o;
                }
                o.rot = inv * vrHead.rotation;

                // [2026-11-12 drift fix] The reference point must be the one used when restoring.
                // PlaceVrRig restores with `target = smoothPos + headRot * pos`, whose origin is smoothPos (the
                // eye midpoint); this used to subtract headBone.position (the head bone origin), about
                // (0, +7 cm up, +8 cm forward) apart. Every POV entry therefore added that gap again: the camera
                // crept upward until it crossed VrHeadShowDistance (30 cm), where the guard above stopped saving
                // and the head came back because the distance was over 30 cm.
                // Subtracting smoothPos instead: with no dragging pos is ~0, so any number of entries stays put.
                o.pos = inv * (vrHead.position - smoothPos);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>Clear the current character's memory (used by the roll-reset key) and go back to exactly at the eyes.</summary>
        private void ClearVrUserOffset()
        {
            if (chara)
                vrUserOffsets.Remove(chara);
            vrHasPosOffset = false;
        }

        /// <summary>Cleared on scene unload (the character references die with it).</summary>
        private void ClearAllVrUserOffsets()
        {
            vrUserOffsets.Clear();
            vrHasPosOffset = false;
        }

        // Destroyed characters leave dead keys behind, so sweep them out while saving
        private void PruneVrUserOffsets()
        {
            if (vrUserOffsets.Count == 0)
                return;
            deadOffsetKeys.Clear();
            foreach (ChaControl c in vrUserOffsets.Keys)
            {
                if (!c)
                    deadOffsetKeys.Add(c);
            }
            for (int i = 0; i < deadOffsetKeys.Count; i++)
                vrUserOffsets.Remove(deadOffsetKeys[i]);
            deadOffsetKeys.Clear();
        }

        private void BeginVrPov()
        {
            // remember where the rig was so leaving POV with the toggle key puts you back there
            vrOrigPos = vrOrigin.position;
            vrOrigRot = vrOrigin.rotation;
            // This is the origin pose; dragging inside 30 cm never rewrites it
            vrEntryPos = vrOrigPos;
            vrEntryRot = vrOrigRot;
            vrAlignHeadRotValid = false;
            vrFollowInit = false;
            vrLeftHead = false;
            vrHeadShown = false;
            vrLastFrame = -1;
            vrEnterCandidate = null;
            vrSwitchCandidate = null;
            // start the rig-move check fresh, the last session's numbers mean nothing now
            vrAppliedValid = false;
            vrDriftMove = 0f;
            vrDriftTurn = 0f;
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
                // Look exactly where the character's head looks.
                //
                // [2026-11-11] This used to do yaw only (`RotateAround(up, delta)`), dropping pitch, so the tilt
                // produced by dragging the world was inherited into POV: the view no longer matched her face and
                // had to be straightened by hand, and ShouldHideHead's facing test failed because of it, leaving
                // the head visible (measured: stuck at dot 0.596, unlocked only at 0.723 after tilting to 43.6 degrees).
                //
                // Now it is full 3D: the target view is defined by her head's forward, so however you dragged the
                // world beforehand it makes no difference - delta is computed from the current view and closes
                // the gap in one go. Roll follows her too; entry alignment is a one-off snapshot, and continuous roll tracking is follow mode's job.
                Transform headBone = chara && chara.objHeadBone ? chara.objHeadBone.transform : null;

                // Target pose: prefer the pose the player adjusted, from this character's memory (read on hotkey
                // entry only), otherwise align strictly to her face. The memory also carries a position offset to restore the adjusted distance.
                Quaternion targetView = Quaternion.identity;
                bool haveTarget = false;
                vrHasPosOffset = false;

                VrUserOffset mem = null;
                if (vrAlignFromMemory && chara)
                    vrUserOffsets.TryGetValue(chara, out mem);

                if (mem != null && headBone)
                {
                    targetView = headBone.rotation * mem.rot;
                    vrPosOffsetLocal = mem.pos;
                    vrHasPosOffset = true;
                    haveTarget = true;
                }
                else if (headBone)
                {
                    Vector3 charaFwd = headBone.forward;
                    if (charaFwd.sqrMagnitude > 0.0001f)
                    {
                        charaFwd.Normalize();
                        // LookRotation's up reference degenerates when she faces almost straight up/down, fall back to world up
                        Vector3 upRef = Mathf.Abs(Vector3.Dot(charaFwd, Vector3.up)) > 0.99f
                            ? Vector3.up
                            : headBone.up;
                        targetView = Quaternion.LookRotation(charaFwd, upRef);
                        haveTarget = true;
                    }
                }

                if (haveTarget)
                {
                    // The reference frame is fixed here: memory is solved against it so her head animation is not mistaken for an adjustment
                    vrAlignHeadRot = headBone.rotation;
                    vrAlignHeadRotValid = true;

                    Quaternion delta = targetView * Quaternion.Inverse(vrHead.rotation);
                    if (Quaternion.Angle(delta, Quaternion.identity) > 0.01f)
                    {
                        // Rotate about the headset position so the headset itself does not move
                        Vector3 headPos = vrHead.position;
                        vrOrigin.rotation = delta * vrOrigin.rotation;
                        vrOrigin.position = headPos + delta * (vrOrigin.position - headPos);
                    }
                }
                vrAlignFromMemory = false;
                vrAlignPending = false;
                vrFollowInit = false;
                // turning the head from here on is measured from the newly faced direction
                if (vrFollowMode != 0)
                    RecordVrFollowBase();
            }

            UpdateVrFollowRotation();

            if (!vrFollowInit)
            {
                // start exactly at the eyes (plus the player's adjusted offset when restoring from memory)
                Vector3 target = smoothPos;
                if (vrHasPosOffset && chara && chara.objHeadBone)
                    target += chara.objHeadBone.transform.rotation * vrPosOffsetLocal;
                vrOrigin.position += target - vrHead.position;
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
                // Can be disabled with F1's "Leave POV by moving your real headset out", leaving only the POV key to exit.
                if (VrExitByHeadMove.Value)
                {
                    Vector3 leaned = vrOrigin.TransformVector(vrOrigin.InverseTransformPoint(vrHead.position) - vrHeadStartInRig);
                    if (leaned.sqrMagnitude > VrExitDistance * VrExitDistance)
                        vrLeftHead = true;
                }
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
        //
        // [2026-11-12] Adjusting while still inside her head does not count as leaving: while the headset is
        // within VrHeadShowDistance (30 cm) dragging only changes the view and must not move the exit spot -
        // leaving should return to where you entered. Only once the headset is dragged past 30 cm (observation
        // mode, her head shown again) does the current position become the exit spot; drag out and back in and it reverts to the entry pose.
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

            vrDriftMove = 0f;
            vrDriftTurn = 0f;

            if (!HeadsetLeftHead())
            {
                // still inside her head -> keep the pre-entry exit position
                vrOrigPos = vrEntryPos;
                vrOrigRot = vrEntryRot;
                return;
            }

            vrOrigPos = vrOrigin.position;
            vrOrigRot = vrOrigin.rotation;
        }

        /// <summary>
        /// Whether the headset has left her head (beyond VrHeadShowDistance). With no character available it reports "still inside",
        /// so the exit position stays where it is.
        /// </summary>
        private bool HeadsetLeftHead()
        {
            if (!chara || !chara.objHeadBone || !vrHead)
                return false;
            Transform headT = chara.objHeadBone.transform;
            Vector3 center = GetEyeMidpoint(chara, headT) - headT.forward * 0.06f;
            return (center - vrHead.position).sqrMagnitude
                   >= VrHeadShowDistance * VrHeadShowDistance;
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
