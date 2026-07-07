# KK_PerspectiveX

A first-person POV plugin for Koikatsu that doesn't clip through the body and doesn't make you dizzy.

Works in the main game (free roam), H scenes, and CharaStudio.

Supports both Koikatsu and Koikatsu Sunshine - the same DLL works for both games. I've tested it thoroughly on Sunshine and it behaves identically to the original Koikatsu in its current state, though as a newer/less common target for POV mods, expect the occasional Sunshine-specific bug. If the two games diverge more in the future I may need to split the plugin, but for now one DLL covers both just fine.

## Demo

<!-- Screenshot/GIF: commit the file (e.g. under images/) and reference it below. -->
<!-- ![PerspectiveX in action](images/demo.gif) -->

<!-- Video: drag & drop the .mp4 into a GitHub issue/PR/discussion comment box to get an
     upload URL (https://github.com/user-attachments/assets/...), then paste that URL
     on its own line below - GitHub renders it as a native video player. -->

## Why another POV mod?

Existing POV plugins have two problems. Some place the camera at the wrong spot, giving you a chest-level view with the head clipping into the screen. RealPOV places it correctly but copies the head bone's full animation rotation, including roll, so the view tilts sideways and jerks around with every animation. PerspectiveX handles position and rotation separately:

| | PerspectiveX | RealPOV | KK_StudioPOV (Studio-only) |
|---|---|---|---|
| **Viewpoint** | True eye level, at the midpoint of the actual eye bones | Same eye-bone midpoint | Same eye-bone midpoint |
| **Rotation** | Fully mouse-controlled and independent of the character's bones, horizon always level. Optional "animation sway" lets a configurable amount of head motion through, with roll always removed | Overrides the neck bone, but the camera still inherits the head's full world rotation, so animated spine/chest lean still rolls the view | Copies the eye bone's raw rotation every frame, so any animation or pose edit rotates the camera directly, with no limit on pitch |
| **Posing while in POV** | Moving bones (e.g. with KKPE) doesn't spin your view - rotation is entirely yours | Bone edits up the chain can still tilt the camera, since rotation isn't fully decoupled | Bone edits move the camera immediately, since it reads the bone's rotation live |
| **Head hiding** | Uses the game's own auto-hide flag, the same one it uses when the camera gets close on its own - no side effects | Same flag (optional) | Deactivates the whole head object; since Unity stops animating deactivated bones, the eye position can freeze while the body keeps moving, letting the camera drift and clip into the body |
| **Mouse look limits** | Clean yaw/pitch, no wraparound | Unclamped rotation accumulation | Unclamped rotation accumulation - enough drag can flip the view upside down |
| **Comfort options** | Position smoothing, animation-sway blend, forward/up offsets, live FOV, all adjustable mid-POV | FOV, sensitivity, fixed offset | FOV, sensitivity |

(Based on reading the public source of [RealPOV](https://github.com/Keelhauled/KeelPlugins) and [KK_StudioPOV](https://github.com/Mantas-2155X/StudioPOV) - describing what the code does, not a knock on either project.)

## Installation

1. You need BepInEx 5 (already included in HF Patch).
2. Download `KK_PerspectiveX.dll` from [Releases](../../releases) and drop it into `BepInEx/plugins/`.
3. If you have RealPOV installed (I think HF Patch includes it), disable it by renaming `RealPOV.Koikatu.dll` to `RealPOV.Koikatu.dll.disabled`, since both plugins use Backspace as the toggle key.

Tested and played on Koikatsu + After School with BepInEx 5. If it doesn't work on your setup, please [open an issue](../../issues) and I'll take a look.

## Controls

All rebindable via ConfigurationManager (F1).

- **Backspace**: toggle POV. In Studio, select a character in the workspace first.
- **Left mouse (hold + drag)**: look around. The cursor stays visible and usable otherwise.
- **Ctrl+L**: hands-free FPS mouse look. The cursor is captured until you press it again.
- **Ctrl+Shift+Left / Ctrl+Shift+Right**: switch the POV to the previous/next character.
- **Scroll wheel**: adjust FOV while in POV.
- **Comma / Period**: tilt the camera left/right. **Slash** resets the tilt to level.

Comfort settings (position smoothing, animation sway, FOV, offsets) are in the plugin settings, adjustable live while in POV. An optional "Align camera with body" setting tilts the view to match the character's body orientation, for example when they're lying on their side, instead of keeping the horizon level.

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

## My links

- [Patreon](https://www.patreon.com/c/zamalkogts)
- [DeviantArt](https://www.deviantart.com/zamalkogts)

## License

[MIT](LICENSE)
