using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Unity.Pipeline.Editor.Authoring;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Claude.Bridge
{
    /// <summary>
    /// Makes the hierarchy match a piece of scene text. Upsert semantics: objects are found by name under their
    /// parent (or created), components are added if missing, listed values are set. Nothing unlisted is removed
    /// except via explicit lines:
    ///
    ///   Name k=v …        ensure object (attrs: pos rot scale active tag layer static prefab)
    ///   +Type k=v …       ensure component, set values (also: enabled=, sorting=Layer/order)
    ///   -Type             remove component
    ///   !Name             delete child object
    ///   @root /Path       following top-level lines are relative to /Path (default: active scene root)
    ///   @prefab Assets/X.prefab   the next top-level object IS that prefab (created if missing); the section ends at
    ///                             the following top-level object, which goes back to the scene
    ///   @scene Assets/Scenes/X.unity   open (or create) that scene first
    ///   # comment
    ///
    /// Scene changes form ONE undo step; any error reverts all of them.
    /// </summary>
    class SceneTextApplier
    {
        public readonly List<string> Report = new List<string>();
        public readonly List<string> Warnings = new List<string>();
        public int Unchanged;
        public readonly HashSet<Scene> TouchedScenes = new HashSet<Scene>();

        // Current section context.
        Transform m_Root;                 // parent for top-level lines (null = scene root)
        GameObject m_PrefabRoot;          // contents root while inside an @prefab section
        string m_PrefabPath;
        bool m_PrefabIsNew;
        bool m_PrefabRootBound;
        Scene m_Preview;
        int m_ReportAtSection;
        int m_LineNo;

        readonly List<(int level, Transform t)> m_Stack = new List<(int, Transform)>();

        class Line
        {
            public int Level;
            public char Kind;      // 'o' object, '+' component, '-' remove component, '!' delete object, '@' directive
            public string Name;
            public List<(string key, string val)> Attrs = new List<(string, string)>();
            public List<string> Bracket;  // [A B C] component list on an object line
        }

        public void Apply(string text)
        {
            Folders.Created = new List<string>();
            var lines = text.Replace("\r", "").Split('\n');
            try
            {
                for (int i = 0; i < lines.Length; i++)
                {
                    m_LineNo = i + 1;
                    var line = Parse(lines[i]);
                    if (line == null) continue;
                    try { Execute(line); }
                    catch (Exception e) when (!(e is ApplyException))
                    {
                        throw new ApplyException($"line {m_LineNo}: {e.Message}\n  > {lines[i].Trim()}");
                    }
                }
                FlushPrefab(save: true);
            }
            catch
            {
                FlushPrefab(save: false);
                throw;
            }
        }

        class ApplyException : Exception { public ApplyException(string m) : base(m) { } }

        Exception Err(string msg) => new ApplyException($"line {m_LineNo}: {msg}");

        // ── parsing ─────────────────────────────────────────────────────────────────

        Line Parse(string raw)
        {
            var trimmed = raw.TrimEnd();
            var body = trimmed.TrimStart();
            if (body.Length == 0 || body.StartsWith("#") || body.StartsWith("…")) return null;
            int spaces = 0;
            foreach (var ch in trimmed) { if (ch == ' ') spaces++; else if (ch == '\t') spaces += 2; else break; }
            if (spaces % 2 != 0) throw Err("indentation must be multiples of 2 spaces");

            var l = new Line { Level = spaces / 2 };
            var tokens = Tokenize(body);
            if (tokens.Count == 0) return null;
            var head = tokens[0];

            if (head.StartsWith("@")) { l.Kind = '@'; l.Name = head.Substring(1); l.Attrs.Add(("arg", string.Join(" ", tokens.Skip(1)))); return l; }
            if (head.StartsWith("+") || head.StartsWith("-")) { l.Kind = head[0]; l.Name = head.Substring(1); ParseAttrs(l, tokens.Skip(1)); return l; }
            if (head.StartsWith("!")) { l.Kind = '!'; l.Name = Unquote(head.Substring(1)); return l; }

            // Object line: the name runs until the first k=v / [ / … token (so names may contain spaces).
            l.Kind = 'o';
            int n = 0;
            if (head.StartsWith("\"")) { l.Name = Unquote(head); n = 1; }
            else
            {
                var parts = new List<string>();
                while (n < tokens.Count && !IsAttr(tokens[n]) && !tokens[n].StartsWith("[") && !tokens[n].StartsWith("…")) parts.Add(tokens[n++]);
                l.Name = string.Join(" ", parts);
            }
            if (tokens.Any(t => t.StartsWith("×")) || Regex.IsMatch(l.Name, @"\.\.") )
                throw Err($"'{l.Name}' is a collapsed group from `ls`; it can't be applied. List the objects individually (or `ls expand=true`).");
            if (l.Name.Length == 0) throw Err("object line without a name");
            ParseAttrs(l, tokens.Skip(n));
            return l;
        }

        static bool IsAttr(string tok) { var eq = tok.IndexOf('='); return eq > 0 && !tok.StartsWith("\""); }

        void ParseAttrs(Line l, IEnumerable<string> tokens)
        {
            foreach (var t in tokens)
            {
                if (t.StartsWith("…") || t.StartsWith("(each")) break;
                if (t.StartsWith("["))
                {
                    l.Bracket = Regex.Replace(t.Trim('[', ']'), @"\{[^}]*\}", "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(x => Regex.Replace(x, @"(\(off\)|\{.*)$", "")).ToList();
                    continue;
                }
                var eq = t.IndexOf('=');
                if (eq <= 0) throw Err($"expected key=value, got '{t}'");
                l.Attrs.Add((t.Substring(0, eq), t.Substring(eq + 1)));
            }
        }

        /// <summary>Split on spaces, keeping "quoted strings", (…), […], {…} together.</summary>
        static List<string> Tokenize(string s)
        {
            var list = new List<string>();
            var cur = new StringBuilder();
            int depth = 0;
            bool quote = false;
            for (int i = 0; i < s.Length; i++)
            {
                var c = s[i];
                if (quote)
                {
                    cur.Append(c);
                    if (c == '\\' && i + 1 < s.Length) { cur.Append(s[++i]); continue; }
                    if (c == '"') quote = false;
                    continue;
                }
                if (c == '"') { quote = true; cur.Append(c); continue; }
                if (c == '(' || c == '[' || c == '{') depth++;
                if (c == ')' || c == ']' || c == '}') depth = Math.Max(0, depth - 1);
                if (c == ' ' && depth == 0)
                {
                    if (cur.Length > 0) { list.Add(cur.ToString()); cur.Clear(); }
                    continue;
                }
                cur.Append(c);
            }
            if (cur.Length > 0) list.Add(cur.ToString());
            return list;
        }

        static string Unquote(string s)
        {
            if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"')
                return s.Substring(1, s.Length - 2).Replace("\\\"", "\"").Replace("\\n", "\n").Replace("\\\\", "\\");
            return s;
        }

        static readonly Regex Tuple = new Regex(@"^[a-z]*\(\s*-?[\d.eE+-]+(\s*,\s*-?[\d.eE+-]+)*\s*\)$");

        /// <summary>Scene-text value → JSON for Props.Assign. Null return = view-only placeholder (skipped).</summary>
        public static JToken Value(string v)
        {
            if (v.EndsWith("…\"") || v.EndsWith("…") || v.EndsWith("…]") || v.EndsWith("…}")) return null;
            if (Regex.IsMatch(v, @"^\[\d+ items\]$") || v == "{…}" || v.StartsWith("curve(") || v == "gradient" || v.StartsWith("bounds(") || v.StartsWith("ref ") || v.StartsWith("<"))
                return null;
            if (v.StartsWith("\"")) return new JValue(Unquote(v));
            if (v == "true" || v == "false") return new JValue(v == "true");
            if (v == "null") return JValue.CreateNull();
            if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return new JValue(d);
            if (Tuple.IsMatch(v))
            {
                var inner = v.Substring(v.IndexOf('(') + 1).TrimEnd(')');
                return new JArray(inner.Split(',').Select(x => (object)double.Parse(x.Trim(), CultureInfo.InvariantCulture)));
            }
            if (v.StartsWith("[") && v.EndsWith("]"))
            {
                try { return JArray.Parse(v); }
                catch
                {
                    var inner = v.Substring(1, v.Length - 2).Trim();
                    if (inner.Length == 0) return new JArray();
                    return new JArray(SplitTop(inner).Select(Value));
                }
            }
            if (v.StartsWith("{") && v.EndsWith("}"))
            {
                try { return JObject.Parse(v); }
                catch
                {
                    var o = new JObject();
                    foreach (var kv in Tokenize(v.Substring(1, v.Length - 2)))
                    {
                        var eq = kv.IndexOf('=');
                        if (eq > 0) o[kv.Substring(0, eq)] = Value(kv.Substring(eq + 1));
                    }
                    return o;
                }
            }
            return new JValue(v);
        }

        /// <summary>Split "a, (1,2), b" on top-level commas.</summary>
        static IEnumerable<string> SplitTop(string s)
        {
            int depth = 0, start = 0;
            bool q = false;
            for (int i = 0; i < s.Length; i++)
            {
                var c = s[i];
                if (c == '"') q = !q;
                if (q) continue;
                if ("([{".IndexOf(c) >= 0) depth++;
                if (")]}".IndexOf(c) >= 0) depth--;
                if (c == ',' && depth == 0) { yield return s.Substring(start, i - start).Trim(); start = i + 1; }
            }
            yield return s.Substring(start).Trim();
        }

        // ── execution ───────────────────────────────────────────────────────────────

        void Execute(Line l)
        {
            if (l.Kind == '@') { Directive(l.Name, l.Attrs[0].val.Trim()); return; }

            if (l.Kind == '+' || l.Kind == '-')
            {
                var owner = OwnerAt(l.Level - 1) ?? throw Err($"component line '{l.Kind}{l.Name}' needs an object above it (indented one level less)");
                if (l.Kind == '+') EnsureComponent(owner.gameObject, l.Name, l.Attrs);
                else RemoveComponent(owner.gameObject, l.Name);
                return;
            }

            var parent = l.Level == 0 ? null : OwnerAt(l.Level - 1);
            if (l.Level > 0 && parent == null) throw Err("indented deeper than its parent");

            if (l.Kind == '!')
            {
                var victim = FindChild(parent, l.Name);
                if (victim == null) { Unchanged++; return; }
                var path = PathOf(victim);
                Touch(victim.gameObject);
                Undo.DestroyObjectImmediate(victim.gameObject);
                Report.Add($"- {path}");
                return;
            }

            // A second top-level object after a prefab's root ends that @prefab section (back to the scene).
            if (l.Level == 0 && m_PrefabRoot != null && m_PrefabRootBound) { FlushPrefab(true); m_Stack.Clear(); }

            var t = EnsureObject(parent, l);
            m_Stack.RemoveAll(e => e.level >= l.Level);
            m_Stack.Add((l.Level, t));
            if (l.Bracket != null)
                foreach (var c in l.Bracket) EnsureComponent(t.gameObject, c, new List<(string, string)>());
        }

        Transform OwnerAt(int level) => m_Stack.LastOrDefault(e => e.level == level).t;

        Transform FindChild(Transform parent, string segment)
        {
            if (parent != null) return ScenePath.Pick(ScenePath.Children(parent), segment);
            if (m_PrefabRoot != null) return null;
            if (m_Root != null) return ScenePath.Pick(ScenePath.Children(m_Root), segment);
            return ScenePath.Pick(SceneManager.GetActiveScene().GetRootGameObjects().Select(g => g.transform), segment);
        }

        Transform EnsureObject(Transform parent, Line l)
        {
            Transform t;
            bool created = false;
            var prefabAttr = l.Attrs.FirstOrDefault(a => a.key == "prefab").val;
            var primitiveAttr = l.Attrs.FirstOrDefault(a => a.key == "primitive").val;

            if (parent == null && m_PrefabRoot != null)
            {
                // The single top-level object of an @prefab section is the prefab root itself.
                m_PrefabRootBound = true;
                t = m_PrefabRoot.transform;
                if (m_PrefabIsNew) { t.name = Path.GetFileNameWithoutExtension(m_PrefabPath); created = true; Report.Add($"+ {PathOf(t)}"); }
            }
            else
            {
                t = FindChild(parent, l.Name);
                if (t == null)
                {
                    var (name, _) = ScenePath.Split(l.Name);
                    GameObject go;
                    if (!string.IsNullOrEmpty(prefabAttr))
                    {
                        var asset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabAttr) ?? throw Err($"no prefab at '{prefabAttr}'");
                        var scene = parent != null ? parent.gameObject.scene : m_Root != null ? m_Root.gameObject.scene : SceneManager.GetActiveScene();
                        go = (GameObject)PrefabUtility.InstantiatePrefab(asset, scene);
                        go.name = name;
                    }
                    else if (!string.IsNullOrEmpty(primitiveAttr))
                    {
                        if (!Enum.TryParse<PrimitiveType>(primitiveAttr, true, out var prim))
                            throw Err($"primitive= must be one of {string.Join(", ", Enum.GetNames(typeof(PrimitiveType)))}");
                        go = GameObject.CreatePrimitive(prim);
                        go.name = name;
                    }
                    else
                    {
                        var needsRect = parent != null && parent is RectTransform;
                        go = needsRect ? new GameObject(name, typeof(RectTransform)) : new GameObject(name);
                    }
                    var p = parent ?? m_Root;
                    if (p != null) go.transform.SetParent(p, false);
                    if (m_PrefabRoot == null) Undo.RegisterCreatedObjectUndo(go, "Claude apply");
                    t = go.transform;
                    created = true;
                    Report.Add($"+ {PathOf(t)}{(prefabAttr != null ? " (prefab " + prefabAttr + ")" : "")}");
                }
            }
            Touch(t.gameObject);

            var changes = new List<string>();
            foreach (var (key, val) in l.Attrs) ObjectAttr(t, key, val, changes);
            if (!created && changes.Count > 0) Report.Add($"~ {PathOf(t)} {string.Join(" ", changes)}");
            else if (created && changes.Count > 0) Report[Report.Count - 1] += " " + string.Join(" ", changes);
            else if (!created) Unchanged++;
            return t;
        }

        void ObjectAttr(Transform t, string key, string val, List<string> changes)
        {
            var go = t.gameObject;
            var v = Value(val);
            switch (key)
            {
                case "prefab": case "overrides": case "primitive": return;
                case "pos": case "position":
                    {
                        var p = Vec(v, 0);
                        if (t.localPosition == p) return;
                        Rec(t); t.localPosition = p; changes.Add($"pos={Fmt.V3(p)}"); return;
                    }
                case "rot": case "rotation":
                    {
                        var e = v is JArray ? Vec(v, 0) : new Vector3(0, 0, v.Value<float>());
                        if (Quaternion.Angle(t.localRotation, Quaternion.Euler(e)) < 0.01f) return;
                        Rec(t); t.localEulerAngles = e; changes.Add($"rot={val}"); return;
                    }
                case "scale":
                    {
                        var s = v is JArray ? Vec(v, 1) : Vector3.one * v.Value<float>();
                        if (t.localScale == s) return;
                        Rec(t); t.localScale = s; changes.Add($"scale={val}"); return;
                    }
                case "active":
                    {
                        var a = v.Value<bool>();
                        if (go.activeSelf == a) return;
                        Rec(go); go.SetActive(a); changes.Add($"active={a.ToString().ToLower()}"); return;
                    }
                case "static":
                    {
                        var a = v.Value<bool>();
                        if (go.isStatic == a) return;
                        Rec(go); go.isStatic = a; changes.Add($"static={val}"); return;
                    }
                case "tag":
                    {
                        var tag = v.ToString();
                        if (go.tag == tag) return;
                        if (!UnityEditorInternal.InternalEditorUtility.tags.Contains(tag))
                        {
                            Tags.AddTag(tag);
                            Warnings.Add($"created tag '{tag}'");
                        }
                        Rec(go); go.tag = tag; changes.Add($"tag={tag}"); return;
                    }
                case "layer":
                    {
                        var name = v.ToString();
                        int layer = v.Type == JTokenType.Float || v.Type == JTokenType.Integer ? v.Value<int>() : LayerMask.NameToLayer(name);
                        if (layer < 0) { layer = Tags.AddLayer(name); Warnings.Add($"created layer '{name}' (#{layer})"); }
                        if (go.layer == layer) return;
                        Rec(go); go.layer = layer; changes.Add($"layer={name}"); return;
                    }
                default:
                    throw Err($"unknown object attribute '{key}' (object attrs: pos rot scale active tag layer static prefab primitive; component values go on a '+Type' line below the object)");
            }
        }

        static Vector3 Vec(JToken v, float zFill)
        {
            var f = v is JArray a ? a.Select(x => x.Value<float>()).ToArray() : Props.Floats(v, 2);
            return new Vector3(f[0], f.Length > 1 ? f[1] : f[0], f.Length > 2 ? f[2] : zFill);
        }

        string PathOf(Transform t) => m_PrefabRoot != null && t.root == m_PrefabRoot.transform ? m_PrefabPath + ":" + ScenePath.Of(t) : ScenePath.Of(t);

        void Rec(Object o) { if (m_PrefabRoot == null) Undo.RecordObject(o, "Claude apply"); }

        void EnsureComponent(GameObject go, string typeName, List<(string key, string val)> values)
        {
            var type = Types.Object(typeName);
            if (type == null || !typeof(Component).IsAssignableFrom(type))
                throw Err($"unknown component type '{typeName}'{Suggest(typeName)}");
            var c = Refs.Comp(go, type);
            bool added = false;
            if (c == null)
            {
                c = m_PrefabRoot != null ? go.AddComponent(type) : Undo.AddComponent(go, type);
                if (c == null) throw Err($"could not add {typeName} to {PathOf(go.transform)} (conflicting component?)");
                added = true;
            }

            var changes = new List<string>();
            var so = new SerializedObject(c);
            foreach (var (key, raw) in values)
            {
                if (key == "sorting" && c is Renderer r)
                {
                    var parts = raw.Trim('"').Split('/');
                    var layerName = parts[0];
                    var order = parts.Length > 1 ? int.Parse(parts[1]) : r.sortingOrder;
                    if (r.sortingLayerName == layerName && r.sortingOrder == order) continue;
                    if (!SortingLayer.layers.Any(sl => sl.name == layerName)) { Tags.AddSortingLayer(layerName); Warnings.Add($"created sorting layer '{layerName}'"); }
                    so.ApplyModifiedProperties();
                    Rec(r);
                    r.sortingLayerName = layerName; r.sortingOrder = order;
                    so.Update();
                    changes.Add($"sorting={layerName}/{order}");
                    continue;
                }
                var v = Value(raw);
                if (v == null) { Warnings.Add($"line {m_LineNo}: skipped {typeName}.{key} (view-only placeholder '{raw}')"); continue; }
                var p = Props.Find(so, key)
                    ?? throw Err($"{typeName} has no property '{key}'{SuggestProp(so, key)}");
                var before = Fmt.Prop(p);
                Props.Assign(p, v);
                var after = Fmt.Prop(p);
                if (before != after) changes.Add($"{SceneTextWriter.PathKey(p.propertyPath)}={SceneTextWriter.Token(after)}");
            }
            if (m_PrefabRoot != null) so.ApplyModifiedPropertiesWithoutUndo(); else so.ApplyModifiedProperties();

            var where = PathOf(go.transform) + ":" + type.Name;
            if (added) Report.Add($"+ {where}{(changes.Count > 0 ? " " + string.Join(" ", changes) : "")}");
            else if (changes.Count > 0) Report.Add($"~ {where} {string.Join(" ", changes)}");
            else Unchanged++;
        }

        void RemoveComponent(GameObject go, string typeName)
        {
            var type = Types.Object(typeName) ?? throw Err($"unknown component type '{typeName}'");
            var c = Refs.Comp(go, type);
            if (c == null) { Unchanged++; return; }
            if (m_PrefabRoot != null) Object.DestroyImmediate(c, true); else Undo.DestroyObjectImmediate(c);
            Report.Add($"- {PathOf(go.transform)}:{type.Name}");
        }

        void Touch(GameObject go) { if (m_PrefabRoot == null && go.scene.IsValid()) TouchedScenes.Add(go.scene); }

        // ── directives ──────────────────────────────────────────────────────────────

        void Directive(string name, string arg)
        {
            m_Stack.Clear();
            switch (name)
            {
                case "root":
                    FlushPrefab(true);
                    m_Root = string.IsNullOrEmpty(arg) || arg == "/" ? null : (ScenePath.Find(arg) ?? throw Err(ScenePath.Explain(arg)));
                    return;
                case "prefab":
                    FlushPrefab(true);
                    m_Root = null;
                    m_PrefabPath = arg;
                    if (!arg.StartsWith("Assets/") || !arg.EndsWith(".prefab")) throw Err("@prefab needs an Assets/….prefab path");
                    m_PrefabIsNew = AssetDatabase.LoadAssetAtPath<GameObject>(arg) == null;
                    if (m_PrefabIsNew)
                    {
                        // Build new prefabs in a preview scene so the open scene is never touched.
                        m_Preview = EditorSceneManager.NewPreviewScene();
                        m_PrefabRoot = new GameObject(Path.GetFileNameWithoutExtension(arg));
                        SceneManager.MoveGameObjectToScene(m_PrefabRoot, m_Preview);
                    }
                    else m_PrefabRoot = PrefabUtility.LoadPrefabContents(arg);
                    m_PrefabRootBound = false;
                    m_ReportAtSection = Report.Count;
                    return;
                case "scene":
                    {
                        FlushPrefab(true);
                        m_Root = null;
                        var active = SceneManager.GetActiveScene();
                        if (active.path == arg) return;
                        foreach (var s in TouchedScenes.Where(s => s.isDirty).ToList()) EditorSceneManager.SaveScene(s);
                        if (active.isDirty && !TouchedScenes.Contains(active))
                            throw Err($"'{active.name}' has unsaved changes that aren't mine; save or discard them before @scene switches scenes");
                        if (File.Exists(Path.Combine(ProjectPaths.ProjectRoot, arg))) EditorSceneManager.OpenScene(arg, OpenSceneMode.Single);
                        else
                        {
                            Folders.Ensure(Path.GetDirectoryName(arg).Replace('\\', '/'));
                            m_PreviousScene = active.path;
                            var s = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
                            EditorSceneManager.SaveScene(s, arg);
                            m_CreatedScene = arg;
                            Report.Add($"+ scene {arg}");
                        }
                        Undo.IncrementCurrentGroup();
                        return;
                    }
                default:
                    throw Err($"unknown directive @{name} (use @root, @prefab, @scene)");
            }
        }

        readonly List<(string path, byte[] backup)> m_SavedPrefabs = new List<(string, byte[])>();

        /// <summary>Undo prefab files written by this apply (they're outside Unity's Undo).</summary>
        string m_CreatedScene, m_PreviousScene;

        public void RollbackAssets()
        {
            if (m_CreatedScene != null)
            {
                if (!string.IsNullOrEmpty(m_PreviousScene)) EditorSceneManager.OpenScene(m_PreviousScene, OpenSceneMode.Single);
                else EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
                AssetDatabase.DeleteAsset(m_CreatedScene);
                m_CreatedScene = null;
            }
            for (int i = m_SavedPrefabs.Count - 1; i >= 0; i--)
            {
                var (path, backup) = m_SavedPrefabs[i];
                if (backup == null) AssetDatabase.DeleteAsset(path);
                else
                {
                    File.WriteAllBytes(Path.Combine(ProjectPaths.ProjectRoot, path), backup);
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                }
            }
            m_SavedPrefabs.Clear();
            if (Folders.Created != null)
                for (int i = Folders.Created.Count - 1; i >= 0; i--)
                    AssetDatabase.DeleteAsset(Folders.Created[i]);
        }

        void FlushPrefab(bool save)
        {
            if (m_PrefabRoot == null) return;
            try
            {
                if (save)
                {
                    if (!m_PrefabRootBound) throw Err($"@prefab {m_PrefabPath} has no root object line");
                    if (!m_PrefabIsNew && Report.Count == m_ReportAtSection) return;
                    Folders.Ensure(Path.GetDirectoryName(m_PrefabPath).Replace('\\', '/'));
                    var full = Path.Combine(ProjectPaths.ProjectRoot, m_PrefabPath);
                    m_SavedPrefabs.Add((m_PrefabPath, File.Exists(full) ? File.ReadAllBytes(full) : null));
                    PrefabUtility.SaveAsPrefabAsset(m_PrefabRoot, m_PrefabPath, out var ok);
                    if (!ok) throw Err($"saving {m_PrefabPath} failed");
                    Report.Add($"saved {m_PrefabPath}");
                }
            }
            finally
            {
                if (m_PrefabIsNew) { Object.DestroyImmediate(m_PrefabRoot); EditorSceneManager.ClosePreviewScene(m_Preview); }
                else PrefabUtility.UnloadPrefabContents(m_PrefabRoot);
                m_PrefabRoot = null;
            }
        }

        // ── helpers ─────────────────────────────────────────────────────────────────

        static string Suggest(string typeName)
        {
            var all = TypeCache.GetTypesDerivedFrom<Component>().Select(t => t.Name).Distinct();
            var best = all.Select(n => (n, d: Distance(n.ToLowerInvariant(), typeName.ToLowerInvariant()))).Where(x => x.d <= 3).OrderBy(x => x.d).Take(3).Select(x => x.n).ToList();
            return best.Count > 0 ? $" — did you mean {string.Join(", ", best)}?" : "";
        }

        static string SuggestProp(SerializedObject so, string key) => Props.Suggest(so, key);

        static int Distance(string a, string b) => Props.Distance(a, b);
    }

    /// <summary>Tags / layers / sorting layers, created on demand.</summary>
    static class Tags
    {
        static SerializedObject TagManager() =>
            new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);

        public static void AddTag(string tag)
        {
            var so = TagManager();
            var tags = so.FindProperty("tags");
            tags.arraySize++;
            tags.GetArrayElementAtIndex(tags.arraySize - 1).stringValue = tag;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        public static int AddLayer(string name)
        {
            var so = TagManager();
            var layers = so.FindProperty("layers");
            for (int i = 8; i < layers.arraySize; i++)
            {
                var p = layers.GetArrayElementAtIndex(i);
                if (string.IsNullOrEmpty(p.stringValue))
                {
                    p.stringValue = name;
                    so.ApplyModifiedPropertiesWithoutUndo();
                    return i;
                }
            }
            throw new InvalidOperationException("no free user layer slots");
        }

        public static void AddSortingLayer(string name)
        {
            var so = TagManager();
            var layers = so.FindProperty("m_SortingLayers");
            layers.arraySize++;
            var e = layers.GetArrayElementAtIndex(layers.arraySize - 1);
            e.FindPropertyRelative("name").stringValue = name;
            e.FindPropertyRelative("uniqueID").uintValue = (uint)new System.Random().Next(1, int.MaxValue);
            so.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
