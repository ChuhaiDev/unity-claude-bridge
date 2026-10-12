---
name: unity-editor
description: Drive the live Unity editor — read and build scenes, prefabs, components, sprites and tilemaps, run play mode, take screenshots, check compile errors and console output. Use for ANY work inside a Unity project (anything touching Assets/, scenes, prefabs or C# scripts there), instead of hand-editing .unity/.prefab YAML or building scenes from code at runtime.
---

# Unity editor (Claude Bridge)

You drive the **open Unity editor** through `u`, the client in this skill's directory:
`<this skill's base directory>/u <command> …` (on Windows: `python <dir>/u …`). Below, `u` means that path.
The human watches in the editor. **Window ▸ Claude Activity** lists every call you make, and `u note` shows them
what you're doing. If `u` reports that the editor is down, ask the human to open the project in Unity.

## Rules
- **Author the game as saved editor content**: scenes, prefabs, ScriptableObjects, tilemaps. Scripts hold
  behaviour. They don't build the static world at runtime or hardcode values that belong in serialized fields.
- Don't hand-edit `.unity` / `.prefab` / `.asset` YAML. Use `apply` / `set`.
- `u note "…"` before each chunk of work. Use meaningful names and hierarchy.
- `apply`, `set` and the tilemap commands save the scene themselves. After Pipeline built-ins, run `u save_scene`.
- Look before and after: `u ls` (text) is cheap; `u shot` (image) when it's visual.

## Scene text: one format for reading and writing
`u ls` prints it and `u apply` consumes it. Indent 2 spaces per level.
```
Level
  Player pos=(0,1) tag=Player layer=Characters      ← object: name + attrs (pos rot scale active tag layer static prefab= primitive=)
    +SpriteRenderer sprite=Assets/Art/hero.png color=#3FA7FF sorting=Characters/10
    +Rigidbody2D gravityScale=3 interpolate=Interpolate constraints=FreezeRotation
    -BoxCollider2D                                  ← remove component
    Weapon pos=(0.5,0) prefab=Assets/Prefabs/Sword.prefab
  !OldThing                                         ← delete child
  Crate#2 pos=(4,0)                                 ← 2nd sibling named Crate
  Ball primitive=sphere pos=(0,3,0)                 ← 3D primitive (cube sphere capsule cylinder plane quad)
    +Rigidbody mass=2
```
- `apply` is an **upsert**: objects are matched by name under their parent or created, components are added if
  missing, and only the listed values are set. Nothing else is touched. Missing tags, layers and sorting layers are created.
- It's **atomic** and one undo step. On any error nothing changes, and the error names the line (with a did-you-mean).
- Directives: `@root /Level` (top-level lines go under it), `@prefab Assets/Prefabs/X.prefab` (the next top-level
  object *is* that prefab: created or edited, then saved; the following top-level object returns to the scene),
  `@scene Assets/Scenes/X.unity` (open or create).
- `u ls -v` output can be pasted back into `apply`. Collapsed lines (`Enemy0..Enemy49 ×50`) can't be;
  use `u ls /Path -v --expand` to get individual lines.

## Commands
```
u ls [/Path | Assets/X.prefab] [-v]     tree (-v: +Component lines with non-default values)
u get <handle> [filter]                 non-default properties; filter by path or component name (--all for everything)
u set <handle> Comp.prop=value …        e.g. u set /Player Rigidbody2D.gravityScale=2 SpriteRenderer.color=red pos=0,1
u apply <file> | u apply <<'EOF' … EOF  scene text; --dry-run reports without changing anything
u rm <handle> …                         objects or assets
u diff [/Path]                          changes since the last `u diff`; run it at the start of a turn to see the human's edits
u shot [/Path | rect=x,y,w,h] [name]    PNG of an area (orthographic temp camera; doesn't move their views) → Read the path
u shot game | u shot scene              what the Game view / their Scene view shows
u play [secs] [keep]                    play → game screenshot + console (deduped) → stop
u errors · u logs                       NEW console warnings+errors · all new output
u note "…"   u sync   u status   u cmds [word]   u help <cmd>
```
Handles: `/Root/Child`, `/Root/Child:Component`, `Assets/x.png`, `Assets/sheet.png#sheet_3` (sub-asset).
References in values work the same way. A texture path assigned to a Sprite field picks its Sprite.
Unity built-ins: `builtin:Cube`, `builtin:Sphere`, `builtin:Default-Material`, ….
Arguments are positional or `key=value`, typed by the command's schema: `1,2` → `[1,2]`, `key=@file` reads a file,
`--flag` → true.

**Batching:** pipe many commands into one call; it stops at the first error. Embed scene text with `apply <<END` … `END`:
```
u <<'EOF'
note Building level 1
apply <<END
Level
  Ground pos=(0,-2)
END
set /Level/Ground SpriteRenderer.color=#4caf50
EOF
```

**Files you write from the shell** (`.cs`, `.png`, …) are imported automatically before your next `u` command,
waiting for the compile if needed. Compile errors stop that command and are printed as `file:line`. Fix them first.
`u sync` forces it.

## 2D helpers (`u help <name>`)
`make_sprite` (placeholder sprite asset) · `sprite_import` (PPU/filter/pivot; grid slicing needs com.unity.2d.sprite) ·
`make_tiles` · `tilemap_create` · `tilemap_read` / `tilemap_paint` (tilemap ⇄ ASCII + legend) · `tilemap_fill` ·
`frame` (point their Scene view at something)

## Bigger jobs
- Generated content: write an editor builder script **outside `Assets/`** (e.g. `<project>/AgentScripts/X.cs`, public
  static entry) and run it with `u run <path> X.Build`. It compiles in memory, with no domain reload.
- One-liners: `u eval code='return …;'`. Anything else the editor exposes: `u cmds <word>`.

## Gotchas
- Play-mode changes are lost on stop. A modal dialog blocks everything (u tells you; ask the human).
- Unity's fake null: `GetComponent` results defeat `??`/`?.`. Use `TryGetComponent`.
- Odin-only serialized data (SerializedMonoBehaviour dictionaries) is invisible to ls/get/set.
- Screenshots go to `<project>/Temp/claude/`, which is wiped when the editor restarts.
- Several Unity projects in one workspace: set `UNITY_PROJECT=/path` to pick one.
