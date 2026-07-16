# KK_PerspectiveX

A first-person POV plugin for Koikatsu that doesn't clip through the body and doesn't make you dizzy.

Works in the main game (free roam), H scenes, and CharaStudio.

![In POV](img/pov.jpg)

Supports both Koikatsu and Koikatsu Sunshine with the same DLL. I've played it plenty on both and they behave the same, though Sunshine is the newer and less common target for POV mods, so the occasional Sunshine-specific bug can slip through. If the two games ever diverge enough I'll split the plugin, but for now one DLL covers both just fine.

A Traditional Chinese (ZH-TW) translation fork is available, maintained by Tokozakura: [releases](https://github.com/Tokozakura/KK_PerspectiveX_ZH-TW/releases).

## Why another POV mod?

Because the existing ones kept annoying me. Some place the camera at the wrong spot, so you get a chest-level view with the head clipping into the screen. RealPOV places it correctly but copies the head bone's full animation rotation, roll included, so the view tilts sideways and jerks around with every animation. PerspectiveX handles position and rotation separately:

| | PerspectiveX | RealPOV | KK_StudioPOV (Studio-only) |
|---|---|---|---|
| **Viewpoint** | True eye level, at the midpoint of the actual eye bones | Same eye-bone midpoint | Same eye-bone midpoint |
| **Rotation** | Fully mouse-controlled and independent of the character's bones, horizon always level. Optional "animation sway" lets a configurable amount of head motion through, with roll always removed | Overrides the neck bone, but the camera still inherits the head's full world rotation, so animated spine/chest lean still rolls the view | Copies the eye bone's raw rotation every frame, so any animation or pose edit rotates the camera directly, with no limit on pitch |
| **Posing while in POV** | Moving bones (e.g. with KKPE) doesn't spin your view - rotation is entirely yours | Bone edits up the chain can still tilt the camera, since rotation isn't fully decoupled | Bone edits move the camera immediately, since it reads the bone's rotation live |
| **Head hiding** | Uses the game's own auto-hide flag, the same one it uses when the camera gets close on its own - no side effects | Same flag (optional) | Deactivates the whole head object; since Unity stops animating deactivated bones, the eye position can freeze while the body keeps moving, letting the camera drift and clip into the body |
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

## My links

- [Patreon](https://www.patreon.com/c/zamalkogts)
- [DeviantArt](https://www.deviantart.com/zamalkogts)

## License

[MIT](LICENSE)
