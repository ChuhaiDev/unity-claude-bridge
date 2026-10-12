using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Claude.Bridge
{
    /// <summary>
    /// Installs the agent side (the `u` client + SKILL.md, shipped in this package's Skill~ folder) as a Claude Code
    /// skill, so "add the package" is all a project needs. Targets:
    ///   &lt;project&gt;/.claude/skills/unity-editor/
    ///   &lt;workspace&gt;/.claude/skills/unity-editor/   — when the Unity project sits inside a bigger workspace
    ///                                                  (an ancestor dir with .git, .claude or CLAUDE.md)
    /// Re-copied whenever the package's files change; a project.txt beside the client pins it to this project.
    /// </summary>
    [InitializeOnLoad]
    static class SkillInstaller
    {
        const string SkillName = "unity-editor";

        static SkillInstaller()
        {
            // Run right away: an unfocused editor may not tick, so delayCall/update could wait indefinitely.
            if (!Install(force: false)) EditorApplication.delayCall += () => Install(force: false);
        }

        [MenuItem("Tools/Claude Bridge/Reinstall Agent Skill")]
        static void Reinstall() => Install(force: true);

        [MenuItem("Tools/Claude Bridge/Show Agent Skill Location")]
        static void Reveal()
        {
            var t = Targets().FirstOrDefault();
            if (t != null && Directory.Exists(t)) EditorUtility.RevealInFinder(Path.Combine(t, "SKILL.md"));
        }

        static string ProjectRoot => Path.GetDirectoryName(Application.dataPath);

        static IEnumerable<string> Targets()
        {
            yield return Path.Combine(ProjectRoot, ".claude", "skills", SkillName);
            var d = Directory.GetParent(ProjectRoot);
            for (int i = 0; i < 4 && d != null; i++, d = d.Parent)
            {
                if (d.FullName == Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) yield break;
                bool workspace = Directory.Exists(Path.Combine(d.FullName, ".git"))
                    || Directory.Exists(Path.Combine(d.FullName, ".claude"))
                    || File.Exists(Path.Combine(d.FullName, "CLAUDE.md"));
                if (workspace)
                {
                    yield return Path.Combine(d.FullName, ".claude", "skills", SkillName);
                    yield break;
                }
            }
        }

        /// <returns>false if the package couldn't be located yet (retry later).</returns>
        static bool Install(bool force)
        {
            try
            {
                var pkg = PackageInfo.FindForAssembly(typeof(SkillInstaller).Assembly);
                if (pkg == null) return false;
                var src = Path.Combine(pkg.resolvedPath, "Skill~");
                if (!Directory.Exists(src)) return true;
                var files = Directory.GetFiles(src).Where(f => !f.EndsWith(".meta")).OrderBy(f => f).ToArray();
                var hash = Hash(files) + "|" + ProjectRoot;

                foreach (var dst in Targets())
                {
                    var stamp = Path.Combine(dst, ".bridge-hash");
                    if (!force && File.Exists(stamp) && File.ReadAllText(stamp) == hash) continue;
                    Directory.CreateDirectory(dst);
                    foreach (var f in files) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
                    File.WriteAllText(Path.Combine(dst, "project.txt"), ProjectRoot + "\n");
                    File.WriteAllText(stamp, hash);
                    MakeExecutable(Path.Combine(dst, "u"));
                    Debug.Log($"Claude Bridge: agent skill '{SkillName}' installed at {dst} — Claude Code picks it up automatically.");
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("Claude Bridge: couldn't install the agent skill: " + e.Message);
            }
            return true;
        }

        static string Hash(IEnumerable<string> files)
        {
            using (var sha = SHA1.Create())
            {
                var sb = new StringBuilder();
                foreach (var f in files) sb.Append(Path.GetFileName(f)).Append(Convert.ToBase64String(sha.ComputeHash(File.ReadAllBytes(f))));
                return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())));
            }
        }

        static void MakeExecutable(string path)
        {
            if (Application.platform == RuntimePlatform.WindowsEditor || !File.Exists(path)) return;
            try
            {
                using (var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("chmod", $"+x \"{path}\"") { UseShellExecute = false, CreateNoWindow = true }))
                    p?.WaitForExit(2000);
            }
            catch { /* the skill also documents `python3 u` */ }
        }
    }
}
