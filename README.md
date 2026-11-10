# Body Accessories (0.2.4 beta)

Model anything you want around Flippy in Blender, and wear it in Flipping is Hard. Hats, wings, signs, a floating
moon, a giant sword through the phone: anything rigid, anywhere around him. A Flipping is Hard mod (BepInEx 6 IL2CPP)
by Fabuleo8796.

Press **X** in game, and Blender opens on a template with **the real Flippy** in it. Model around him and save. Your
accessory shows up on Flippy **within a second, live**, and tumbles with him.

Everything is client-side, so it's safe in multiplayer. Other players see your normal Flippy, unless they also run
**[FIH Sync](https://github.com/Fabuleo8796/FIHSync)**, which shows modded players each other's accessories
automatically (animations too; videos only from friends, unless you change it).

## How to use it

1. Get into a level and press **X**. The panel slides in from the right.
2. Press **+ New accessory** (or **Edit** on one you have). Blender opens with Flippy in the middle.
   - The first time, the mod copies the plain Flippy (his cover and screen) out of your game, so you model around the
     real shape. Wear the default cover first if you want a fully plain Flippy.
3. Model **inside the "Accessory" collection**: inside Flippy, around him, far away, crossing through him. It's all
   up to you. Flippy himself is locked and never exported.
4. Press **Ctrl+S** (or **Export to FIH** in Blender's sidebar: N, then the FIH tab). It's on Flippy in game.
5. Keep tweaking and saving. The game updates every time.

**In Blender (N → FIH tab):**

- **Export to FIH**: sends the Accessory collection to the game. Saving with Ctrl+S does the same (switch it off with
  "Export on save").
- **Add texture to selected**: pick a PNG or JPG. The model gets unwrapped (Smart UV Project) and textured
  automatically. Models with a texture but no UVs also get unwrapped on export.
- **Reload game Flippy**: loads Flippy again after you press Copy again in game.
- **Text, curves and surfaces** export too. Give text some Extrude so it isn't paper-thin.
- **Animation:** keyframe your objects (move, rotate, scale, also with constraints or parents). Whatever moves between
  the scene's **Start** and **End** frames loops in game at your scene's frame rate. The model is exported as it is on
  the Start frame.
- **Video textures:** use **Add texture to selected** with a video (MP4, MOV, WEBM…), or any Image Texture node
  holding a movie. The game plays the same part of the video your timeline shows, looping with the animation (no
  sound). The video file is copied next to the accessory.

**In game (X):**

| | |
|---|---|
| **Your accessories** | Wear each one or not (switch), **Edit** opens it in Blender, **×** deletes it (click twice). Deleted files go to `BepInEx\config\BodyAccessories\trash\`, just in case |
| **+ New accessory** / **Open folder** | Starts a new one from the template, or opens the mod's folder |
| **Look** | **Game** (Flippy's own toon shading, the default), **Lit** or **Flat colour**. **Toon outline** adds the game's outline (experimental) |
| **Flippy in Blender** | Whether Blender has the real Flippy, and **Copy again** (after you change your cover) |
| **Blender** | Which Blender the mod uses. With no Blender (or one older than 4.0), **Download** gets the official portable Blender 5.2.3 for you |
| **Shift+X** | Accessories on/off, anywhere |

The key can be changed in `BepInEx\config\com.fabuleo8796.bodyaccessories.cfg`.

## Install

You need Flipping is Hard with BepInEx 6 (IL2CPP), and Blender 4.0 or newer (tested on 4.0, 4.2, 4.5 and 5.2). If you
don't have Blender, the mod can download it for you.

Download `BodyAccessories-v0.2.4.zip` from the releases and extract it into the game folder (the one with
`Flipping is Hard.exe`). You should end up with `BepInEx\plugins\BodyAccessories\` holding `BodyAccessories.dll`,
`template.blend` and `fih_accessory.py`. All three are needed.

## Good to know

- **Blender download:** the panel's Download button gets Blender's official portable zip (about 390 MB) from Blender's
  own mirrors. It checks the file against the SHA-256 Blender publishes and unpacks it to `%LOCALAPPDATA%\FIHBlender`.
  There's no installer and no admin rights. **Remove it** deletes it again. A Blender you installed yourself is never
  touched.
- **Your files:**
  - `BepInEx\config\BodyAccessories\blender\` holds your `.blend` files (one per accessory).
  - `BepInEx\config\BodyAccessories\accessories\` holds the exported `.glb` files the game reads.
  - `.glb` files from anywhere else work too: drop them in that folder.
- **Limits:** up to 300,000 triangles per accessory and textures up to 4096×4096. Rigid pieces only: objects can be
  keyframed, but there are no bones (armatures bend nothing) and no physics.
- **See-through textures:** PNGs with no background work: the see-through parts are cut out (in every look). A picture
  on a plane shows from both sides when Backface Culling is off in Blender (the default).
- **Hidden** while the phone is destroyed, and back when it's repaired. It follows you through respawns and levels.

If something looks wrong, open an issue with your `BepInEx\LogOutput.log` (and
`BepInEx\config\BodyAccessories\flippy\report.txt` if the Flippy copy failed).

## How it works

- **Other players' accessories (FIH Sync) are handled as untrusted files.**
  - Pictures are never decoded on your PC: the sender's own game turns its textures into raw pixels (up to
    1024×1024), and yours only copies pixels.
  - Videos only play from the players FIH Sync's "Videos from" allows (friends only by default). Your own videos are
    shared up to 20 MB in total; a bigger one stays on your PC and the others see that accessory without it.
  - Only accessory file types get unpacked, inside FIH Sync's cache, up to 64 MB. Files that lie about their sizes
    or loop forever are refused.
  - Per player: up to 8 accessories, 150,000 triangles and 2 videos.

- **The real Flippy, without shipping game files.** The game's meshes and textures can't be read normally. So when you
  first press Edit, the mod reads Flippy's cover and screen back from the graphics card, read-only, and writes an OBJ
  with textures into your config folder. The Blender template loads it over a simple stand-in. It's made on each
  player's own PC from their own game: this repo contains no game files.
- **Blender side** (`blender/fih_accessory.py`): it runs when the mod opens Blender. It exports the Accessory
  collection as `.glb` (modifiers applied, text and curves turned into temporary meshes) through a temp file, so the
  game never reads half a file.
- **Game side:**
  - Every half second the mod looks for new or changed `.glb` files.
  - It reads them with its own glTF reader (`src/Glb.cs`) on a background thread, and decodes textures with Windows'
    picture library (GDI+).
  - Then it builds the meshes and hangs them on Flippy's visuals root, so they move and tumble with him exactly.
- **Axes:** Blender's +Z is up the phone and -Y is out of the screen (Blender's Front view looks at his screen). 1
  Blender unit = 1 game unit.
- **Materials:** the Game look copies Flippy's own body shader (`SG_Player_MSAO`), so accessories get the same toon
  lighting as him.

## Layout

| Path | What |
|---|---|
| `src/Plugin.cs` | Load, config |
| `src/WearDriver.cs` | Keys, the live folder watch, opening Blender, following Flippy |
| `src/Panel.cs` | The X panel |
| `src/Wear.cs` | Meshes, materials, attaching to Flippy |
| `src/Glb.cs` | The glTF (.glb) reader |
| `src/Picture.cs` | Texture decoding (GDI+) |
| `src/FlippyModel.cs` | Copies the plain Flippy from the GPU into an OBJ for Blender |
| `src/BlenderGet.cs` | The optional Blender download |
| `src/Ui.cs`, `src/GameControls.cs` | UI pieces (Geist font) and pausing the game's controls while the panel is open |
| `blender/make_template.py` | Builds `template.blend`: the stand-in Flippy and the Accessory collection |
| `src/Motion.cs` | Animations and video textures (`.anim.json`), looping in game |
| `src/SyncLink.cs` | Sharing accessories with other players through FIH Sync (optional) |
| `blender/fih_accessory.py` | The Blender side: the FIH tab, export, Add texture, animation and video recording, loading the game Flippy |
| `tests/` | Offline tests: the glTF reader and texture decoding, and the Blender download |
| `release/` | The built mod (0.2.4) |
| `lib/FIHSync.dll` | FIH Sync, built against (a soft dependency: Body Accessories runs without it) |
| `tools/make_rsp.py` | Writes the compiler's response file |

## Building (no .NET SDK)

1. **The template** (only when `blender/make_template.py` changes):

   ```
   blender --background --factory-startup --python blender\make_template.py -- blender\template.blend
   ```

2. **The plugin**, with Unity 6000.0.66f1's bundled compiler:
   - `python tools\make_rsp.py bodyaccessories bin\BodyAccessories.dll src` writes `_private\bodyaccessories.rsp`
     (references to the game's `dotnet\`, `BepInEx\core\` and `BepInEx\interop\` DLLs, and the embedded fonts).
   - Then compile:

     ```
     <Unity>\Editor\Data\NetCoreRuntime\dotnet.exe <Unity>\Editor\Data\DotNetSdkRoslyn\csc.dll -noconfig @_private\bodyaccessories.rsp
     ```

MIT license. Geist font © Vercel, SIL Open Font License (`src/Resources/OFL-Geist.txt`).
