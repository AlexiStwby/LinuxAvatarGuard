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
                var key = "LinuxAvatarGuard.Welcome." + GuardText.Version + "." + Application.dataPath;
                if (EditorPrefs.GetBool(key))
                    return;
                EditorPrefs.SetBool(key, true);
                GuardWindow.Open();
            };
        }
    }
}
