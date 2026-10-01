// SPDX-License-Identifier: MIT
using System;
using UnityEditor;
using UnityEngine;
namespace LinuxAvatarGuard
{
    public sealed class GuardWindow : EditorWindow
    {
        GameObject avatar;
        float strength = .1f;
        bool advanced;
        string message = "", manualId = "";
        Vector2 scroll;
#if LAG_VRCSDK
        GuardProfile profile;
#endif
        [MenuItem("Tools/Linux Avatar Guard/Asistente")]
        public static void Open() => GetWindow<GuardWindow>("Linux Avatar Guard");
        [MenuItem("Tools/Linux Avatar Guard/Crear copia protegida")]
        static void LegacyOpen() => Open();
        void OnEnable()
        {
            minSize = new Vector2(470, 560);
            avatar = Selection.activeGameObject;
#if LAG_VRCSDK
            var all = GuardProfiles.All();
            if (all.Count > 0)
                profile = all[all.Count - 1];
#endif
        }
        void OnInspectorUpdate() => Repaint();
        void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.LabelField("Linux Avatar Guard 0.2.0", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Preparar · Subir · Activar OSC", EditorStyles.miniLabel);
            EditorGUILayout.HelpBox("Ofuscación experimental de geometría. Conserva el avatar original. " +
                                    "Requiere Linux, Vulkan, " +
                                        "lilToon y avatar PC. No protege texturas ni impide capturas GPU.",
                                    MessageType.Info);
#if LAG_VRCSDK && UNITY_EDITOR_LINUX
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("1. Preparar una copia", EditorStyles.boldLabel);
            avatar = (GameObject)EditorGUILayout.ObjectField("Avatar de trabajo", avatar, typeof(GameObject),
                                                             true);
            if (GUILayout.Button("Usar avatar seleccionado"))
                avatar = GuardSetup.AvatarRoot(Selection.activeGameObject);
            if (GuardProfiles.BuildId(GuardSetup.AvatarRoot(avatar)) != null &&
                GUILayout.Button("Reconocer copia protegida existente"))
                Try(() =>
                    {
                        var imported = GuardSetup.ImportExisting(avatar);
                        if (imported != null)
                            profile = imported;
                    });
            if (avatar && avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length != 0)
                EditorGUILayout.HelpBox("Avatar con skinning: comprueba gestos, visemas y PhysBones en la " +
                                        "copia. MA/AAO/VRCFury " +
                                            "requieren una copia final horneada antes de protegerla.",
                                        MessageType.Info);
            advanced = EditorGUILayout.Foldout(advanced, "Ajustes avanzados");
            if (advanced)
                strength = EditorGUILayout.Slider("Desplazamiento", strength, .01f, .5f);
            using (new EditorGUI.DisabledScope(
                !avatar || Application.isPlaying ||
                EditorApplication.isCompiling)) if (GUILayout.Button("Comprobar y preparar avatar",
                                                                     GUILayout.Height(32)))
                Try(() =>
                    {
                        profile = GuardSetup.Prepare(avatar, strength);
                        message =
                            "Copia y escena listas. La clave tiene un respaldo privado fuera del proyecto.";
                    });
            var profiles = GuardProfiles.All();
            if (profiles.Count > 0)
            {
                int index = profiles.FindIndex(p => profile != null && p.buildId == profile.buildId);
                if (index < 0)
                    index = profiles.Count - 1;
                var names = profiles.ConvertAll(p => p.name + " · " + p.buildId.Substring(0, 8)).ToArray();
                profile = profiles[EditorGUILayout.Popup("Copia preparada", index, names)];
            }
            if (profile != null)
            {
                if (!System.IO.File.Exists(profile.keyPath))
                {
                    EditorGUILayout.HelpBox(
                        "No se encuentra el respaldo privado. Selecciona la copia protegida y usa " +
                        "Reconocer copia protegida existente para recuperar tu archivo de claves.",
                        MessageType.Error);
                    EditorGUILayout.EndScrollView();
                    return;
                }
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("2. Revisar y subir desde el SDK", EditorStyles.boldLabel);
                if (GUILayout.Button("Abrir escena para subir"))
                    Try(() => GuardSetup.OpenScene(profile));
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Ver desbloqueado"))
                    Try(() => GuardSetup.Preview(profile, true));
                if (GUILayout.Button("Ver bloqueado"))
                    Try(() => GuardSetup.Preview(profile, false));
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.LabelField(Application.isPlaying
                                               ? "En Play Mode usa los parámetros de Gesture Manager."
                                               : "La vista previa no guarda claves en el prefab. Prueba " +
                                                 "Play Mode con Gesture Manager.",
                                           EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.HelpBox(
                    "Publica con el SDK oficial. Si el avatar ya tiene Blueprint ID, la copia lo conserva: " +
                        "compruébalo antes de publicar. El asistente no publica por ti.",
                    MessageType.Info);
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("3. Activar OSC en VRChat", EditorStyles.boldLabel);
                var key = GuardProfiles.Key(profile);
                var detected = GuardSetup.PublishedId(profile);
                if (GuardProfiles.ValidAvatarId(detected) && key.avatarId != detected)
                {
                    EditorGUILayout.LabelField("ID detectado", detected);
                    if (GUILayout.Button("Vincular avatar publicado"))
                        Try(() =>
                            {
                                GuardProfiles.Link(profile, detected);
                                message = "Avatar vinculado. Activa OSC en VRChat e inicia el desbloqueo.";
                            });
                }
                if (!GuardProfiles.ValidAvatarId(key.avatarId))
                {
                    EditorGUILayout.HelpBox(
                        "Después de subir vuelve aquí y vincula el ID detectado. No hace falta editar JSON.",
                        MessageType.Info);
                    manualId = EditorGUILayout.TextField("ID manual (opcional)", manualId);
                    using (new EditorGUI.DisabledScope(
                        !GuardProfiles.ValidAvatarId(manualId))) if (GUILayout.Button("Vincular ID manual"))
                        Try(() => GuardProfiles.Link(profile, manualId));
                }
                else
                {
                    EditorGUILayout.LabelField("Avatar vinculado", key.avatarId);
                    var runtimeStatus = GuardProfiles.Status(profile);
                    if (!GuardProfiles.Running(profile) && runtimeStatus?.state == "error")
                        EditorGUILayout.HelpBox(runtimeStatus.message, MessageType.Error);
                    if (GuardProfiles.Running(profile))
                    {
                        EditorGUILayout.HelpBox(GuardProfiles.Status(profile)?.message ??
                                                    "Desbloqueador activo.",
                                                MessageType.Info);
                        if (GUILayout.Button("Detener desbloqueo"))
                            Try(() => GuardProfiles.Stop(profile));
                    }
                    else if (GUILayout.Button("Iniciar desbloqueo OSC", GUILayout.Height(32)))
                        Try(() =>
                            {
                                GuardProfiles.Start(profile);
                                message =
                                    "Desbloqueador iniciado. Activa OSC; si no detecta el avatar actual, " +
                                    "cambia a otro avatar y vuelve.";
                            });
                }
                if (GUILayout.Button("Abrir lanzador para usar sin Unity"))
                    Try(() =>
                        {
                            GuardProfiles.RefreshTools(profile);
                            EditorUtility.RevealInFinder(
                                System.IO.Path.Combine(profile.toolsPath, "Desbloquear.sh"));
                        });
                EditorGUILayout.LabelField("Respaldo privado", profile.keyPath,
                                           EditorStyles.wordWrappedMiniLabel);
            }
#else
            EditorGUILayout.HelpBox(
                "Instala VRChat SDK Avatars y abre el proyecto en Unity Editor nativo para Linux.",
                MessageType.Error);
#endif
            if (!string.IsNullOrEmpty(message))
                EditorGUILayout.HelpBox(message, MessageType.None);
            EditorGUILayout.EndScrollView();
        }
        void Try(Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                message = e.Message;
                Debug.LogWarning("Linux Avatar Guard: " + e.Message);
            }
        }
    }
}
