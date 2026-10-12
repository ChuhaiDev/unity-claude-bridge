using System.Collections.Generic;
using System.IO;
using UnityEditor;

namespace Claude.Bridge
{
    static class Folders
    {
        /// <summary>Folders created by Ensure (so a failed apply can remove them again). Null = not tracking.</summary>
        public static List<string> Created;

        public static void Ensure(string folder)
        {
            if (string.IsNullOrEmpty(folder) || AssetDatabase.IsValidFolder(folder)) return;
            var parent = Path.GetDirectoryName(folder).Replace('\\', '/');
            Ensure(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
            Created?.Add(folder);
        }
    }
}
