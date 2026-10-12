using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Claude.Bridge
{
    /// <summary>
    /// Hierarchy paths with disambiguation: "/Level/Crate#3" is the third sibling named Crate.
    /// Resolution looks in the open prefab stage first (if any), then loaded scenes, active scene first.
    /// </summary>
    static class ScenePath
    {
        public static string Of(Transform t)
        {
            var parts = new List<string>();
            for (; t != null; t = t.parent) parts.Add(Segment(t));
            parts.Reverse();
            return "/" + string.Join("/", parts);
        }

        /// <summary>"Name", or "Name#n" when it's the n-th (n≥2) sibling with that name.</summary>
        public static string Segment(Transform t)
        {
            var siblings = t.parent != null ? Children(t.parent) : t.gameObject.scene.GetRootGameObjects().Select(g => g.transform);
            int n = 0;
            foreach (var s in siblings)
            {
                if (s.name == t.name) n++;
                if (s == t) break;
            }
            return n >= 2 ? $"{t.name}#{n}" : t.name;
        }

        public static IEnumerable<Transform> Children(Transform t)
        {
            for (int i = 0; i < t.childCount; i++) yield return t.GetChild(i);
        }

        public static IEnumerable<Transform> Roots()
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null) return new[] { stage.prefabContentsRoot.transform };
            var active = SceneManager.GetActiveScene();
            return Enumerable.Range(0, SceneManager.sceneCount).Select(SceneManager.GetSceneAt)
                .Where(s => s.isLoaded).OrderBy(s => s == active ? 0 : 1)
                .SelectMany(s => s.GetRootGameObjects()).Select(g => g.transform);
        }

        /// <summary>Pick the child named by a segment ("Name" or "Name#n") from candidates.</summary>
        public static Transform Pick(IEnumerable<Transform> candidates, string segment)
        {
            var (name, index) = Split(segment);
            return candidates.Where(c => c.name == name).Skip(index - 1).FirstOrDefault();
        }

        public static (string name, int index) Split(string segment)
        {
            var hash = segment.LastIndexOf('#');
            if (hash > 0 && int.TryParse(segment.Substring(hash + 1), out var n) && n >= 1)
                return (segment.Substring(0, hash), n);
            return (segment, 1);
        }

        /// <summary>Resolve "/A/B#2/C"; null when not found.</summary>
        public static Transform Find(string path)
        {
            var segs = path.Trim('/').Split('/');
            if (segs.Length == 0 || segs[0].Length == 0) return null;
            var cur = Pick(Roots(), segs[0]);
            for (int i = 1; i < segs.Length && cur != null; i++)
                cur = Pick(Children(cur), segs[i]);
            return cur;
        }

        /// <summary>Error text for a path that didn't resolve: names the deepest part that did.</summary>
        public static string Explain(string path)
        {
            var segs = path.Trim('/').Split('/');
            var cur = Pick(Roots(), segs[0]);
            if (cur == null)
                return $"No root object '{segs[0]}'. Roots: {string.Join(", ", Roots().Select(r => r.name).Distinct().Take(15))}";
            for (int i = 1; i < segs.Length; i++)
            {
                var next = Pick(Children(cur), segs[i]);
                if (next == null)
                    return $"{Of(cur)} has no child '{segs[i]}'. Children: {string.Join(", ", Children(cur).Select(c => c.name).Distinct().Take(15))}";
                cur = next;
            }
            return null;
        }
    }
}
