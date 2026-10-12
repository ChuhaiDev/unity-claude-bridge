using System;
using System.Linq;
using Unity.Pipeline.Editor.Authoring;
using Unity.Pipeline.Models;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Claude.Bridge
{
    /// <summary>Resolves the reference strings that <see cref="Fmt.Ref"/> produces, plus Pipeline handles.</summary>
    static class Refs
    {
        /// <summary>
        /// Resolve a handle. Accepts everything Fmt.Ref emits ("Assets/x.png#sub", "/A/B:Rigidbody2D")
        /// and anything the Pipeline's ObjectRef accepts (globalId, guid, instance id, hierarchy path).
        /// When <paramref name="want"/> is given, the result is coerced to it: a texture path yields its
        /// Sprite, a GameObject yields its component of that type.
        /// </summary>
        public static Object Resolve(string s, Type want = null)
        {
            if (string.IsNullOrWhiteSpace(s) || s == "null") return null;
            s = s.Trim();

            if (s.StartsWith("builtin:")) return Builtin(s.Substring(8), want ?? typeof(Object));

            string component = null;
            if (s.StartsWith("/") && s.LastIndexOf(':') > 0)
            {
                var i = s.LastIndexOf(':');
                component = s.Substring(i + 1);
                s = s.Substring(0, i);
            }

            Object obj;
            if (s.StartsWith("Assets/") || s.StartsWith("Packages/"))
            {
                string sub = null;
                var hash = s.IndexOf('#');
                if (hash > 0) { sub = s.Substring(hash + 1); s = s.Substring(0, hash); }
                var all = AssetDatabase.LoadAllAssetsAtPath(s);
                if (all == null || all.Length == 0) throw new ArgumentException($"No asset at '{s}'");
                var candidates = all.Where(o => o != null);
                if (sub != null) candidates = candidates.Where(o => o.name == sub);
                if (want != null && want != typeof(Object))
                {
                    var typed = candidates.Where(o => want.IsInstanceOfType(o)).ToArray();
                    // Prefab root GameObject → component of the wanted type.
                    if (typed.Length == 0 && typeof(Component).IsAssignableFrom(want))
                        typed = candidates.OfType<GameObject>().Select(g => (Object)g.GetComponent(want)).Where(c => c != null).ToArray();
                    if (typed.Length == 0) throw new ArgumentException($"'{s}' has no {want.Name}{(sub != null ? " named " + sub : "")}");
                    return typed.FirstOrDefault(o => AssetDatabase.IsMainAsset(o)) ?? typed[0];
                }
                obj = sub != null ? candidates.FirstOrDefault() : AssetDatabase.LoadMainAssetAtPath(s);
                if (obj == null) throw new ArgumentException($"No sub-asset '{sub}' in '{s}'");
                return obj;
            }

            if (s.StartsWith("/"))
            {
                var t = ScenePath.Find(s) ?? throw new ArgumentException(ScenePath.Explain(s));
                obj = t.gameObject;
            }
            else
            {
                var handle = Newtonsoft.Json.JsonConvert.DeserializeObject<ObjectRef>(Newtonsoft.Json.JsonConvert.SerializeObject(s));
                if (!ObjectResolver.TryResolve(handle, out obj, out var error))
                    throw new ArgumentException(error);
            }

            if (component != null)
            {
                var go = obj as GameObject ?? (obj as Component)?.gameObject;
                var t = Types.Component(component);
                obj = Refs.Comp(go, t) ?? throw new ArgumentException($"{Fmt.Ref(go)} has no {component}");
            }
            if (want != null && !want.IsInstanceOfType(obj))
            {
                var go = obj as GameObject ?? (obj as Component)?.gameObject;
                if (go != null && typeof(Component).IsAssignableFrom(want))
                    obj = Refs.Comp(go, want) ?? throw new ArgumentException($"{Fmt.Ref(go)} has no {want.Name}");
                else if (want == typeof(GameObject) && obj is Component c)
                    obj = c.gameObject;
                else
                    throw new ArgumentException($"{Fmt.Ref(obj)} is a {obj.GetType().Name}, expected {want.Name}");
            }
            return obj;
        }

        static readonly string[] BuiltinExt = { "", ".fbx", ".mat", ".png", ".shader", ".ttf" };

        /// <summary>Unity's built-in resources: meshes (Cube, Sphere…), default materials, UI sprites, fonts.</summary>
        static Object Builtin(string name, Type want)
        {
            var loaded = Resources.FindObjectsOfTypeAll(want).FirstOrDefault(o => o.name == name && EditorUtility.IsPersistent(o));
            if (loaded != null) return loaded;
            foreach (var ext in BuiltinExt)
            {
                Object o = null;
                try { o = Resources.GetBuiltinResource(want, name + ext); } catch { }
                if (o == null) try { o = AssetDatabase.GetBuiltinExtraResource(want, name + ext); } catch { }
                if (o != null) return o;
            }
            throw new ArgumentException($"No built-in {want.Name} named '{name}' (meshes: Cube Sphere Capsule Cylinder Plane Quad)");
        }

        public static Object Resolve(ObjectRef handle)
        {
            if (!ObjectResolver.TryResolve(handle, out var obj, out var error))
                throw new ArgumentException(error);
            return obj;
        }

        public static GameObject GameObject(string s) => (GameObject)Resolve(s, typeof(GameObject));

        /// <summary>GetComponent without Unity's editor "fake null" (which defeats ?? and ?.).</summary>
        public static Component Comp(GameObject go, Type t) => go.TryGetComponent(t, out var c) ? c : null;
    }

    static class Types
    {
        /// <summary>Find a UnityEngine.Object-derived type by short or full name.</summary>
        public static Type Object(string name)
        {
            var all = TypeCache.GetTypesDerivedFrom<Object>();
            return all.FirstOrDefault(t => t.FullName == name)
                ?? all.FirstOrDefault(t => t.Name == name)
                ?? all.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        public static Type Component(string name)
        {
            var t = Object(name);
            if (t == null || !typeof(Component).IsAssignableFrom(t))
                throw new ArgumentException($"Unknown component type '{name}'");
            return t;
        }

        /// <summary>Element type of an object-reference property, from its "PPtr&lt;$Sprite&gt;" type string.</summary>
        public static Type OfPPtr(SerializedProperty p)
        {
            var s = p.type;
            if (!s.StartsWith("PPtr<")) return typeof(Object);
            s = s.Substring(5).TrimEnd('>').TrimStart('$');
            return Object(s) ?? typeof(Object);
        }
    }

    static class Persist
    {
        /// <summary>Save a scene this command modified (not in play mode, not unsaved/untitled scenes).</summary>
        public static string Save(UnityEngine.SceneManagement.Scene scene)
        {
            if (EditorApplication.isPlaying || !scene.IsValid() || !scene.isDirty || string.IsNullOrEmpty(scene.path)) return null;
            if (UnityEditor.SceneManagement.EditorSceneManager.IsPreviewScene(scene)) return null;
            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);
            return scene.path;
        }
    }
}
