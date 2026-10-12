# Claude Bridge for Unity

*Unofficial community tool — not made by or affiliated with Anthropic.*

Lets [Claude Code](https://claude.com/claude-code) work *inside* your open Unity editor instead of guessing at
files. Claude reads the scene as compact text, builds scenes and prefabs as real saved content (undoable, atomic),
edits components and import settings, paints tilemaps, runs play mode, takes screenshots, and sees compile errors
and console output. You can watch all of it in **Window ▸ Claude Activity**.

## Install
1. Package Manager ▸ **+** ▸ **Install package from tarball…** ▸ pick the `.tgz`
   (or *from disk* / *from git URL* if you keep the source somewhere).
   Unity's `com.unity.pipeline` package (the editor's local command server) is pulled in automatically.
2. That's it. On the next editor load the package installs a Claude Code skill to
   `<project>/.claude/skills/unity-editor/`, and also to the enclosing workspace if the project sits inside a folder
   with `.git`, `.claude` or `CLAUDE.md`. Start Claude Code in either place and it uses the skill when you ask for Unity work.

**Requirements:** Unity 6000.0+, Python 3 on PATH (the client is a single script), and the project open in the editor
while Claude works.

## What's in it
- `Editor/` holds the editor commands, registered with the Pipeline server: `snapshot`/`apply` (scene text),
  `inspect`/`set`, `capture_area`, sprite/tile/tilemap helpers, `refresh`/`compile_errors`, `note`.
- `Editor/Activity` is the Claude Activity window. Click an entry to select what it touched; double-click to frame it.
- `Skill~/` holds the agent side (`u` client + `SKILL.md`), copied out by `Editor/Setup/SkillInstaller.cs`.
  **Tools ▸ Claude Bridge ▸ Reinstall Agent Skill** forces a re-copy.

Optional: with `com.unity.2d.sprite` installed, `sprite_import` can also grid-slice sprite sheets.

## Security
The Pipeline server listens on 127.0.0.1 only, and every request needs a per-session token from
`Library/Pipeline/`. Anything that can read your project folder can drive the editor, including running C#,
so treat it like any local dev server.
