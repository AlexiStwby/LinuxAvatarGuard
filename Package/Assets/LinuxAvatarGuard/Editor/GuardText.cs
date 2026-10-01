// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using UnityEditor;

namespace LinuxAvatarGuard
{
    public enum GuardLanguage { ES, EN, JP }

    // Spanish source text is also the fallback. Preferences never enter avatar assets or keys.
    public static class GuardText
    {
        public const string Version = "0.2.1";
        public const string PreferenceKey = "LinuxAvatarGuard.Language";
        public static GuardLanguage Language
        {
            get
            {
                int value = EditorPrefs.GetInt(PreferenceKey, 0);
                return value >= 0 && value <= 2 ? (GuardLanguage)value : GuardLanguage.ES;
            }
            set
            {
                if (!Enum.IsDefined(typeof(GuardLanguage), value))
                    throw new ArgumentOutOfRangeException(nameof(value));
                EditorPrefs.SetInt(PreferenceKey, (int)value);
            }
        }

        public static string Text(string spanish)
        {
            int language = (int)Language;
            if (string.IsNullOrEmpty(spanish) || language == 0) return spanish;
            if (Translations.TryGetValue(spanish, out var translated)) return translated[language - 1];
            // Validation errors append asset names or paths. Translate only the known prefix.
            foreach (var entry in Translations)
                if (entry.Key.EndsWith(": ", StringComparison.Ordinal) &&
                    spanish.StartsWith(entry.Key, StringComparison.Ordinal))
                    return entry.Value[language - 1] + Text(spanish.Substring(entry.Key.Length));
            return spanish;
        }

        static readonly Dictionary<string, string[]> Translations = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            { "Idioma", new[] { "Language", "言語" } },
            { "Preparar · Subir · Activar OSC", new[] { "Prepare · Upload · Start OSC", "準備 · アップロード · OSCを開始" } },
            { "Ofuscación experimental de geometría. Conserva el avatar original. Requiere Linux, Vulkan, lilToon y avatar PC. No protege texturas ni impide capturas GPU.", new[] { "Experimental mesh obfuscation. Preserves the original avatar. Requires Linux, Vulkan, lilToon and a PC avatar. Does not protect textures or prevent GPU capture.", "実験的なメッシュ難読化です。元のアバターは保持されます。Linux、Vulkan、lilToon、PC用アバターが必要です。テクスチャの保護やGPUキャプチャの防止はできません。" } },
            { "1. Preparar una copia", new[] { "1. Prepare a copy", "1. コピーを準備" } },
            { "Avatar de trabajo", new[] { "Source avatar", "元のアバター" } },
            { "Usar avatar seleccionado", new[] { "Use selected avatar", "選択中のアバターを使用" } },
            { "Reconocer copia protegida existente", new[] { "Recognize an existing protected copy", "既存の保護済みコピーを認識" } },
            { "Avatar con skinning: comprueba gestos, visemas y PhysBones en la copia. MA/AAO/VRCFury requieren una copia final horneada antes de protegerla.", new[] { "Skinned avatar: check gestures, visemes and PhysBones on the copy. For MA/AAO/VRCFury, bake a final copy before protecting it.", "スキニングされたアバターです。コピーでジェスチャー、リップシンク、PhysBoneを確認してください。MA/AAO/VRCFuryは、処理を適用した最終コピーを作成してから保護してください。" } },
            { "Ajustes avanzados", new[] { "Advanced settings", "詳細設定" } },
            { "Desplazamiento", new[] { "Displacement", "変位量" } },
            { "Comprobar y preparar avatar", new[] { "Validate and prepare avatar", "アバターを検証して準備" } },
            { "Copia y escena listas. La clave tiene un respaldo privado fuera del proyecto.", new[] { "Copy and scene ready. A private key backup is stored outside the project.", "コピーとシーンを準備しました。鍵の非公開バックアップはプロジェクトの外に保存されています。" } },
            { "Copia preparada", new[] { "Prepared copy", "準備済みコピー" } },
            { "No se encuentra el respaldo privado. Selecciona la copia protegida y usa Reconocer copia protegida existente para recuperar tu archivo de claves.", new[] { "Private backup not found. Select the protected copy and use Recognize an existing protected copy to recover your key file.", "非公開バックアップが見つかりません。保護済みコピーを選択し、「既存の保護済みコピーを認識」で鍵ファイルを復元してください。" } },
            { "2. Revisar y subir desde el SDK", new[] { "2. Review and upload with the SDK", "2. 確認してSDKでアップロード" } },
            { "Abrir escena para subir", new[] { "Open upload scene", "アップロード用シーンを開く" } },
            { "Ver desbloqueado", new[] { "Preview unlocked", "解除状態をプレビュー" } },
            { "Ver bloqueado", new[] { "Preview locked", "ロック状態をプレビュー" } },
            { "En Play Mode usa los parámetros de Gesture Manager.", new[] { "In Play Mode, preview uses Gesture Manager parameters.", "再生モードではGesture Managerのパラメーターを使用します。" } },
            { "La vista previa no guarda claves en el prefab. Prueba Play Mode con Gesture Manager.", new[] { "Preview does not save keys in the prefab. Test Play Mode with Gesture Manager.", "プレビューはPrefabに鍵を保存しません。Gesture Managerの再生モードでテストしてください。" } },
            { "Publica con el SDK oficial. Si el avatar ya tiene Blueprint ID, la copia lo conserva: compruébalo antes de publicar. El asistente no publica por ti.", new[] { "Upload with the official SDK. The copy preserves an existing Blueprint ID: check it before uploading. The wizard does not upload for you.", "公式SDKでアップロードしてください。既存のBlueprint IDはコピーに引き継がれるため、アップロード前に確認してください。このアシスタントは自動でアップロードしません。" } },
            { "3. Activar OSC en VRChat", new[] { "3. Enable OSC in VRChat", "3. VRChatでOSCを有効化" } },
            { "ID detectado", new[] { "Detected ID", "検出したID" } },
            { "Vincular avatar publicado", new[] { "Link uploaded avatar", "アップロード済みアバターを関連付け" } },
            { "Avatar vinculado. Activa OSC en VRChat e inicia el desbloqueo.", new[] { "Avatar linked. Enable OSC in VRChat and start unlocking.", "アバターを関連付けました。VRChatでOSCを有効にし、ロック解除を開始してください。" } },
            { "Después de subir vuelve aquí y vincula el ID detectado. No hace falta editar JSON.", new[] { "After uploading, return here and link the detected ID. No JSON editing is needed.", "アップロード後にここに戻り、検出されたIDを関連付けてください。JSONの編集は不要です。" } },
            { "ID manual (opcional)", new[] { "Manual ID (optional)", "IDを手入力（任意）" } },
            { "Vincular ID manual", new[] { "Link manual ID", "入力したIDを関連付け" } },
            { "Avatar vinculado", new[] { "Linked avatar", "関連付け済みアバター" } },
            { "Desbloqueador activo.", new[] { "Unlocker running.", "ロック解除ツールが動作中です。" } },
            { "Detener desbloqueo", new[] { "Stop unlocking", "ロック解除を停止" } },
            { "Iniciar desbloqueo OSC", new[] { "Start OSC unlocking", "OSCロック解除を開始" } },
            { "Desbloqueador iniciado. Activa OSC; si no detecta el avatar actual, cambia a otro avatar y vuelve.", new[] { "Unlocker started. Enable OSC; if the current avatar is not detected, switch to another avatar and back.", "ロック解除ツールを開始しました。OSCを有効にしてください。現在のアバターが検出されない場合は、別のアバターに切り替えてから戻してください。" } },
            { "Abrir lanzador para usar sin Unity", new[] { "Open launcher to use without Unity", "Unityなしで使うランチャーを開く" } },
            { "Respaldo privado", new[] { "Private backup", "非公開バックアップ" } },
            { "Instala VRChat SDK Avatars y abre el proyecto en Unity Editor nativo para Linux.", new[] { "Install VRChat SDK Avatars and open the project in native Linux Unity Editor.", "VRChat SDK Avatarsをインストールし、Linuxネイティブ版Unity Editorでプロジェクトを開いてください。" } },
            { "Seleccionar respaldo privado de esta copia", new[] { "Select this copy's private backup", "このコピーの非公開バックアップを選択" } },
            { "Exportar sólo Linux Avatar Guard", new[] { "Export Linux Avatar Guard only", "Linux Avatar Guardのみをエクスポート" } },
            { "Material generado. Edita lilToon en el avatar original y vuelve a generar la copia. Las claves se aplican mediante OSC.", new[] { "Generated material. Edit lilToon on the original avatar and regenerate the copy. Keys are applied through OSC.", "生成されたマテリアルです。元のアバターでlilToonを編集し、コピーを再生成してください。鍵はOSCで適用されます。" } },
            { "Esperando el avatar configurado. Activa OSC en VRChat.", new[] { "Waiting for the configured avatar. Enable OSC in VRChat.", "設定したアバターを待機中です。VRChatでOSCを有効にしてください。" } },
            { "Otro avatar activo. Esperando el avatar configurado.", new[] { "Another avatar is active. Waiting for the configured avatar.", "別のアバターを使用中です。設定したアバターを待機しています。" } },
            { "Avatar configurado detectado.", new[] { "Configured avatar detected.", "設定したアバターを検出しました。" } },
            { "VRChat confirma las cuatro claves. Desbloqueo activo.", new[] { "VRChat confirmed all four keys. Unlocking is active.", "VRChatで4つの鍵を確認しました。ロック解除が動作中です。" } },
            { "Enviando claves por OSC. Si sigue deformado, comprueba OSC y el ID vinculado.", new[] { "Sending keys over OSC. If the avatar remains deformed, check OSC and the linked ID.", "OSCで鍵を送信中です。変形が続く場合は、OSCと関連付けたIDを確認してください。" } },
            { "Desbloqueador detenido.", new[] { "Unlocker stopped.", "ロック解除ツールを停止しました。" } },
            { "El puerto OSC está ocupado. Cierra otra copia del desbloqueador o coordina tu aplicación OSC.", new[] { "The OSC port is busy. Close another unlocker instance or coordinate your OSC application.", "OSCポートが使用中です。別のロック解除ツールを終了するか、OSCアプリケーションの設定を調整してください。" } },
            { "No se pudo iniciar OSC: ", new[] { "Could not start OSC: ", "OSCを開始できませんでした: " } },
            { "Esta herramienta es exclusiva de Linux.", new[] { "This tool requires Linux.", "このツールはLinux専用です。" } },
            { "La clave debe pertenecer a tu usuario y tener permisos 600. Ejecuta chmod 600 sobre el JSON.", new[] { "The key must belong to your user and have permissions 600. Run chmod 600 on the JSON file.", "鍵ファイルは現在のユーザーが所有し、権限が600である必要があります。JSONファイルにchmod 600を実行してください。" } },
            { "Asigna el avatarId correcto (avtr_UUID) al JSON antes de enviar claves.", new[] { "Set the correct avatarId (avtr_UUID) in the JSON before sending keys.", "鍵を送信する前に、JSONに正しいavatarId（avtr_UUID）を設定してください。" } },
            { "Parámetros de clave inválidos.", new[] { "Invalid key parameters.", "鍵パラメーターが無効です。" } },
            { "Dependencia de animación sin guardar: ", new[] { "Unsaved animation dependency: ", "未保存のアニメーション依存アセット: " } },
            { "Guarda primero los cambios de animación del avatar de trabajo: ", new[] { "Save the source avatar's animation changes first: ", "先に元のアバターのアニメーション変更を保存してください: " } },
            { "No se pudo remapear un subasset: ", new[] { "Could not remap a subasset: ", "サブアセットの参照を置き換えられませんでした: " } },
            { "Preparación exclusiva de Unity Editor para Linux.", new[] { "Preparation requires Unity Editor for Linux.", "準備にはLinux版Unity Editorが必要です。" } },
            { "Inicia Unity con -force-vulkan. No se cambia Project Settings automáticamente.", new[] { "Start Unity with -force-vulkan. Project Settings are not changed automatically.", "-force-vulkanを指定してUnityを起動してください。Project Settingsは自動で変更されません。" } },
            { "Esta versión requiere Built-in Render Pipeline.", new[] { "This version requires the Built-in Render Pipeline.", "このバージョンにはBuilt-in Render Pipelineが必要です。" } },
            { "Sal de Play Mode y espera a que termine la compilación.", new[] { "Exit Play Mode and wait for compilation to finish.", "再生モードを終了し、コンパイルが完了するまでお待ちください。" } },
            { "Selecciona la raíz con VRC Avatar Descriptor.", new[] { "Select the root with a VRC Avatar Descriptor.", "VRC Avatar Descriptorが付いたルートを選択してください。" } },
            { "La raíz del avatar debe estar en el nivel superior.", new[] { "The avatar root must be at the top hierarchy level.", "アバターのルートはHierarchyの最上位に置いてください。" } },
            { "El avatar contiene scripts ausentes.", new[] { "The avatar contains missing scripts.", "アバターにMissing Scriptがあります。" } },
            { "Primero hornea MA/AAO/VRCFury en una copia mediante su flujo de exportación. Esta versión protege un avatar final para evitar que un optimizador elimine los UV de protección.", new[] { "First bake MA/AAO/VRCFury into a copy using their export workflow. This version protects a final avatar so optimizers cannot remove the protection UVs.", "各ツールのエクスポート手順でMA/AAO/VRCFuryの処理をコピーに適用してください。保護用UVが最適化で削除されないよう、このバージョンは処理済みの最終アバターを保護します。" } },
            { "Cloth simularía la geometría ofuscada antes del shader. No se soporta en esta versión.", new[] { "Cloth would simulate the obfuscated geometry before the shader. It is unsupported in this version.", "Clothはシェーダー処理前の難読化された形状でシミュレーションされるため、このバージョンでは対応していません。" } },
            { "MeshCollider expondría geometría original. Usa colliders primitivos en una copia.", new[] { "MeshCollider would expose the original geometry. Use primitive colliders on a copy.", "MeshColliderは元の形状を公開してしまいます。コピーではプリミティブのコライダーを使用してください。" } },
            { "Avatar sin renderers.", new[] { "Avatar has no renderers.", "アバターにRendererがありません。" } },
            { "La protección de mallas skinned requiere activar el modo experimental y validar poses. La normal puede normalizarse durante el skinning.", new[] { "Protecting skinned meshes requires experimental mode and pose validation. Normals may be normalized during skinning.", "スキニングされたメッシュの保護には実験モードとポーズの検証が必要です。スキニング中に法線が正規化される場合があります。" } },
            { "Huesos escalados: no soportados por el decodificador.", new[] { "Scaled bones are unsupported by the decoder.", "スケール変更されたボーンは復元処理に対応していません。" } },
            { "Renderer no soportado: ", new[] { "Unsupported renderer: ", "未対応のRenderer: " } },
            { "Renderer sin malla: ", new[] { "Renderer has no mesh: ", "メッシュのないRenderer: " } },
            { "El número de materiales debe coincidir con los submeshes: ", new[] { "Material count must match the submesh count: ", "マテリアル数とサブメッシュ数が一致する必要があります: " } },
            { "Todos los submeshes deben usar una variante estándar lilToon soportada: ", new[] { "All submeshes must use a supported standard lilToon variant: ", "すべてのサブメッシュに対応する標準lilToonバリアントが必要です: " } },
            { "Se necesitan 32 bits libres de parámetros sincronizados.", new[] { "32 free bits of synchronized parameters are required.", "同期パラメーターに32ビットの空きが必要です。" } },
            { "No se encontraron los parámetros predeterminados del SDK. Configura expresiones personalizadas en una copia antes de protegerla.", new[] { "SDK default parameters were not found. Set up custom expressions on a copy before protecting it.", "SDKの既定パラメーターが見つかりません。コピーにカスタム表情を設定してから保護してください。" } },
            { "Animación que cambia mallas no soportada: ", new[] { "Mesh replacement animation is unsupported: ", "メッシュを差し替えるアニメーションは未対応です: " } },
            { "Material animado sin lilToon compatible: ", new[] { "Animated material without compatible lilToon: ", "互換性のあるlilToonを使っていないアニメーション対象マテリアル: " } },
            { "Animación de escala/clave incompatible: ", new[] { "Incompatible scale/key animation: ", "互換性のないスケールまたは鍵のアニメーション: " } },
            { "No se pudo asegurar el directorio de claves.", new[] { "Could not secure the key directory.", "鍵のディレクトリの権限を設定できませんでした。" } },
            { "No se pudo restringir la clave a su propietario.", new[] { "Could not restrict the key to its owner.", "鍵ファイルへのアクセスを所有者のみに制限できませんでした。" } },
            { "Dependencia de malla original detectada. Se cancela para evitar una falsa protección: ", new[] { "Original mesh dependency detected. Cancelled to avoid incomplete protection: ", "元のメッシュへの依存を検出しました。不完全な保護を避けるため中止しました: " } },
            { "No se pudo guardar el prefab.", new[] { "Could not save the prefab.", "Prefabを保存できませんでした。" } },
            { "Descriptor sin capa FX.", new[] { "Descriptor has no FX layer.", "DescriptorにFXレイヤーがありません。" } },
            { "FX debe ser AnimatorController, no OverrideController.", new[] { "FX must be an AnimatorController, not an OverrideController.", "FXにはOverrideControllerではなくAnimatorControllerが必要です。" } },
            { "Colisión de parámetros.", new[] { "Parameter name collision.", "パラメーター名が重複しています。" } },
            { "No se encontraron los archivos de la herramienta.", new[] { "Tool files were not found.", "ツールのファイルが見つかりません。" } },
            { "La malla debe ser legible. No se cambia su importador automáticamente.", new[] { "The mesh must be readable. Its importer is not changed automatically.", "メッシュは読み取り可能である必要があります。インポーターは自動で変更されません。" } },
            { "Clave inválida.", new[] { "Invalid key.", "鍵が無効です。" } },
            { "Intensidad fuera de rango (0, 0.5].", new[] { "Strength is outside the range (0, 0.5].", "強度が範囲外です（0より大きく、0.5以下）。" } },
            { "UV7 ocupado: se conserva el original y se cancela.", new[] { "UV7 is occupied: the original is preserved and the operation is cancelled.", "UV7は使用中です。元のアバターを保持し、処理を中止します。" } },
            { "UV8 ocupado: se conserva el original y se cancela.", new[] { "UV8 is occupied: the original is preserved and the operation is cancelled.", "UV8は使用中です。元のアバターを保持し、処理を中止します。" } },
            { "Malla vacía o sin normales.", new[] { "Mesh is empty or has no normals.", "メッシュが空か、法線がありません。" } },
            { "Las normales deben ser unitarias.", new[] { "Normals must have unit length.", "法線の長さは1である必要があります。" } },
            { "Blendshape con deltas de normales: no es compatible con este decodificador conservador. No se elimina ni se modifica el blendshape.", new[] { "Blendshape with normal deltas: incompatible with this conservative decoder. The blendshape is not removed or modified.", "法線の差分を含むブレンドシェイプは、この保守的な復元処理に対応していません。ブレンドシェイプは削除・変更されません。" } },
            { "Esta función requiere Linux.", new[] { "This feature requires Linux.", "この機能にはLinuxが必要です。" } },
            { "No se pudieron asegurar los permisos privados.", new[] { "Could not set private permissions.", "非公開用の権限を設定できませんでした。" } },
            { "No se pudo asegurar el archivo privado.", new[] { "Could not secure the private file.", "非公開ファイルの権限を設定できませんでした。" } },
            { "Identificador de generación inválido.", new[] { "Invalid build identifier.", "生成IDが無効です。" } },
            { "No se pudo habilitar el lanzador.", new[] { "Could not enable the launcher.", "ランチャーを有効にできませんでした。" } },
            { "Se necesita el ID avtr_... de tu avatar publicado.", new[] { "The avtr_... ID of your uploaded avatar is required.", "アップロード済みアバターのavtr_... IDが必要です。" } },
            { "Instala Python 3 con el gestor de tu distribución para iniciar OSC.", new[] { "Install Python 3 with your distribution's package manager to start OSC.", "OSCを開始するには、ディストリビューションのパッケージマネージャーでPython 3をインストールしてください。" } },
            { "Primero vincula el avatar publicado.", new[] { "Link the uploaded avatar first.", "先にアップロード済みアバターを関連付けてください。" } },
            { "Esta copia ya está protegida. Selecciona tu avatar de trabajo para generar otra versión.", new[] { "This copy is already protected. Select the source avatar to generate another version.", "このコピーは保護済みです。別のバージョンを生成するには、元のアバターを選択してください。" } },
            { "No se pudo guardar la escena de subida.", new[] { "Could not save the upload scene.", "アップロード用シーンを保存できませんでした。" } },
            { "Este perfil no tiene una escena preparada; instancia su prefab en tu escena de subida.", new[] { "This profile has no prepared scene; instantiate its prefab in your upload scene.", "このプロファイルには準備済みシーンがありません。アップロード用シーンにPrefabを配置してください。" } },
            { "Sal de Play Mode antes de reconocer una copia guardada.", new[] { "Exit Play Mode before recognizing a saved copy.", "保存済みコピーを認識する前に、再生モードを終了してください。" } },
            { "Selecciona una copia protegida de Linux Avatar Guard.", new[] { "Select a copy protected by Linux Avatar Guard.", "Linux Avatar Guardで保護したコピーを選択してください。" } },
            { "La clave seleccionada corresponde a otra copia del avatar.", new[] { "The selected key belongs to another avatar copy.", "選択した鍵は別のアバターコピー用です。" } },
            { "Abre la escena de la copia protegida para verla.", new[] { "Open the protected copy's scene to preview it.", "プレビューするには、保護済みコピーのシーンを開いてください。" } },
            { "Selecciona el avatar en Gesture Manager antes de probarlo en Play Mode.", new[] { "Select the avatar in Gesture Manager before testing in Play Mode.", "再生モードでテストする前に、Gesture Managerでアバターを選択してください。" } },
            { "Gesture Manager está simulando otro avatar.", new[] { "Gesture Manager is simulating another avatar.", "Gesture Managerは別のアバターをシミュレーションしています。" } },
            { "No se encontró la licencia de lilToon junto a sus fuentes.", new[] { "The lilToon license was not found alongside its sources.", "lilToonのソース付近にライセンスファイルが見つかりません。" } },
            { "Variante lilToon no soportada: ", new[] { "Unsupported lilToon variant: ", "未対応のlilToonバリアント: " } },
            { "Falló la variante de shader: ", new[] { "Shader variant failed: ", "シェーダーバリアントでエラーが発生しました: " } },
            { "Se requiere lilToon con fuentes .shader: ", new[] { "lilToon with .shader sources is required: ", ".shaderソースを含むlilToonが必要です: " } },
            { "El shader ya tiene una personalización incompatible: ", new[] { "The shader already has an incompatible customization: ", "シェーダーに互換性のないカスタマイズが既にあります: " } },
            { "Shader sin Properties: ", new[] { "Shader has no Properties block: ", "Propertiesブロックのないシェーダー: " } },
            { "Shader sin SubShader: ", new[] { "Shader has no SubShader block: ", "SubShaderブロックのないシェーダー: " } }
        };
    }
}
