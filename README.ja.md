# UnitySplats

[![Version](https://img.shields.io/badge/version-v1.1.0-blue.svg)](./CHANGELOG.md)
[![Unity](https://img.shields.io/badge/Unity-6000.0%2B-black.svg)](https://unity.com/)
[![License](https://img.shields.io/badge/license-MIT-green.svg)](./LICENSE.md)

[English](README.md) · [简体中文](README.zh-CN.md) · 日本語

UnitySplats は、[3D Gaussian Splatting](https://repo-sam.inria.fr/fungraph/3d-gaussian-splatting/)（3DGS）コンテンツのインポート、読み込み、レンダリングを行うクロスプラットフォームの Unity 6 パッケージです。ビルトインレンダーパイプライン、URP、HDRP に対応し、実行時に GPU または移植可能な CPU ソート経路を選択します。

![Unity でレンダリングした Gaussian Splat](Documentation~/Images/splat.png)

## プロジェクトの系譜と帰属表示

UnitySplats は [wuyize25/gsplat-unity](https://github.com/wuyize25/gsplat-unity) を改変し、大幅に拡張したフォークです。元の Unity レンダラーは Copyright (c) 2025 Yize Wu で、MIT License の下で配布されています。

このパッケージは、PLY、SOG、GLB 実装で使われる概念を含め、[PlayCanvas Engine](https://github.com/playcanvas/engine) の Gaussian Splat アーキテクチャと形式動作の一部も採用しています。この Unity 実装で参照した PlayCanvas は `v2.21.0-beta.14`（`d5fe88878e338936fe763bbce1a58bc315e89cbe`）です。PlayCanvas Engine は Copyright (c) 2011-2026 PlayCanvas Ltd で、MIT License の下で配布されています。

UnitySplats は ARLOOPA が保守する Unity 実装です。PlayCanvas の公式製品ではなく、PlayCanvas JavaScript エンジンも含みません。既存の上流著作権およびライセンス表記は、ソースと[サードパーティー通知](./Third%20Party%20Notices.md)に保持されています。

## 機能

- 標準バイナリ 3DGS PLY と PlayCanvas 圧縮 PLY のインポートおよび実行時読み込み。
- バンドル済みおよび展開済みの PlayCanvas SOG v1/v2 に対応。
- Zstandard 圧縮された SPZ v4 属性を含む SPZ v1-v4 に対応。
- `KHR_gaussian_splatting` を使用する Gaussian Splat GLB に対応。
- ファイルパス、バイト配列、ストリームからの実行時読み込み。
- Spark パック形式と非圧縮形式の Unity アセット表現。
- 非同期 GPU アップロードと、アップロード中のオプションレンダリング。
- 平行投影カメラ、MSAA、カットアウト、アクティブソース範囲、XR 統合。
- Direct3D、Vulkan、Metal、OpenGL、WebGL 2 のグラフィックスバックエンドを実行時に選択。

## 対応形式

| 形式 | インポート | 実行時読み込み | 備考 |
| --- | --- | --- | --- |
| `.ply` | はい | はい | バイナリリトルエンディアンの標準 3DGS および PlayCanvas 圧縮 PLY |
| `.sog` | はい | はい | バンドル済み SOG v1/v2 |
| 展開済み SOG `meta.json` | はい | はい | 完全な SOG ディレクトリが必要 |
| `.spz` | はい | はい | SPZ v1-v4、SH 次数 0-4 |
| `.glb` | はい | はい | `KHR_gaussian_splatting` が必要。ファイルごとに UnitySplats インポーターを選択 |

インポーターの `Auto` 座標モードは、GLB には glTF の LUF フレームを、PLY、SOG、SPZ には一般的な 3DGS/OpenGL の RUB フレームを使い、データを Unity RUF に変換します。

## プラットフォームとグラフィックス API の対応

UnitySplats は実行時に、互換性のある最速のソートおよびレンダリング経路を選択します。

| プラットフォーム | グラフィックス API | ソート/レンダリング経路 |
| --- | --- | --- |
| Windows | Direct3D 12、Vulkan | wave/subgroup の要件を満たす場合は GPU 基数ソート、それ以外は CPU にフォールバック |
| Windows | Direct3D 11、OpenGL Core | 非同期 CPU カウンティングソートと GPU インスタンシングレンダリング |
| Linux | Vulkan、OpenGL Core | 互換性のある Vulkan デバイスでは GPU 基数ソート、それ以外は CPU にフォールバック |
| Android | Vulkan | 対応時は GPU 基数ソート、それ以外は CPU にフォールバック |
| Android | OpenGL ES 3.1 | 非同期 CPU ソートと移植可能なサンプルドテクスチャ描画経路 |
| macOS、iOS | Metal | 対応時は GPU 基数ソート、それ以外は CPU にフォールバック |
| Web | WebGL 2 | メインスレッド CPU ソートと移植可能なサンプルドテクスチャ描画経路 |

OpenGL ES 経路では、頂点ステージのストレージバッファーではなく、RGBA8 バイトパックテクスチャと浮動小数点サンプルドテクスチャを使用します。これにより、一部のモバイル GPU が SSBO 対応を報告しながら Unity 生成の `BufferBlock` バインディングを拒否するときに見られるドライバー障害を回避します。Android で対応する最低 OpenGL バージョンは OpenGL ES 3.1 です。

メンテナーは、OpenGL ES、Vulkan、Metal を使う複数の Android および Apple デバイスでテストしています。Samsung と HONOR の Android 端末、Meta Quest 3 と Meta Quest 3S ヘッドセットも含まれます。Samsung と HONOR の端末は GLES 互換経路の検証にも使用しました。デバイスドライバーには差があるため、記載された API は対応対象であり、すべての GPU/OS の組み合わせを保証するものではありません。ハードウェアに関する報告は GitHub Issues で歓迎します。

WebGL 2 経路は、コンピュートバッファーや構造化グラフィックスバッファーを作成しません。Splat データと描画順序をサンプルドテクスチャに保存し、Unity のメインスレッドでカウンティングソートを実行します。Web ビルドでは Spark 圧縮を使い、Splat 数を適度に保ってください。ブラウザーのメモリ制限と同期ソートにより、大きな非圧縮アセットはネイティブプラットフォームより大幅に高コストです。WebGL ではグローバルマージとコンピュートカットアウトを利用できません。

### ソートの制限

GPU 基数ソート経路には、wave/subgroup 操作と互換性のあるコンピュートワークグループ制限が必要です。CPU フォールバックは GPU インスタンシング描画を維持しますが、グローバルな複数レンダラーマージとコンピュートカットアウトを無効にします。レンダラーごとに 1 つのソート順を保持し、ゲームカメラを優先するため、同時に存在する大きく異なるカメラ視点は同じ順序を共有します。

クロスレンダラーソートは既定で有効です。すべてのアクティブレンダラーが完全にアップロード済み、Spark パック形式、同じ GameObject レイヤー、既定のレンダリング順序で、パックされたレンダラー/インデックス制限内に収まる場合、重なったレンダラーの Splat をグローバルに深度ソートし、1 つのマージ済みシーンとして描画します。`Project Settings > Gsplat` から設定できます。それ以外の構成では個別のドローコールにフォールバックします。

## 要件

- Unity 6000.0 以降。同梱の開発プロジェクトは Unity 6000.3.9f1 を使用しています。
- `com.unity.mathematics` 1.3.2 以降。
- SOG 画像のデコードに [`com.netpyoung.webp`](https://github.com/netpyoung/unity.webp) 0.3.22。
- 上の表にある対応グラフィックス API。

## インストール

### Git URL からインストールし、Unity.WebP を自動解決

UnitySplats は Unity.WebP 0.3.22 をパッケージ依存関係として宣言済みです。Unity に自動解決させるには、`com.netpyoung` スコープ用の OpenUPM レジストリと UnitySplats の Git URL を、対象プロジェクトの `Packages/manifest.json` に追加します。

```json
{
  "scopedRegistries": [
    {
      "name": "OpenUPM",
      "url": "https://package.openupm.com",
      "scopes": [
        "com.netpyoung"
      ]
    }
  ],
  "dependencies": {
    "com.arloopa.unitysplats": "https://github.com/arloopa/UnitySplats.git"
  }
}
```

これらのエントリーを既存のレジストリや依存関係と統合してください。Unity は UnitySplats の解決時に Unity.WebP も自動でインストールします。

### 両方のパッケージを Git URL からインストール

Unity Package Manager は、ある Git パッケージを別の Git パッケージの推移的依存関係として解決できません。先に Unity.WebP をインストールしてください。

1. `Window > Package Manager` を開きます。
2. `+` をクリックし、`Install package from git URL...` を選択します。
3. Unity.WebP をインストールします。

   ```text
   https://github.com/netpyoung/unity.webp.git?path=unity_project/Assets/unity.webp#0.3.22
   ```

4. 同じ手順を繰り返して UnitySplats をインストールします。

   ```text
   https://github.com/arloopa/UnitySplats.git
   ```

リポジトリの現在の既定ブランチがインストールされます。

### `manifest.json` から両方の Git 依存関係をインストール

2 つの直接依存関係を対象プロジェクトの `Packages/manifest.json` に追加します。

```json
{
  "dependencies": {
    "com.netpyoung.webp": "https://github.com/netpyoung/unity.webp.git?path=unity_project/Assets/unity.webp#0.3.22",
    "com.arloopa.unitysplats": "https://github.com/arloopa/UnitySplats.git"
  }
}
```

プロジェクトのほかの依存関係も同じオブジェクトに残してください。

### リリースアーカイブからインストール

UnitySplats のリリース ZIP をダウンロードし、対象プロジェクトの `Assets` フォルダー外に展開します。前述の OpenUPM レジストリを設定し、Git URL から Unity.WebP をインストールするか、別途ダウンロードした Unity.WebP パッケージを追加します。次に `Window > Package Manager` を開き、`+ > Install package from disk...` を選択して、UnitySplats の `package.json` を指定します。

UnitySplats のソースアーカイブには Unity.WebP は含まれません。Unity が受け入れる Git 依存 URL は利用側プロジェクトの `Packages/manifest.json` にあるものだけなので、パッケージ間の自動解決には OpenUPM のようなスコープ付きレジストリが必要です。

## レンダーパイプラインの設定

- **ビルトインレンダーパイプライン：**追加のレンダー機能は不要です。
- **URP：**有効な Universal Renderer Data アセットを開き、`Gsplat URP Feature` を追加します。Unity 6 RenderGraph と互換モードの両方に対応します。
- **HDRP：**Custom Pass Volume と `Gsplat HDRP Pass` を追加し、後者を `Before Transparent` インジェクションポイントに配置します。

最大のパフォーマンスを得るには、Android では Vulkan、Windows では Vulkan または Direct3D 12 を優先してください。Direct3D 11 と OpenGL は移植可能な CPU ソーターを自動選択します。Android で OpenGL を使う場合は、Player Settings で OpenGL ES 3.1 を必須にしてください。

## Splat のインポートとレンダリング

1. `.ply`、`.sog`、`.spz`、または Gaussian Splat `.glb` ファイルを、プロジェクトの `Assets` フォルダーへコピーまたはドラッグします。
   GLB の場合はファイルを選択し、`Assets > UnitySplats > Choose GLB Importer... > UnitySplats (Gaussian Splat)` を選びます。ファイル Inspector の Unity Importer ドロップダウンから `GsplatImporter` を選ぶこともできます。UnitySplats はオプションの GLB インポーターなので、通常の GLB モデルを扱う glTFast などのパッケージと共存できます。
   インストール済みの別の 2 パッケージがどちらも既定の GLB インポーターとして登録されている場合、片方を無効にするかオーバーライドインポーターへ変更するまで、Unity は両者の競合を報告し続けます。これは Gaussian GLB 用に UnitySplats を明示的に選ぶことを妨げません。
2. インポートした `GsplatAsset` を選択します。推奨のパック形式は `Spark` です。メモリやアップロード速度より、プロジェクト内の正確な値を重視する場合は `Uncompressed` を選びます。
3. インポートしたアセットを Project ウィンドウから Hierarchy または Scene ビューへドラッグします。UnitySplats は `GsplatRenderer` を持つ GameObject を自動作成し、アセットを割り当て、ローカル Z 回転を 180 度に設定します。

GameObject を手動で作成して `Gsplat Renderer` コンポーネントを追加し、インポートしたアセットを `Gsplat Asset` フィールドへ割り当てることもできます。

Scene ビューでは、変換後の Splat アセット境界内をクリックすると、コライダーなしで GameObject を選択できます。Unity 通常の Shift/Ctrl 選択修飾キーも引き続き機能します。複数の `GsplatRenderer` GameObject を選択すると、共通の Inspector プロパティを複数オブジェクトで編集でき、アタッチされたすべての `BoxCollider` コンポーネントを 1 回でフィットさせることもできます。

2 つ以上の Spark レンダラーが重なる場合は、`Project Settings > Gsplat` の **Enable Cross-Renderer Sorting** を有効のままにしてください。対応する Vulkan、Direct3D 12、Metal デバイスでは、Splat ごとに 1 つの共有深度順序を作成し、カメラ移動時にキャプチャ全体が前後に入れ替わるのを防ぎます。OpenGL、WebGL、Direct3D 11、および非対応 GPU 構成で使われる移植可能な CPU ソーターは個別のドローコールを維持するため、真のマージ順序は提供できません。

ほとんどの 3DGS コンテンツはガンマ空間で学習されています。`Gamma To Linear` は `GsplatRenderer` で既定で有効です。ソースカラーがプロジェクトのリニアワークフロー用に作成済みなら無効にしてください。そうしないと、アルファブレンディング前の変換で視覚的な精度が低下する場合があります。

### 展開済み SOG

SOG の `meta.json` を選択し、`Assets > UnitySplats > Import unpacked SOG (Spark)` を選びます。参照されるすべてのファイルをメタデータと同じ場所に置いてください。

## 実行時読み込み

実行時ローダーはメモリ上の `GsplatAsset` を返し、Unity オブジェクトの作成と割り当てはメインスレッドで行う必要があります。

```csharp
using Gsplat;

GsplatAsset asset = GsplatRuntimeLoader.LoadFile(
    absolutePath,
    CompressionMode.Spark);

GsplatRenderer renderer = GetComponent<GsplatRenderer>();
renderer.GsplatAsset = asset;

// When the renderer no longer uses the runtime asset:
renderer.GsplatAsset = null;
Destroy(asset);
```

`LoadFile` はファイルシステムパスを受け取ります。Android APK の `StreamingAssets`、HTTP(S)、その他 URI ベースのソースは `UnityWebRequest` で読み取り、`GsplatRuntimeLoader.Load(byte[], ...)` に渡してください。ほかの入力形式には `Load(Stream, ...)` または `LoadUnpackedSog(...)` を使用します。

Package Manager には、バイト配列読み込みと実行時アセットのクリーンアップを示すオプションの **Runtime PLY Loading** サンプルが含まれます。

## アクティブソース範囲

`GsplatRenderer.SetActiveRanges` は、別のアセットを作成せずにソース Splat 範囲の正確な和集合を選択します。範囲は半開区間 `[Offset, Offset + Count)` のセマンティクスを使い、重複または接触すると正規化されます。

```csharp
renderer.SetActiveRanges(new[]
{
    new GsplatActiveRange(offset: 1000, count: 500),
    new GsplatActiveRange(offset: 3000, count: 250)
});

uint configured = renderer.ActiveSplatCount;
uint resident = renderer.ResidentActiveSplatCount;

renderer.ClearActiveRanges();
```

これらのメソッドは Unity GPU バッファーを更新するため、メインスレッドで実行する必要があります。明示的な範囲が有効な間は、コンピュートカットアウトを迂回します。

## 形式に関する注意

- PLY 入力はバイナリリトルエンディアンである必要があります。
- SOG v1/v2 は Unity.WebP と、各プラットフォーム用の libwebp バイナリを使用します。
- `SPZ_ADOBE_coordinate_system` を含む SPZ ファイルは、その記述子を自動的に使用します。
- GLB プリミティブは `KHR_gaussian_splatting` と必要なアクセサーを宣言する必要があります。UnitySplats は `splat-transform` 2.6.0 以前が書き出す従来の対数スケールエンコーディングも認識します。複数のノード変換で Splat メッシュをインスタンス化するシーングラフは、1 つのアセットにフラット化されません。
## XR 対応

| XR レンダリングモード | ビルトイン | URP | HDRP |
| --- | --- | --- | --- |
| マルチパス | はい | はい | いいえ |
| Single Pass Instanced | いいえ | はい | いいえ |

## ドキュメント

- [実装の詳細](./Documentation~/Implementation%20Details.md)
- [変更履歴](./CHANGELOG.md)
- [サードパーティー通知](./Third%20Party%20Notices.md)

## ライセンス

UnitySplats は [MIT License](./LICENSE.md) の下で配布されます。

- ARLOOPA による変更とパッケージ化：Copyright (c) 2026 ARLOOPA。
- 元の `gsplat-unity`：Copyright (c) 2025 Yize Wu、MIT License。
- PlayCanvas 由来の部分：Copyright (c) 2011-2026 PlayCanvas Ltd、MIT License。

追加のソースコンポーネントは元の通知を保持します。UnityGaussianSplatting、GPUSorting、Spark、SPZ、ZstdSharp、Unity.WebP、libwebp を含む完全な帰属一覧は、[サードパーティー通知](./Third%20Party%20Notices.md)を参照してください。

## 謝辞

- [PlayCanvas Engine](https://github.com/playcanvas/engine)
- [wuyize25/gsplat-unity](https://github.com/wuyize25/gsplat-unity)
- [aras-p/UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting)
- [b0nes164/GPUSorting](https://github.com/b0nes164/GPUSorting)
- [sparkjsdev/spark](https://github.com/sparkjsdev/spark)
- [nianticlabs/spz](https://github.com/nianticlabs/spz)
- [oleg-st/ZstdSharp](https://github.com/oleg-st/ZstdSharp)
- [netpyoung/unity.webp](https://github.com/netpyoung/unity.webp)
