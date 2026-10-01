// SPDX-License-Identifier: MIT
using UnityEditor;
using UnityEngine;
namespace LinuxAvatarGuard
{
    [InitializeOnLoad]
    static class GuardWelcome
    {
        static GuardWelcome()
        {
            EditorApplication.delayCall += () =>
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
                    return;
                var key = "LinuxAvatarGuard.Welcome.0.2.0." + Application.dataPath;
                if (EditorPrefs.GetBool(key))
                    return;
                EditorPrefs.SetBool(key, true);
                GuardWindow.Open();
            };
        }
    }
}
