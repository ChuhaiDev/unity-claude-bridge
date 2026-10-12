using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using Unity.Pipeline.Commands;
using Unity.Pipeline.Editor.Authoring;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Claude.Bridge
{
    /// <summary>Write-side commands that complement the Pipeline built-ins.</summary>
    static class EditCommands
    {
        [CliCommand("set",
            "Set serialized properties on a component, GameObject, asset, or asset importer in ONE undo step. " +
            "Keys are friendly property paths: 'color', 'sprite', 'size.x', 'items[2].hp' (m_ prefixes optional). " +
            "When target is a GameObject, prefix keys with the component: 'SpriteRenderer.color'. " +
            "Values: numbers/bools/strings, '#RRGGBB' or [r,g,b,a] colors, [x,y,z] vectors, enum names, " +
            "object refs as strings ('Assets/hero.png' auto-picks its Sprite, '/Player:Rigidbody2D', null), arrays, {struct}. " +
            "Works in play mode too (changes are lost on stop).",
            Tags = new[] { "gameobjects/components", "assets", "claude" })]
        static object Set(
            [CliArg("target", "Handle: /Path/To/Obj, /Obj:Component, Assets/x.asset, Assets/x.png#sub…", Required = true)] string target,
            [CliArg("values", "Map of property path → value.", Required = true)] JObject values,
            [CliArg("importer", "Set the asset's import settings (then reimport) instead of the asset itself.")] bool importer = false)
        {
            var obj = Refs.Resolve(target);
            var touched = new Dictionary<Object, SerializedObject>();
            var report = new StringBuilder();

            using (new AuthoringUndoScope($"Claude: set {target}"))
            {
                foreach (var kv in values)
                {
                    var (owner, path) = Owner(obj, kv.Key, importer);
                    if (!touched.TryGetValue(owner, out var so)) touched[owner] = so = new SerializedObject(owner);
                    var p = Props.Find(so, path)
                        ?? throw new ArgumentException($"{Fmt.Ref(owner)} has no property '{path}'{Props.Suggest(so, path)}");
                    var before = Fmt.Prop(p);
                    Props.Assign(p, kv.Value);
                    var after = Fmt.Prop(p);
                    var key = string.Join(".", System.Text.RegularExpressions.Regex.Replace(p.propertyPath, @"\.Array\.data\[(\d+)\]", "[$1]").Split('.').Select(SceneTextWriter.Key));
                    report.Append(before == after ? "= " : "~ ").Append(Fmt.Ref(owner)).Append(' ').Append(key).Append('=').AppendLine(SceneTextWriter.Token(after));
                }
                foreach (var so in touched.Values) so.ApplyModifiedProperties();
            }

            var scenes = new HashSet<UnityEngine.SceneManagement.Scene>();
            foreach (var owner in touched.Keys)
            {
                if (owner is AssetImporter ai) ai.SaveAndReimport();
                else if (EditorUtility.IsPersistent(owner)) { EditorUtility.SetDirty(owner); AssetDatabase.SaveAssetIfDirty(owner); }
                else if (owner is Component c) scenes.Add(c.gameObject.scene);
                else if (owner is GameObject g) scenes.Add(g.scene);
            }
            foreach (var sc in scenes) Persist.Save(sc);
            return new { text = report.ToString() };
        }

        static (Object owner, string path) Owner(Object obj, string key, bool importer)
        {
            if (importer)
                return (AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(obj)) ?? throw new ArgumentException("No importer for " + Fmt.Ref(obj)), key);

            if (obj is GameObject go)
            {
                var dot = key.IndexOf('.');
                if (dot > 0)
                {
                    var typeName = key.Substring(0, dot);
                    var t = Types.Object(typeName);
                    if (t != null && typeof(Component).IsAssignableFrom(t))
                    {
                        var c = Refs.Comp(go, t) ?? throw new ArgumentException($"{Fmt.Ref(go)} has no {typeName} (it has: {string.Join(", ", go.GetComponents<Component>().Where(x => x != null).Select(x => x.GetType().Name))})");
                        return (c, key.Substring(dot + 1));
                    }
                }
                switch (key)
                {
                    case "active": return (go, "m_IsActive");
                    case "name": return (go, "m_Name");
                    case "tag": return (go, "m_TagString");
                    case "layer": return (go, "m_Layer");
                    case "static": return (go, "m_StaticEditorFlags");
                }
                // Position shorthands on the transform.
                if (key == "position" || key == "pos") return (go.transform, "m_LocalPosition");
                if (key == "scale") return (go.transform, "m_LocalScale");
                var has = string.Join(", ", go.GetComponents<Component>().Where(c => c != null).Select(c => c.GetType().Name));
                throw new ArgumentException(dot > 0
                    ? $"{Fmt.Ref(go)} has no component '{key.Substring(0, dot)}' (it has: {has}). Add one with `apply` (+Type line)."
                    : $"'{key}': prefix it with a component, e.g. 'SpriteRenderer.{key}' (components: {has}). Object attrs: pos, scale, active, name, tag, layer.");
            }
            return (obj, key);
        }

        [CliCommand("refresh",
            "AssetDatabase.Refresh(): import files changed on disk (new/edited .cs, .png, …) and kick off script " +
            "compilation if needed. The editor does NOT notice external file edits until this runs (or it regains focus).",
            Tags = new[] { "assets", "scripts/compile", "claude" })]
        static object Refresh()
        {
            AssetDatabase.Refresh(ImportAssetOptions.Default);
            return new { compiling = EditorApplication.isCompiling || EditorApplication.isUpdating };
        }

        [CliCommand("compile_errors",
            "Current C# compiler errors/warnings from the last compilation (file:line: message). Empty when clean.",
            MainThreadRequired = true, Tags = new[] { "scripts/compile", "claude" })]
        static object CompileErrors()
        {
            return new { text = CompileTracker.Report(), failed = EditorUtility.scriptCompilationFailed, compiling = EditorApplication.isCompiling };
        }

        [CliCommand("frame",
            "Point the Scene view at an object (or everything in the open scenes), framed on its renderer/collider bounds, " +
            "optionally selecting it. Use before `capture_scene_view`, and to show the human what you're working on.",
            Tags = new[] { "navigation", "claude" })]
        static object Frame(
            [CliArg("target", "Handle to frame. Omit to frame all renderers.")] string target = null,
            [CliArg("select", "Also select it.")] bool select = false,
            [CliArg("padding", "Extra margin in world units.")] float padding = 1f)
        {
            var sv = SceneView.lastActiveSceneView ?? (SceneView.sceneViews.Count > 0 ? (SceneView)SceneView.sceneViews[0] : null);
            if (sv == null) throw new InvalidOperationException("No Scene view is open.");
            var bounds = World.Bounds(target);
            bounds.Expand(padding * 2);
            if (EditorSettings.defaultBehaviorMode == EditorBehaviorMode.Mode2D) sv.in2DMode = true;
            sv.Frame(bounds, true);
            sv.Repaint();
            if (select && target != null) Selection.activeGameObject = Refs.GameObject(target);
            return new { center = Fmt.V3(bounds.center), size = Fmt.V3(bounds.size) };
        }

        [CliCommand("note",
            "Post a human-readable status line to the user's Claude Activity window and as a Scene view toast. " +
            "Use it to say what you're about to do.",
            Tags = new[] { "claude" })]
        static object Note([CliArg("text", "Message.", Required = true)] string text)
        {
            ActivityFeed.ShowToast(text);
            return new { ok = true };
        }
    }

    /// <summary>Keeps the messages of the most recent compilation per assembly.</summary>
    [InitializeOnLoad]
    static class CompileTracker
    {
        const string Key = "Claude.Bridge.CompileMessages";

        static CompileTracker()
        {
            CompilationPipeline.compilationStarted += _ => SessionState.SetString(Key, "");
            CompilationPipeline.assemblyCompilationFinished += (asm, msgs) =>
            {
                var lines = msgs.Where(m => m.type == CompilerMessageType.Error || m.type == CompilerMessageType.Warning)
                    .Where(m => !(m.file ?? "").Contains("PackageCache") && !(m.file ?? "").Contains("Plugins/Sirenix"))
                    .Select(m => $"{(m.type == CompilerMessageType.Error ? "error" : "warn")} {m.file}:{m.line}: {Strip(m.message)}");
                var all = SessionState.GetString(Key, "") + string.Join("\n", lines.Select(l => l + "\n"));
                SessionState.SetString(Key, all);
            };
        }

        static string Strip(string msg)
        {
            // Unity prefixes "path(line,col): error CS1234:" — keep just the CS code and text.
            var i = msg.IndexOf(": error ", StringComparison.Ordinal);
            if (i < 0) i = msg.IndexOf(": warning ", StringComparison.Ordinal);
            return i >= 0 ? msg.Substring(i + 2) : msg;
        }

        public static string Report()
        {
            var s = SessionState.GetString(Key, "");
            // Errors first, de-duplicated, warnings capped.
            var lines = s.Split('\n').Where(l => l.Length > 0).Distinct().ToList();
            var errors = lines.Where(l => l.StartsWith("error")).ToList();
            var warns = lines.Where(l => l.StartsWith("warn")).Take(20).ToList();
            return string.Join("\n", errors.Concat(warns));
        }
    }
}
