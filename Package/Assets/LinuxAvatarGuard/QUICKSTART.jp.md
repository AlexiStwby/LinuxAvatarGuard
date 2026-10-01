# Linux Avatar Guard 0.2.1 — 無料・実験的なツール

lilToonを使用するVRChat PCアバター向けのメッシュ難読化ツールです。準備にはLinuxネイティブ版Unity EditorとVulkanが必要です。MITライセンスで公開しています。元のアバターを保持し、保護用のコピーを作成します。

## インストールと操作

1. ALCOM/VCCでVRChat SDK AvatarsとlilToonを導入し、LinuxにPython 3をインストールしてください。Unityを`-force-vulkan`で起動し、`LinuxAvatarGuard-0.2.1.unitypackage`をインポートします。
2. **Tools > Linux Avatar Guard > Asistente**を開き、上部の**Idioma**で**JP**を選択してください。既定は**ES**（スペイン語）で、**EN**（英語）も選べます。選択はすぐに適用され、Unityの再起動後や別のプロジェクトでも保持されます。Unityのメニュー名、ファイル名、単体Pythonツールのコンソール出力は変更されません。
3. 元のアバターを選び、**アバターを検証して準備**を押します。保護済みPrefabとアップロード用シーンを作成します。**アップロード用シーンを開く**で確認し、公式SDKからWindows PC用としてアップロードしてください。既存のBlueprint IDはコピーに引き継がれるので、アップロード前に確認してください。
4. **アップロード済みアバターを関連付け**を押し、VRChatでOSCを有効にして、**OSCロック解除を開始**を押します。OSCQueryが使える場合は現在のアバターも検出します。検出されない場合は別のアバターに切り替えて戻してください。

## プレビューとバックアップ

**解除状態をプレビュー / ロック状態をプレビュー**で外観を確認できます。再生モードではGesture Managerで対象アバターを選択してください。編集モードのプレビューだけではアニメーションの動作確認にはなりません。

Unityなしで使うには、**Unityなしで使うランチャーを開く**を押します。`Desbloquear.sh`または`Desbloquear.desktop`を使用してください。OSCポート9001を使えるプロセスは1つだけです。

非公開の鍵とランチャーは`${XDG_DATA_HOME:-~/.local/share}/linux-avatar-guard/<buildId>/`に保存され、UnityのLibraryを削除しても残ります。このフォルダーを公開したり、Assets/Packagesに鍵を置いたりしないでください。0.2.0からの更新では既存のプロファイルを保持します。動作中のアバターを再生成・再アップロードする必要はありません。

## 対応範囲

確認環境はUnity 2022.3.22f1、SDK Avatars 3.10.5、lilToon 2.3.4、Built-in Render Pipelineです。MA/AAO/VRCFuryは各ツールの処理を適用した最終コピーが必要です。スキニングは実験的なため、ジェスチャー、リップシンク、PhysBone、ポーズを確認してください。未対応の機能を自動で削除することはありません。

強力な暗号化や完全な抽出防止を保証するものではありません。テクスチャやGPUキャプチャは保護できず、同期された鍵を解析すればメッシュを復元できます。OSCや正しい鍵がない場合、またはSafetyでシェーダーやアニメーションが無効な場合は、変形した状態になることがあります。

外部アバターや依存パッケージは同梱していません。詳細な制限は`README.es.md`と`QUICKSTART.en.md`、ライセンスと参照元は`LICENSE.txt`と`THIRD-PARTY-NOTICES.md`を確認してください。
