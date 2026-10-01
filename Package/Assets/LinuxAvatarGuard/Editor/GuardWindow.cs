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
        static string T(string text) => GuardText.Text(text);
        void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Linux Avatar Guard " + GuardText.Version, EditorStyles.boldLabel);
            GUILayout.Label(T("Idioma"), GUILayout.Width(60));
            var language = (GuardLanguage)EditorGUILayout.Popup((int)GuardText.Language,
                new[] { "ES", "EN", "JP" }, GUILayout.Width(65));
            if (language != GuardText.Language)
            {
                GuardText.Language = language;
                Repaint();
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField(T("Preparar · Subir · Activar OSC"), EditorStyles.miniLabel);
            EditorGUILayout.HelpBox(T("Ofuscación experimental de geometría. Conserva el avatar original. Requiere Linux, Vulkan, lilToon y avatar PC. No protege texturas ni impide capturas GPU."),
                                    MessageType.Info);
#if LAG_VRCSDK && UNITY_EDITOR_LINUX
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(T("1. Preparar una copia"), EditorStyles.boldLabel);
            avatar = (GameObject)EditorGUILayout.ObjectField(T("Avatar de trabajo"), avatar, typeof(GameObject),
                                                             true);
            if (GUILayout.Button(T("Usar avatar seleccionado")))
                avatar = GuardSetup.AvatarRoot(Selection.activeGameObject);
            if (GuardProfiles.BuildId(GuardSetup.AvatarRoot(avatar)) != null &&
                GUILayout.Button(T("Reconocer copia protegida existente")))
                Try(() =>
                    {
                        var imported = GuardSetup.ImportExisting(avatar);
                        if (imported != null)
                            profile = imported;
                    });
            if (avatar && avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length != 0)
                EditorGUILayout.HelpBox(T("Avatar con skinning: comprueba gestos, visemas y PhysBones en la copia. MA/AAO/VRCFury requieren una copia final horneada antes de protegerla."),
                                        MessageType.Info);
            advanced = EditorGUILayout.Foldout(advanced, T("Ajustes avanzados"));
            if (advanced)
                strength = EditorGUILayout.Slider(T("Desplazamiento"), strength, .01f, .5f);
            using (new EditorGUI.DisabledScope(
                !avatar || Application.isPlaying ||
                EditorApplication.isCompiling)) if (GUILayout.Button(T("Comprobar y preparar avatar"),
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
                profile = profiles[EditorGUILayout.Popup(T("Copia preparada"), index, names)];
            }
            if (profile != null)
            {
                if (!System.IO.File.Exists(profile.keyPath))
                {
                    EditorGUILayout.HelpBox(
                        T("No se encuentra el respaldo privado. Selecciona la copia protegida y usa Reconocer copia protegida existente para recuperar tu archivo de claves."),
                        MessageType.Error);
                    EditorGUILayout.EndScrollView();
                    return;
                }
                EditorGUILayout.Space();
                EditorGUILayout.LabelField(T("2. Revisar y subir desde el SDK"), EditorStyles.boldLabel);
                if (GUILayout.Button(T("Abrir escena para subir")))
                    Try(() => GuardSetup.OpenScene(profile));
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button(T("Ver desbloqueado")))
                    Try(() => GuardSetup.Preview(profile, true));
                if (GUILayout.Button(T("Ver bloqueado")))
                    Try(() => GuardSetup.Preview(profile, false));
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.LabelField(T(Application.isPlaying
                                               ? "En Play Mode usa los parámetros de Gesture Manager."
                                               : "La vista previa no guarda claves en el prefab. Prueba " +
                                                 "Play Mode con Gesture Manager."),
                                           EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.HelpBox(
                    T("Publica con el SDK oficial. Si el avatar ya tiene Blueprint ID, la copia lo conserva: compruébalo antes de publicar. El asistente no publica por ti."),
                    MessageType.Info);
                EditorGUILayout.Space();
                EditorGUILayout.LabelField(T("3. Activar OSC en VRChat"), EditorStyles.boldLabel);
                var key = GuardProfiles.Key(profile);
                var detected = GuardSetup.PublishedId(profile);
                if (GuardProfiles.ValidAvatarId(detected) && key.avatarId != detected)
                {
                    EditorGUILayout.LabelField(T("ID detectado"), detected);
                    if (GUILayout.Button(T("Vincular avatar publicado")))
                        Try(() =>
                            {
                                GuardProfiles.Link(profile, detected);
                                message = "Avatar vinculado. Activa OSC en VRChat e inicia el desbloqueo.";
                            });
                }
                if (!GuardProfiles.ValidAvatarId(key.avatarId))
                {
                    EditorGUILayout.HelpBox(
                        T("Después de subir vuelve aquí y vincula el ID detectado. No hace falta editar JSON."),
                        MessageType.Info);
                    manualId = EditorGUILayout.TextField(T("ID manual (opcional)"), manualId);
                    using (new EditorGUI.DisabledScope(
                        !GuardProfiles.ValidAvatarId(manualId))) if (GUILayout.Button(T("Vincular ID manual")))
                        Try(() => GuardProfiles.Link(profile, manualId));
                }
                else
                {
                    EditorGUILayout.LabelField(T("Avatar vinculado"), key.avatarId);
                    var runtimeStatus = GuardProfiles.Status(profile);
                    if (!GuardProfiles.Running(profile) && runtimeStatus?.state == "error")
                        EditorGUILayout.HelpBox(T(runtimeStatus.message), MessageType.Error);
                    if (GuardProfiles.Running(profile))
                    {
                        EditorGUILayout.HelpBox(T(GuardProfiles.Status(profile)?.message ??
                                                    "Desbloqueador activo."),
                                                MessageType.Info);
                        if (GUILayout.Button(T("Detener desbloqueo")))
                            Try(() => GuardProfiles.Stop(profile));
                    }
                    else if (GUILayout.Button(T("Iniciar desbloqueo OSC"), GUILayout.Height(32)))
                        Try(() =>
                            {
                                GuardProfiles.Start(profile);
                                message =
                                    "Desbloqueador iniciado. Activa OSC; si no detecta el avatar actual, " +
                                    "cambia a otro avatar y vuelve.";
                            });
                }
                if (GUILayout.Button(T("Abrir lanzador para usar sin Unity")))
                    Try(() =>
                        {
                            GuardProfiles.RefreshTools(profile);
                            EditorUtility.RevealInFinder(
                                System.IO.Path.Combine(profile.toolsPath, "Desbloquear.sh"));
                        });
                EditorGUILayout.LabelField(T("Respaldo privado"), profile.keyPath,
                                           EditorStyles.wordWrappedMiniLabel);
            }
#else
            EditorGUILayout.HelpBox(
                T("Instala VRChat SDK Avatars y abre el proyecto en Unity Editor nativo para Linux."),
                MessageType.Error);
#endif
            if (!string.IsNullOrEmpty(message))
                EditorGUILayout.HelpBox(T(message), MessageType.None);
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
                Debug.LogWarning("Linux Avatar Guard: " + T(e.Message));
            }
        }
    }
}
