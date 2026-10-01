// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Linq;
using UnityEditor;
namespace LinuxAvatarGuard
{
    public static class GuardExport
    {
        [MenuItem("Tools/Linux Avatar Guard/Exportar herramienta para distribuir")]
        static void Open()
        {
            var path=EditorUtility.SaveFilePanel(GuardText.Text("Exportar sólo Linux Avatar Guard"), "", "LinuxAvatarGuard-"+GuardText.Version, "unitypackage");
            if (!string.IsNullOrEmpty(path)) Export(path);
        }
        public static void Export(string destination)
        {
            var script=AssetDatabase.FindAssets("GuardExport t:MonoScript").Select(AssetDatabase.GUIDToAssetPath).First(p=>p.EndsWith("/GuardExport.cs",StringComparison.Ordinal));
            var root=Path.GetDirectoryName(Path.GetDirectoryName(script)).Replace('\\','/');
            var files=AssetDatabase.GetAllAssetPaths().Where(p=>p.StartsWith(root+"/",StringComparison.Ordinal)&&File.Exists(p)&&!p.Contains("/__pycache__/")&&!p.EndsWith(".pyc",StringComparison.Ordinal)).ToArray();
            if(files.Length==0)throw new InvalidOperationException("No se encontraron los archivos de la herramienta.");
            AssetDatabase.ExportPackage(files,destination,ExportPackageOptions.Default);
        }
    }
}
