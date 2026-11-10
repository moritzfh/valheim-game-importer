# Game Importer: Ocarina of Time in Valheim

A BepInEx mod plus a set of Python tools that turn Kokiri Forest and the Deku Tree from *The Legend of Zelda: Ocarina of Time*
into a playable Valheim world. Layout, collision, enemies, puzzles, texts and cutscene cameras come from the original game, the
look is Valheim's own terrain, textures, plants and lighting. It's a remaster, not a 1:1 copy.

Video: https://youtu.be/LTBrWFFM9xE

**This repo contains no game files.** Everything Zelda gets built on your PC from your own copy of the game.
Built with Claude (Anthropic): all code in this repo was written by Claude.

## What's playable

- Title screen and the intro (Link's house, the nightmare, the Deku Tree calling Navi, Navi waking Link up)
- Kokiri Forest: Kokiri, Saria, Mido with their original texts, the shop, chests, grass and rupees, the Kokiri Sword in the maze,
  the rolling boulders
- The Deku Tree: meeting him, then the dungeon up to the web in B1. Burn it with a Deku Stick and drop down to B2
- Enemies: Deku Babas, Skulltulas (also on walls and gold ones), Deku Scrubs, Gohma larvae
- Puzzles and objects: torches, push blocks, switches (also the eye switch), the water level, ladders, doors with bars, Navi hints
- Hearts, damage, Z-targeting, shield, roll, climbing, ledge grab, swimming and diving, Deku Stick, Fairy Slingshot

Not there yet: Queen Gohma and everything after the Deku Tree. Singleplayer only.

## What you need

- Valheim (Steam) with **BepInEx** (BepInExPack_Valheim, e.g. through r2modman or Thunderstore Mod Manager)
- **Your own Ocarina of Time ROM** from the GameCube *Collector's Edition* (Europe/PAL), as a `.z64` file.
  Other versions are not supported yet. The setup script checks the MD5: `2c27b4e000e85fd78dbca551f1b1c965`
- **Linux or Windows with WSL** for the build steps. On Windows: `wsl --install -d Ubuntu` in an admin PowerShell, reboot,
  then in the Ubuntu window:
  ```
  sudo apt update
  sudo apt install git curl python3 python3-venv python3-pip build-essential libxml2-dev ffmpeg python3-numpy python3-scipy python3-pil
  git clone https://github.com/moritzfh/valheim-game-importer.git
  cd valheim-game-importer
  ```

## Install

1. **Mod:** put `GameImporter.dll` (from the Releases page) into `BepInEx/plugins`.
2. **Valheim textures:** start Valheim and enter any world once (a throwaway world is fine). After a few seconds the mod
   exports the Valheim textures the pack builder needs to `BepInEx/config/GameImporter/vtex` and `vmat`
   ("Game Importer: Valheim textures exported..." shows up in the middle of the screen). Quit Valheim.
3. **Extract your ROM** (in Linux/WSL, inside the cloned repo folder). Copy your ROM somewhere WSL can see it, e.g. `/mnt/c/Users/you/Downloads/oot.z64`:
   ```
   bash tools/oot/oot_setup_decomp.sh /path/to/your-rom.z64 ~/oot-decomp
   ```
   This clones the [zeldaret/oot](https://github.com/zeldaret/oot) decompilation at a fixed commit, checks your ROM and extracts
   only what the pack needs (Kokiri Forest, Link's house, the shop, the Deku Tree, texts, audio). Takes a few minutes.
4. **Build the pack** (~2-3 minutes). From WSL your Windows drive is under `/mnt/c`:
   ```
   GI="/mnt/c/Program Files (x86)/Steam/steamapps/common/Valheim/BepInEx/config/GameImporter"
   python3 tools/oot/oot_build.py ~/oot-decomp ~/oot-pack --vtex "$GI/vtex" --vmat "$GI/vmat"
   ```
   (With r2modman the `BepInEx/config` folder is inside your mod profile instead.)
5. **Copy the pack:** copy everything inside `~/oot-pack` to `BepInEx/config/GameImporter/packs/oot/`.
6. **Play:** create a new world whose name starts with **OoT** (e.g. `OoT`), use a fresh character, start it.
   The title screen comes up, press a key and the intro starts.

## Controls

| Key | Action (N64 button) |
|---|---|
| WASD, mouse | move, camera |
| Left mouse | sword (B) |
| Right mouse (hold) | Z-targeting (Z) |
| Shift | A: talk, open chests and doors, climb up ledges, roll while moving |
| Space | jump |
| R or middle mouse | shield (R) |
| 1 | Deku Stick (C-left) |
| 3 | Fairy Slingshot (C-right), aim with the mouse, left mouse shoots |
| Q | Navi (C-up) |
| Ctrl / Alt | walk slowly |
| F8 | play a cutscene |
| F7 | mood panel (time of day, Valheim weather, fog) |
| F9 / F10 / F11 | dev panel (teleports, items, reset puzzles; labels are still partly German) / free camera / HUD on/off |

## How it works

- `tools/oot/oot_setup_decomp.sh`: zeldaret/oot decomp + your ROM -> extracted scenes, objects, texts and audio
- `oot_gfx.py`: a small F3DEX2 display list interpreter that turns the N64 draw commands into meshes with vertex colors and
  textures (written as .glb)
- `oot_vpack.py`, `oot_kf*.py`, `oot_ydan*.py`: the collision floor becomes Valheim terrain (`oot.vmap`), the rest becomes models
  with collision; textures get swapped for Valheim ones; Valheim plants, mushrooms and roots get scattered around
- `oot_items.py`, `oot_npc.py`, `oot_ydan.py`, ...: actor lists from the scenes -> positions of NPCs, enemies, chests, switches
- `oot_cutscene.py`, `oot_intro.py`, `oot_start.py`: cutscene cameras replayed frame by frame like the game does it
- `oot_sfx.py`: music rendered from the ROM's sequences and soundfonts, sound effects from the ROM's audio banks
- `oot_build.py`: runs everything and writes the pack (json + glb + ogg + png + heightmap)
- `src/`: the BepInEx mod. Loads the pack and adds the Zelda parts at runtime (`Oot*.cs`): Link's moves, combat, enemies and
  objects ported from the decomp's C code, HUD, title screen, intro, Navi, music and sounds

## Known issues

- Only the GameCube Collector's Edition (EU) ROM works for now
- The build steps need Linux or WSL
- Some NPCs only have simple idle animations, crawlspaces are a transition without crawl animation,
  the camera in tight corridors is still clunky
- Music is synthesized from the ROM, so it sounds a bit different from the real N64 output

## Building the DLL yourself

- Windows (Visual Studio 2022 or Build Tools): `python tools\build_win.py "C:\...\Valheim"`
- Linux (.NET SDK): `./build.sh /path/to/Valheim`

## Credits and legal

- [zeldaret/oot](https://github.com/zeldaret/oot): the decompilation that makes reading the game data possible
- Cinzel font (SIL Open Font License) for the title logo
- Not affiliated with Nintendo or Iron Gate. The Legend of Zelda and Ocarina of Time are trademarks of Nintendo.
  This repo ships no Nintendo or Iron Gate assets. Use it only with a game you own.

Code: MIT (see LICENSE).
