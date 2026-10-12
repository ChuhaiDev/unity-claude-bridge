# Claude Bridge for Unity

*Unofficial community tool — not made by or affiliated with Anthropic.*

Lets [Claude Code](https://claude.com/claude-code) work inside your open Unity editor. It reads the scene as
compact text, builds scenes and prefabs as real saved content (atomic, undoable), edits components, sprites and
tilemaps, play-tests, takes screenshots, and sees compile errors. You watch it all in **Window ▸ Claude Activity**.
Details: [package README](com.chuhaidev.claude-bridge/README.md).

## Install (Unity 6000.0+, Python 3)

Add the registry once per project in **Edit ▸ Project Settings ▸ Package Manager ▸ Scoped Registries**:

| Name | URL | Scope |
|---|---|---|
| Claude Bridge | `https://chuhaidev.github.io/unity-claude-bridge` | `com.chuhaidev` |

Then **Package Manager ▸ My Registries ▸ Claude Bridge for Unity ▸ Install**. Updates show up there too.
On the next editor load the package installs its Claude Code skill (`.claude/skills/unity-editor/`) into the project.

Or skip the registry and install by git URL:
`https://github.com/ChuhaiDev/unity-claude-bridge.git?path=com.chuhaidev.claude-bridge`

## Releasing
Bump `version` in `com.chuhaidev.claude-bridge/package.json` and push to `main`. The
[workflow](.github/workflows/registry.yml) rebuilds the registry on GitHub Pages
([tools/build_registry.py](tools/build_registry.py)) and tags the version. Published versions are immutable.
