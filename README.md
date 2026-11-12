# KK_PerspectiveX

A first-person POV plugin for Koikatsu that doesn't clip through the body and doesn't make you dizzy.

Works in the main game (free roam), H scenes, and CharaStudio.

![In POV](img/pov.png)

Supports both Koikatsu and Koikatsu Sunshine with the same DLL. I've played it plenty on both and they behave the same, though Sunshine is the newer and less common target for POV mods, so the occasional Sunshine-specific bug can slip through. If the two games ever diverge enough I'll split the plugin, but for now one DLL covers both just fine.

A Traditional Chinese (ZH-TW) translation fork is available, maintained by Tokozakura: [releases](https://github.com/Tokozakura/KK_PerspectiveX_ZH-TW/releases).

## Why another POV mod?

Because the existing ones kept annoying me. Some place the camera at the wrong spot, so you get a chest-level view with the head clipping into the screen. RealPOV places it correctly but copies the head bone's full animation rotation, roll included, so the view tilts sideways and jerks around with every animation. PerspectiveX handles position and rotation separately:

| | PerspectiveX | RealPOV | KK_StudioPOV (Studio-only) |
|---|---|---|---|
| **Viewpoint** | True eye level, at the midpoint of the actual eye bones | Same eye-bone midpoint | Same eye-bone midpoint |
| **Rotation** | Fully mouse-controlled and independent of the character's bones, horizon always level. Optional "animation sway" lets a configurable amount of head motion through, with roll always removed | Overrides the neck bone, but the camera still inherits the head's full world rotation, so animated spine/chest lean still rolls the view | Copies the eye bone's raw rotation every frame, so any animation or pose edit rotates the camera directly, with no limit on pitch |
| **Posing while in POV** | Moving bones (e.g. with KKPE) doesn't spin your view - rotation is entirely yours | Bone edits up the chain can still tilt the camera, since rotation isn't fully decoupled | Bone edits move the camera immediately, since it reads the bone's rotation live |
| **Head hiding** | Hides the head's renderers only: it vanishes from view but keeps animating normally, and nothing is written into your character or scene files | Flips the character's stored "head always visible" flag (optional, off by default) | Deactivates the whole head object; since Unity stops animating deactivated bones, the eye position can freeze while the body keeps moving, letting the camera drift and clip into the body |
| **Mouse look limits** | Clean yaw/pitch, no wraparound | Unclamped rotation accumulation | Unclamped rotation accumulation - enough drag can flip the view upside down |
| **Comfort options** | Position smoothing, animation-sway blend, forward/up offsets, live FOV, one-click presets, saveable view slots, camera lock - all adjustable mid-POV | FOV, sensitivity, fixed offset | FOV, sensitivity |

(Based on reading the public source of [RealPOV](https://github.com/Keelhauled/KeelPlugins) and [KK_StudioPOV](https://github.com/Mantas-2155X/StudioPOV) - describing what the code does, not a knock on either project.)

## Installation

1. You need BepInEx 5 (already included in HF Patch).
2. Download `KK_PerspectiveX.dll` from [Releases](../../releases) and drop it into `BepInEx/plugins/`.
3. If you have RealPOV installed (I think HF Patch includes it), disable it by renaming `RealPOV.Koikatu.dll` to `RealPOV.Koikatu.dll.disabled`, since both plugins use Backspace as the toggle key.

Tested and played on KK and KKS with BepInEx 5+. If it doesn't work on your setup, please [open an issue](../../issues) and I'll take a look.

## Controls

Everything is rebindable via ConfigurationManager (F1).

- **Backspace**: toggle POV. In Studio, select a character in the workspace first.
- **Left mouse (hold + drag)**: look around. The cursor stays visible and usable otherwise.
- **Ctrl+L**: hands-free FPS mouse look. The cursor is captured until you press it again.
- **Ctrl+Shift+Left / Ctrl+Shift+Right**: switch the POV to the previous/next character.
- **Scroll wheel**: adjust FOV while in POV (can be turned off in the settings if you keep hitting it by accident).
- **Comma / Period**: tilt the camera left/right. **Slash** resets the tilt to level.
- **Semicolon**: lock the camera in place. The view stops following the head, which is handy during caress animations that toss it around; looking around still works. Press again to unlock and the camera glides back.
- **Ctrl+Shift+1/2/3**: save the current view (FOV, look direction, tilt, camera offsets) into a slot. **Ctrl+1/2/3**: recall it. Slots are remembered between game sessions.
- **Ctrl+P**: open the presets panel. In Studio you can also click the PerspectiveX eye button in the bottom-left toolbar.

- ## VR Controls

- Hold the side button and drag the world behind the character's head to enter first-person (it won't accidentally trigger first-person when kissing the character). You can also press Backspace to enter first-person directly (press left on the controller trackpad or push the joystick left).
- In first-person, press right on the controller trackpad or push the joystick right to switch to the next character's first-person view (you can change the shortcut in F1; the controller shortcut must be the same as the "Switch POV to next character" shortcut to take effect; default is Numpad 3, and neither Studio nor the main game has any function bound to these two keys). Controller mapping only works in Studio and the main game's Free H. If you use KK_MainGameVR for main-game VR, the controller trackpad mapping under "H Scene Main" should be set to None.
- In first-person, press down on the controller trackpad or push the joystick down to cycle the first-person head animation tracking mode. If you get dizzy, tracking position only is enough. The modes are:
- Track position
- Track position + horizontal rotation
- Track position + vertical rotation
- Track position + horizontal rotation + vertical rotation
- Track position + horizontal rotation + vertical rotation + roll rotation (tilting your head left/right)
- Head tracking will not conflict with the player's VR camera; the two can exist independently. While first-person is tracking the head animation, you can still drag the world to adjust it to a suitable position. So even if you stay lying down without moving, you can fully experience first-person from any angle.
- For each character, the world drag position and angle after entering first-person via Backspace are remembered independently. The next time you enter that character's first-person view, it will return to the remembered position, and it will not conflict with the world drag position and angle outside first-person. The memory is cleared when the game restarts.
- When you drag the world a certain distance away from the head, the head will automatically become visible again; dragging it back will automatically hide it again (you need to enter from behind the head). After dragging the world away from the head, it is not considered exiting first-person; it will continue tracking the character's head animation from a third-person view. At this time you can also continue pressing down on the trackpad or joystick to trigger the head animation tracking mode switch. To exit POV, you need to press left on the controller trackpad or push the joystick left once; only then is it considered a complete exit from POV. This is intentional design to improve immersion.
- If you are not playing lying down and want moving the headset away from the character's head by a certain distance in the real world to be directly treated as exiting POV, you can change an option in F1; just enable that option.

### Presets panel

The in-game panel collects everything preset-related in one place, no config diving needed:

- **View presets**: one-click setups (Cozy 60 / Natural 90 / Action 110) that set FOV, position smoothing and camera offset together.
- **Custom presets**: dial in your favorite FOV/smoothing/offset combo, type a name, and save it as your own one-click preset (up to 5).
- **Saved views**: the same three save/recall slots as the hotkeys, as buttons, with each slot's FOV shown.

Comfort settings (position smoothing, animation sway, FOV, offsets) are in the plugin settings, adjustable live while in POV. The view presets are available there too. An optional "Align camera with body" setting tilts the view to match the character's body orientation, for example when they're lying on their side, instead of keeping the horizon level.

## Building from source

The plugin targets .NET Framework 3.5 and builds with the regular .NET SDK on any OS.
Copy these reference DLLs from your game install into `lib/` first (they are copyrighted, so not included here):

- `BepInEx/core/`: `BepInEx.dll`, `0Harmony.dll`
- `Koikatu_Data/Managed/`: `Assembly-CSharp.dll`, `Assembly-CSharp-firstpass.dll`, `UnityEngine.dll`, `UnityEngine.UI.dll`

Then:

```sh
cd src/KK_PerspectiveX
dotnet build -c Release
```

Output: `src/KK_PerspectiveX/bin/Release/KK_PerspectiveX.dll`

## The XMods

- [KK_PerspectiveX](https://github.com/bani4kaskashka/KK_PerspectiveX): first-person POV that doesn't clip through the body and doesn't make you dizzy (this one)
- [StudioHeightX](https://github.com/bani4kaskashka/KK-KKS-StudioHeightX): live height readout for the selected Studio character
- [PerceptiveHeightX](https://github.com/bani4kaskashka/PerceptiveHeightX): POV size-perception add-on for PerspectiveX

## Credits

Better VR support provided by [HanYuanyi](https://github.com/HanYuanyi).

## My links

- [Patreon](https://www.patreon.com/c/zamalkogts)
- [DeviantArt](https://www.deviantart.com/zamalkogts)

## License

[MIT](LICENSE)
