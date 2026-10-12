using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.Tilemaps;
using Object = UnityEngine.Object;

namespace Claude.Bridge
{
    /// <summary>
    /// Renders hierarchy as "scene text", the same format SceneTextApplier reads:
    ///
    ///   Player pos=(1,2) tag=Player                      object line: name + object attributes
    ///     +SpriteRenderer sprite=Assets/a.png#a sorting=Default/5     component line (values = non-defaults)
    ///     +Rigidbody2D gravityScale=3
    ///     Weapon pos=(0.5,0) [SpriteRenderer]            child (compact mode lists components in brackets)
    ///
    /// Runs of ≥3 similar siblings collapse to one line ("Enemy0..Enemy49 ×50"). If the result is over the
    /// line budget, depth is reduced and cut subtrees become "… N descendants".
    /// </summary>
    class SceneTextWriter
    {
        public bool Values;
        public bool Expand;
        public int MaxLines = 150;

        static readonly HashSet<string> Noise = new HashSet<string>
        {
            "m_Script", "m_ObjectHideFlags", "m_CorrespondingSourceObject", "m_PrefabInstance", "m_PrefabAsset",
            "m_GameObject", "m_EditorHideFlags", "m_EditorClassIdentifier", "m_Enabled",
        };

        // Fields that differ from AddComponent defaults in templates but almost never matter.
        static readonly Dictionary<string, HashSet<string>> TypeNoise = new Dictionary<string, HashSet<string>>
        {
            ["Camera"] = new HashSet<string> { "field of view", "m_Depth", "m_TargetEye", "m_AllowMSAA", "m_OcclusionCulling", "m_projectionMatrixMode", "m_GateFitMode", "m_FOVAxisMode", "m_SensorSize", "m_LensShift", "m_FocalLength", "m_Iso", "m_ShutterSpeed", "m_Aperture", "m_FocusDistance", "m_BladeCount", "m_Curvature", "m_BarrelClipping", "m_Anamorphism" },
            ["Light2D"] = new HashSet<string> { "m_LocalBounds", "m_ApplyToSortingLayers", "m_ShadowSoftness", "m_ShadowsEnabled" },
            ["CompositeCollider2D"] = new HashSet<string> { "m_ColliderPaths", "m_CompositePaths" },
            ["RectTransform"] = new HashSet<string> { "m_LocalRotation", "m_LocalPosition", "m_LocalScale", "m_ConstrainProportionsScale" },
            ["CanvasScaler"] = new HashSet<string> { "m_PresetInfoIsWorld" },
        };

        static readonly HashSet<Type> HiddenInCompact = new HashSet<Type> { typeof(Transform), typeof(RectTransform), typeof(CanvasRenderer) };

        StringBuilder m_Sb;
        int m_DepthLimit;

        public string Render(string header, IList<Transform> roots, int maxDepth)
        {
            // Shrink depth until it fits the budget (always show at least the roots).
            int height = roots.Count == 0 ? 0 : roots.Max(Height);
            int depth = maxDepth >= 0 ? Math.Min(maxDepth, height) : height;
            string text;
            while (true)
            {
                m_Sb = new StringBuilder();
                m_DepthLimit = depth;
                if (header != null) m_Sb.Append("# ").AppendLine(header);
                Siblings(roots, 0);
                text = m_Sb.ToString();
                int lines = text.Count(c => c == '\n');
                if (lines <= MaxLines || depth == 0) break;
                depth--;
            }
            int total = text.Count(c => c == '\n');
            if (total > MaxLines)
            {
                var cut = string.Join("\n", text.Split('\n').Take(MaxLines));
                text = cut + $"\n… {total - MaxLines} more lines (narrow with root=, or raise max_lines=)\n";
            }
            else if (depth < height && (maxDepth < 0 || depth < maxDepth))
                text += $"# depth limited to {depth} to fit {MaxLines} lines; drill in with root=\n";
            return text;
        }

        static int Height(Transform t) => t.childCount == 0 ? 0 : 1 + ScenePath.Children(t).Max(Height);
        static int Count(Transform t) => t.childCount + ScenePath.Children(t).Sum(Count);

        void Siblings(IList<Transform> list, int level)
        {
            for (int i = 0; i < list.Count;)
            {
                int run = 1;
                if (!Expand)
                {
                    var sig = Signature(list[i]);
                    while (i + run < list.Count && Signature(list[i + run]) == sig) run++;
                }
                if (run >= 3) { Collapsed(list, i, run, level); i += run; }
                else { Node(list[i], level); i++; }
            }
        }

        static readonly Regex Trailing = new Regex(@"[\s_\-]*(\(\d+\)|\d+)$");

        static string Stem(string name) => Trailing.Replace(name, "");

        static string Signature(Transform t)
        {
            var comps = string.Join(",", t.GetComponents<Component>().Select(c => c == null ? "?" : c.GetType().Name));
            var prefab = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(t.gameObject);
            return $"{Stem(t.name)}|{comps}|{t.childCount}|{prefab}";
        }

        void Collapsed(IList<Transform> list, int start, int run, int level)
        {
            var first = list[start];
            var last = list[start + run - 1];
            var ind = new string(' ', level * 2);
            m_Sb.Append(ind).Append(first.name == last.name ? first.name : $"{first.name}..{last.name}").Append(" ×").Append(run);
            var ps = Enumerable.Range(start, run).Select(i => list[i].localPosition).ToList();
            var min = new Vector3(ps.Min(p => p.x), ps.Min(p => p.y), ps.Min(p => p.z));
            var max = new Vector3(ps.Max(p => p.x), ps.Max(p => p.y), ps.Max(p => p.z));
            if (min != max) m_Sb.Append(" pos=").Append(Fmt.V3(min)).Append("..").Append(Fmt.V3(max));
            else if (min != Vector3.zero) m_Sb.Append(" pos=").Append(Fmt.V3(min));
            var prefab = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(first.gameObject);
            if (PrefabUtility.IsAnyPrefabInstanceRoot(first.gameObject)) m_Sb.Append(" prefab=").Append(prefab);
            if (!Values) AppendCompactComponents(first.gameObject);
            if (first.childCount > 0) m_Sb.Append($" (each {first.childCount} children)");
            m_Sb.AppendLine();
            if (Values)
            {
                m_Sb.Append(ind).AppendLine($"  # values of {first.name}:");
                Components(first.gameObject, level + 1);
            }
        }

        void Node(Transform t, int level)
        {
            var go = t.gameObject;
            var ind = new string(' ', level * 2);
            m_Sb.Append(ind).Append(NameToken(go.name));
            ObjectAttrs(t);
            if (!Values) AppendCompactComponents(go);
            if (level >= m_DepthLimit && t.childCount > 0)
            {
                var n = Count(t);
                m_Sb.Append($" … {n} descendant{(n == 1 ? "" : "s")}");
            }
            m_Sb.AppendLine();
            if (Values) Components(go, level + 1);
            if (level < m_DepthLimit) Siblings(ScenePath.Children(t).ToList(), level + 1);
        }

        public static string NameToken(string name)
        {
            bool needsQuote = name.Length == 0 || name.Contains("=") || name.Contains("\"") || "+-!@#[".IndexOf(name[0]) >= 0
                || name.Contains(" ×") || name != name.Trim();
            return needsQuote ? "\"" + name.Replace("\"", "\\\"") + "\"" : name;
        }

        void ObjectAttrs(Transform t)
        {
            var go = t.gameObject;
            if (!(t is RectTransform))
            {
                if (t.localPosition != Vector3.zero) m_Sb.Append(" pos=").Append(Fmt.V3(t.localPosition));
                var e = t.localEulerAngles;
                if (e != Vector3.zero) m_Sb.Append(" rot=").Append(e.x == 0 && e.y == 0 ? Fmt.F(e.z) : Fmt.V3Full(e));
                var s = t.localScale;
                if (s != Vector3.one) m_Sb.Append(" scale=").Append(s.x == s.y && s.y == s.z ? Fmt.F(s.x) : Fmt.V3(s));
            }
            if (!go.activeSelf) m_Sb.Append(" active=false");
            if (go.tag != "Untagged") m_Sb.Append(" tag=").Append(go.tag);
            if (go.layer != 0) m_Sb.Append(" layer=").Append(LayerMask.LayerToName(go.layer));
            if (go.isStatic) m_Sb.Append(" static=true");
            if (PrefabUtility.IsAnyPrefabInstanceRoot(go))
            {
                m_Sb.Append(" prefab=").Append(PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go));
                var mods = PrefabUtility.GetObjectOverrides(go).Count(o => !(o.instanceObject is Transform))
                    + PrefabUtility.GetAddedComponents(go).Count + PrefabUtility.GetAddedGameObjects(go).Count;
                if (mods > 0) m_Sb.Append(" overrides=").Append(mods);
            }
        }

        void AppendCompactComponents(GameObject go)
        {
            var names = new List<string>();
            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null) { names.Add("<missing script>"); continue; }
                if (HiddenInCompact.Contains(c.GetType())) continue;
                var n = c.GetType().Name;
                if (!Enabled(c)) n += "(off)";
                if (c is Tilemap tm) n += "{" + TilemapCommands.Summary(tm) + "}";
                names.Add(n);
            }
            if (names.Count > 0) m_Sb.Append(" [").Append(string.Join(" ", names)).Append(']');
        }

        static bool Enabled(Component c) =>
            c is Behaviour b ? b.enabled : c is Renderer r ? r.enabled : !(c is Collider2D col) || col.enabled;

        void Components(GameObject go, int level)
        {
            var ind = new string(' ', level * 2);
            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null) { m_Sb.Append(ind).AppendLine("# <missing script>"); continue; }
                if (c is Transform && !(c is RectTransform)) continue;
                // A root canvas drives its own RectTransform; those values are noise.
                if (c is RectTransform && go.TryGetComponent<Canvas>(out var cv) && cv.isRootCanvas) continue;
                var line = ComponentLine(c);
                if (HiddenInCompact.Contains(c.GetType()) && line == "+" + c.GetType().Name) continue;
                m_Sb.Append(ind).AppendLine(line);
            }
        }

        public static string ComponentLine(Component c)
        {
            var sb = new StringBuilder("+").Append(c.GetType().Name);
            if (!Enabled(c)) sb.Append(" enabled=false");
            if (c is Tilemap tm) { sb.Append(" # ").Append(TilemapCommands.Summary(tm)); return sb.ToString(); }
            foreach (var (key, val) in NonDefaults(c))
            {
                sb.Append(' ').Append(key).Append('=').Append(val);
                if (sb.Length > 400) { sb.Append(" …"); break; }
            }
            if (c is Renderer r && (r.sortingLayerID != 0 || r.sortingOrder != 0))
                sb.Append(" sorting=").Append(r.sortingLayerName).Append('/').Append(r.sortingOrder);
            return sb.ToString();
        }

        /// <summary>Top-level serialized fields whose value differs from a fresh component's (user scripts: all fields).</summary>
        public static IEnumerable<(string key, string value)> NonDefaults(Component c)
        {
            var type = c.GetType();
            var user = c is MonoBehaviour && Defaults.IsUserType(type);
            var defaults = user ? null : Defaults.Of(type);
            TypeNoise.TryGetValue(type.Name, out var noise);
            var it = new SerializedObject(c).GetIterator();
            for (bool enter = true; it.NextVisible(enter); enter = false)
            {
                if (Noise.Contains(it.name) || (noise != null && noise.Contains(it.name))) continue;
                if (it.propertyType == SerializedPropertyType.ManagedReference) continue;
                var v = Fmt.Prop(it);
                if (defaults != null && defaults.TryGetValue(it.propertyPath, out var dv) && dv == v) continue;
                // A changed struct: list only the fields that changed ("shadows.type=Soft"), not the whole struct.
                if (defaults != null && it.propertyType == SerializedPropertyType.Generic && !it.isArray && it.hasVisibleChildren)
                {
                    foreach (var kv in ChangedFields(it, defaults)) yield return kv;
                    continue;
                }
                yield return (Key(it.name), Token(v));
            }
        }

        static IEnumerable<(string key, string value)> ChangedFields(SerializedProperty parent, Dictionary<string, string> defaults)
        {
            var it = parent.Copy();
            var end = parent.GetEndProperty();
            bool enter = true;
            while (it.NextVisible(enter) && !SerializedProperty.EqualContents(it, end))
            {
                var v = Fmt.Prop(it);
                var same = defaults.TryGetValue(it.propertyPath, out var dv) && dv == v;
                enter = !same && it.propertyType == SerializedPropertyType.Generic && !it.isArray && it.hasVisibleChildren;
                if (same || enter) continue;
                yield return (PathKey(it.propertyPath), Token(v));
            }
        }

        public static string Key(string name) => Fmt.Short(name).Replace(' ', '_');

        /// <summary>m_Shadows.m_Type → shadows.type; items.Array.data[2] → items[2].</summary>
        public static string PathKey(string propertyPath) =>
            string.Join(".", System.Text.RegularExpressions.Regex.Replace(propertyPath, @"\.Array\.data\[(\d+)\]", "[$1]").Split('.').Select(Key));

        /// <summary>Make a formatted value a single token: quote anything with spaces that isn't already grouped.</summary>
        public static string Token(string v)
        {
            if (v.Length == 0) return "\"\"";
            if (!v.Contains(' ')) return v;
            char c = v[0];
            if (c == '"' || c == '[' || c == '{' || v.EndsWith(")")) return v;
            return "\"" + v.Replace("\"", "\\\"") + "\"";
        }
    }

    /// <summary>Cached serialized values of freshly added components, for "only show what changed".</summary>
    static class Defaults
    {
        static readonly Dictionary<Type, Dictionary<string, string>> s_Top = new Dictionary<Type, Dictionary<string, string>>();

        public static bool IsUserType(Type t) =>
            !(t.Namespace ?? "").StartsWith("Unity") && !t.Assembly.GetName().Name.StartsWith("Unity") && !t.Assembly.GetName().Name.StartsWith("Sirenix");

        public static Dictionary<string, string> Of(Type type)
        {
            if (s_Top.TryGetValue(type, out var d)) return d;
            d = new Dictionary<string, string>();
            WithInstance(type, so =>
            {
                // Every visible property by path (top level and struct fields; array contents skipped).
                var it = so.GetIterator();
                for (bool enter = true; it.NextVisible(enter);)
                {
                    d[it.propertyPath] = Fmt.Prop(it);
                    enter = it.propertyType == SerializedPropertyType.Generic && !it.isArray && it.hasVisibleChildren && it.depth < 4;
                }
            });
            return s_Top[type] = d;
        }

        /// <summary>Run <paramref name="body"/> against a throwaway default instance of a component or ScriptableObject type.</summary>
        public static void WithInstance(Type type, Action<SerializedObject> body)
        {
            if (typeof(ScriptableObject).IsAssignableFrom(type))
            {
                ScriptableObject so = null;
                try { so = ScriptableObject.CreateInstance(type); if (so != null) body(new SerializedObject(so)); }
                catch { }
                finally { if (so != null) Object.DestroyImmediate(so); }
                return;
            }
            if (!typeof(Component).IsAssignableFrom(type)) return;
            var go = new GameObject("__claude_defaults") { hideFlags = HideFlags.HideAndDontSave };
            go.SetActive(false);
            try
            {
                var c = Refs.Comp(go, type) ?? go.AddComponent(type);
                if (c != null) body(new SerializedObject(c));
            }
            catch { /* abstract or unconstructible: compare against nothing */ }
            finally { Object.DestroyImmediate(go); }
        }
    }
}
