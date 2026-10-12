using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Unity.Pipeline.Commands;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Claude.Bridge
{
    /// <summary>Scene text in (apply) and out (snapshot), plus property-level inspect.</summary>
    static class ViewCommands
    {
        [CliCommand("snapshot",
            "The hierarchy as scene text (the format `apply` accepts). Default: compact (components listed in [brackets]), " +
            "similar siblings collapsed, depth auto-reduced to fit max_lines. values=true: one '+Component k=v' line per component " +
            "with non-default values — copy, edit, and pass to `apply`.",
            Tags = new[] { "scenes", "claude" })]
        static object Snapshot(
            [CliArg("root", "Subtree handle (/Path), or an Assets/….prefab. Default: open scene(s) / prefab stage.")] string root = null,
            [CliArg("values", "Component lines with non-default values.")] bool values = false,
            [CliArg("depth", "Max depth (-1 = as deep as fits).")] int depth = -1,
            [CliArg("expand", "Don't collapse similar siblings.")] bool expand = false,
            [CliArg("max_lines", "Line budget.")] int maxLines = 150)
        {
            var w = new SceneTextWriter { Values = values, Expand = expand, MaxLines = maxLines };
            if (!string.IsNullOrEmpty(root) && root.EndsWith(".prefab"))
            {
                var contents = PrefabUtility.LoadPrefabContents(root);
                try { return new { text = "@prefab " + root + "\n" + w.Render(null, new[] { contents.transform }, depth) }; }
                finally { PrefabUtility.UnloadPrefabContents(contents); }
            }
            if (!string.IsNullOrEmpty(root))
            {
                var go = Refs.GameObject(root);
                var parent = go.transform.parent;
                var text = w.Render(null, new[] { go.transform }, depth);
                return new { text = (parent != null ? $"@root {ScenePath.Of(parent)}\n" : "") + text };
            }

            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null)
                return new { text = w.Render($"prefab stage {stage.assetPath}{(stage.scene.isDirty ? " (unsaved)" : "")}", new[] { stage.prefabContentsRoot.transform }, depth) };

            var sb = new StringBuilder();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                var flags = new List<string>();
                if (scene.isDirty) flags.Add("unsaved");
                if (scene == SceneManager.GetActiveScene() && SceneManager.sceneCount > 1) flags.Add("active");
                if (EditorApplication.isPlaying) flags.Add("PLAYING — edits are temporary");
                var header = $"scene {(string.IsNullOrEmpty(scene.path) ? (string.IsNullOrEmpty(scene.name) ? "(untitled, not saved)" : scene.name) : scene.path)}{(flags.Count > 0 ? " (" + string.Join(", ", flags) + ")" : "")}";
                sb.Append(w.Render(header, scene.GetRootGameObjects().Select(g => g.transform).ToList(), depth));
            }
            return new { text = sb.ToString() };
        }

        [CliCommand("apply",
            "Make the scene/prefab match scene text (same format `snapshot` prints). Upsert: objects matched by name under their parent " +
            "or created; '+Type k=v' ensures a component and sets values; '-Type' removes a component; '!Name' deletes a child. " +
            "Object attrs: pos rot scale active tag layer static prefab=Assets/X.prefab (instantiate). Directives: @root /Path, " +
            "@prefab Assets/X.prefab (edit/create a prefab), @scene Assets/S.unity. Indent 2 spaces per level. " +
            "All scene edits are ONE undo step and atomic (any error reverts everything). Reports only what changed.",
            Tags = new[] { "scenes", "gameobjects", "prefabs", "claude" })]
        static object Apply(
            [CliArg("text", "Scene text.", Required = true)] string text,
            [CliArg("dry_run", "Report what would change, then revert.")] bool dryRun = false,
            [CliArg("save", "Save touched scenes afterwards.")] bool save = true)
        {
            if (EditorApplication.isPlaying) save = false;
            Undo.IncrementCurrentGroup();
            var group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Claude: apply");
            var a = new SceneTextApplier();
            try
            {
                a.Apply(text);
            }
            catch (Exception e)
            {
                Undo.RevertAllDownToGroup(group);
                a.RollbackAssets();
                throw new ArgumentException(e.Message + "\n(nothing was changed)");
            }
            Undo.CollapseUndoOperations(group);

            var sb = new StringBuilder();
            foreach (var r in a.Report) sb.AppendLine(r);
            if (a.Report.Count == 0) sb.AppendLine("no changes");
            if (a.Unchanged > 0 && a.Report.Count > 0) sb.AppendLine($"({a.Unchanged} already matched)");
            foreach (var w in a.Warnings) sb.AppendLine("note: " + w);

            if (dryRun)
            {
                Undo.RevertAllDownToGroup(group);
                a.RollbackAssets();
                return new { text = "DRY RUN (reverted)\n" + sb };
            }
            if (save)
                foreach (var s in a.TouchedScenes)
                    if (s.isDirty && !string.IsNullOrEmpty(s.path)) { EditorSceneManager.SaveScene(s); sb.AppendLine($"saved {s.path}"); }
            return new { text = sb.ToString() };
        }

        [CliCommand("inspect",
            "Serialized properties of a GameObject (all components), a component, or an asset — one 'path = value' line each, " +
            "ONLY values that differ from defaults unless all=true. Paths are exactly what `set` accepts. importer=true: the asset's import settings.",
            Tags = new[] { "gameobjects/components", "assets", "claude" })]
        static object Inspect(
            [CliArg("target", "Handle: /Path, /Path:Component, Assets/x.asset, Assets/x.png#sub…", Required = true)] string target,
            [CliArg("filter", "Only paths containing this (case-insensitive).")] string filter = null,
            [CliArg("all", "Include values equal to the defaults.")] bool all = false,
            [CliArg("importer", "Inspect the asset's importer settings.")] bool importer = false,
            [CliArg("max_array", "Expand arrays up to this many elements.")] int maxArray = 16,
            [CliArg("max_chars", "Output budget.")] int maxChars = 4000)
        {
            var obj = Refs.Resolve(target);
            var sb = new StringBuilder();
            if (importer)
            {
                var imp = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(obj)) ?? throw new ArgumentException($"{target} has no importer");
                Dump(sb, imp, filter, all, maxArray);
            }
            else if (obj is GameObject go)
            {
                sb.AppendLine($"{Fmt.Ref(go)}{(go.activeSelf ? "" : " active=false")}{(go.tag != "Untagged" ? " tag=" + go.tag : "")}{(go.layer != 0 ? " layer=" + LayerMask.LayerToName(go.layer) : "")}");
                foreach (var c in go.GetComponents<Component>())
                    if (c == null) sb.AppendLine("+<missing script>");
                    else Dump(sb, c, filter, all, maxArray);
            }
            else Dump(sb, obj, filter, all, maxArray);

            var text = sb.ToString();
            if (text.Length > maxChars)
            {
                var cut = text.Substring(0, maxChars);
                cut = cut.Substring(0, cut.LastIndexOf('\n') + 1);
                text = cut + $"… {text.Length - cut.Length} more chars (use filter= or max_chars=)\n";
            }
            return new { text };
        }

        static readonly HashSet<string> Noise = new HashSet<string>
        {
            "m_Script", "m_ObjectHideFlags", "m_CorrespondingSourceObject", "m_PrefabInstance", "m_PrefabAsset",
            "m_GameObject", "m_EditorHideFlags", "m_EditorClassIdentifier",
        };

        static readonly Dictionary<Type, Dictionary<string, string>> s_LeafDefaults = new Dictionary<Type, Dictionary<string, string>>();

        static Dictionary<string, string> LeafDefaults(Type t)
        {
            if (s_LeafDefaults.TryGetValue(t, out var d)) return d;
            d = new Dictionary<string, string>();
            var dd = d;
            Defaults.WithInstance(t, so => { foreach (var (path, v) in Leaves(so, 64)) dd[path] = v; });
            return s_LeafDefaults[t] = d.Count > 0 ? d : null;
        }

        static void Dump(StringBuilder sb, Object o, string filter, bool all, int maxArray)
        {
            var defaults = all || (o is MonoBehaviour && Defaults.IsUserType(o.GetType())) ? null : LeafDefaults(o.GetType());
            var header = o is Component c ? "+" + c.GetType().Name : $"[{o.GetType().Name}] {Fmt.Ref(o)}";
            var lines = new List<string>();
            int hidden = 0;
            // A filter that names the component (e.g. "Rigidbody") shows that whole component.
            var typeMatch = !string.IsNullOrEmpty(filter) && o.GetType().Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
            if (typeMatch) filter = null;
            foreach (var (path, v) in Leaves(new SerializedObject(o), maxArray))
            {
                if (!string.IsNullOrEmpty(filter) && path.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (defaults != null && defaults.TryGetValue(path, out var dv) && dv == v) { hidden++; continue; }
                lines.Add($"  {path} = {v}");
            }
            if (o is Renderer r && (r.sortingLayerID != 0 || r.sortingOrder != 0) && (string.IsNullOrEmpty(filter) || "sorting".Contains(filter.ToLowerInvariant())))
                lines.Add($"  sorting = {r.sortingLayerName}/{r.sortingOrder}");
            if (lines.Count == 0 && !string.IsNullOrEmpty(filter)) return;
            sb.AppendLine(header);
            foreach (var l in lines) sb.AppendLine(l);
        }

        static readonly Regex ArrayData = new Regex(@"\.Array\.data\[(\d+)\]");

        /// <summary>Friendly property path: m_Materials.Array.data[0] → materials[0].</summary>
        static string Friendly(string path) =>
            string.Join(".", ArrayData.Replace(path, "[$1]").Split('.').Select(SceneTextWriter.Key));

        static IEnumerable<(string path, string value)> Leaves(SerializedObject so, int maxArray)
        {
            var it = so.GetIterator();
            bool enter = true;
            while (it.NextVisible(enter))
            {
                enter = false;
                if (it.depth == 0 && Noise.Contains(it.name)) continue;
                if (it.propertyType == SerializedPropertyType.ArraySize) continue;
                if (it.propertyType == SerializedPropertyType.Generic)
                {
                    if (it.name == "Array") { enter = true; continue; }
                    if (it.isArray)
                    {
                        if (it.arraySize == 0 || it.arraySize > maxArray) yield return (Friendly(it.propertyPath), it.arraySize == 0 ? "[]" : $"[{it.arraySize} items]");
                        else enter = true;
                        continue;
                    }
                    if (it.hasVisibleChildren) { enter = true; continue; }
                }
                yield return (Friendly(it.propertyPath), SceneTextWriter.Token(Fmt.Prop(it, 2)));
                if (it.propertyType == SerializedPropertyType.ManagedReference) enter = true;
            }
        }
    }
}
