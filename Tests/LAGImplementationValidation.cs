// SPDX-License-Identifier: MIT
#if UNITY_EDITOR && LAG_VRCSDK
using UnityEditor.SceneManagement;

// Use only in a disposable validation project: the functional fixture recreates Assets/Fixture.
public static class LAGImplementationValidation
{
    public static void Run()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        LAGCodecValidation.Run();
        LAGValidation.Run();
        LAGSecurityBaseline.Run();
        LAGPreparationValidation.Run();
        LAGBackendValidation.Run();
    }
}
#endif
