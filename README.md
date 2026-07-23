# UnitySplats

[![Version](https://img.shields.io/badge/version-v1.1.0-blue.svg)](./CHANGELOG.md)
[![Unity](https://img.shields.io/badge/Unity-6000.0%2B-black.svg)](https://unity.com/)
[![License](https://img.shields.io/badge/license-MIT-green.svg)](./LICENSE.md)

[English](README.md) · [简体中文](README.zh-CN.md) · [日本語](README.ja.md)

UnitySplats is a cross-platform Unity 6 package for importing, loading, and rendering [3D Gaussian Splatting](https://repo-sam.inria.fr/fungraph/3d-gaussian-splatting/) (3DGS) content. It supports the Built-in Render Pipeline, URP, and HDRP, with GPU and portable CPU sorting paths selected at runtime.

![Gaussian splat rendered in Unity](Documentation~/Images/splat.png)

## Project lineage and attribution

UnitySplats is a modified and substantially extended fork of [wuyize25/gsplat-unity](https://github.com/wuyize25/gsplat-unity). The original Unity renderer is Copyright (c) 2025 Yize Wu and is distributed under the MIT License.

The package also adapts parts of the Gaussian-splat architecture and format behavior from the [PlayCanvas Engine](https://github.com/playcanvas/engine), including concepts used by the PLY, SOG, and GLB implementations. The PlayCanvas reference used during this Unity work was `v2.21.0-beta.14` (`d5fe88878e338936fe763bbce1a58bc315e89cbe`). PlayCanvas Engine is Copyright (c) 2011-2026 PlayCanvas Ltd and is distributed under the MIT License.

UnitySplats is a Unity implementation maintained by ARLOOPA. It is not an official PlayCanvas product and does not include the PlayCanvas JavaScript engine. Existing upstream copyright and license notices are retained in the source and in [Third Party Notices](./Third%20Party%20Notices.md).

## Features

- Standard binary 3DGS PLY and PlayCanvas compressed PLY import and runtime loading.
- Bundled and unpacked PlayCanvas SOG v1/v2 support.
- SPZ v1-v4 support, including Zstandard-compressed SPZ v4 attributes.
- Gaussian-splat GLB support using `KHR_gaussian_splatting`.
- Runtime loading from file paths, byte arrays, and streams.
- Spark-packed and uncompressed Unity asset representations.
- Asynchronous GPU upload with optional rendering during upload.
- Orthographic cameras, MSAA, cutouts, active source ranges, and XR integration.
- Runtime graphics-backend selection for Direct3D, Vulkan, Metal, OpenGL, and WebGL 2.

## Supported formats

| Format | Import | Runtime load | Notes |
| --- | --- | --- | --- |
| `.ply` | Yes | Yes | Binary little-endian standard 3DGS and PlayCanvas compressed PLY |
| `.sog` | Yes | Yes | Bundled SOG v1/v2 |
| unpacked SOG `meta.json` | Yes | Yes | Requires the complete SOG directory |
| `.spz` | Yes | Yes | SPZ v1-v4, SH degrees 0-4 |
| `.glb` | Yes | Yes | Requires `KHR_gaussian_splatting`; select the UnitySplats importer per file |

The importer's `Auto` coordinate mode uses glTF's LUF frame for GLB and the common 3DGS/OpenGL RUB frame for PLY, SOG, and SPZ, converting the data to Unity RUF.

## Platform and graphics API support

UnitySplats chooses the fastest compatible sorting and rendering path at runtime.

| Platform | Graphics APIs | Sort/render path |
| --- | --- | --- |
| Windows | Direct3D 12, Vulkan | GPU radix sort when wave/subgroup requirements are available; CPU fallback otherwise |
| Windows | Direct3D 11, OpenGL Core | Asynchronous CPU counting sort with instanced GPU rendering |
| Linux | Vulkan, OpenGL Core | GPU radix sort on compatible Vulkan devices; CPU fallback otherwise |
| Android | Vulkan | GPU radix sort when supported; CPU fallback otherwise |
| Android | OpenGL ES 3.1 | Asynchronous CPU sort and portable sampled-texture draw path |
| macOS, iOS | Metal | GPU radix sort when supported; CPU fallback otherwise |
| Web | WebGL 2 | Main-thread CPU sort and portable sampled-texture draw path |

The OpenGL ES path uses RGBA8 byte-packed and floating-point sampled textures instead of vertex-stage storage buffers. This avoids driver failures seen when some mobile GPUs report SSBO support but reject Unity's generated `BufferBlock` bindings. OpenGL ES 3.1 is the minimum supported Android OpenGL version.

Maintainer testing has covered multiple Android and Apple devices using OpenGL ES, Vulkan, and Metal, including Samsung and HONOR Android hardware and Meta Quest 3 and Meta Quest 3S headsets. Samsung and HONOR devices were also used to validate the GLES compatibility path. Device drivers still vary, so a listed API is a supported target rather than a guarantee for every GPU/OS combination. Hardware reports are welcome through GitHub Issues.

The WebGL 2 path does not create compute or structured graphics buffers. It stores splat data and draw order in sampled textures and runs the counting sort on Unity's main thread. Use Spark compression and moderate splat counts for Web builds: browser memory limits and synchronous sorting make large uncompressed assets substantially more expensive than on native platforms. Global merge and compute cutouts are unavailable on WebGL.

### Sorting limitations

The GPU radix path requires wave/subgroup operations and compatible compute workgroup limits. The CPU fallback retains instanced GPU drawing, but disables global multi-renderer merge and compute cutouts. It keeps one sort order per renderer and prioritizes a game camera, so substantially different simultaneous camera viewpoints share that order.

Cross-renderer sorting is enabled by default. It globally depth-orders splats from overlapping renderers and draws them as one merged scene when all active renderers are fully uploaded, Spark-packed, use the same GameObject layer, keep the default render order, and fit the packed renderer/index limits. Configure it from `Project Settings > Gsplat`. Other configurations fall back to separate draw calls.

## Requirements

- Unity 6000.0 or newer. The included development project uses Unity 6000.3.9f1.
- `com.unity.mathematics` 1.3.2 or newer.
- [`com.netpyoung.webp`](https://github.com/netpyoung/unity.webp) 0.3.22 for SOG image decoding.
- A supported graphics API from the table above.

## Installation

### Install from a Git URL with automatic Unity.WebP resolution

UnitySplats already declares Unity.WebP 0.3.22 as a package dependency. To let Unity resolve it automatically, add the OpenUPM registry for the `com.netpyoung` scope and the UnitySplats Git URL to the target project's `Packages/manifest.json`:

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

Merge these entries with any existing registries and dependencies. Unity then installs Unity.WebP automatically when it resolves UnitySplats.

### Install both packages from Git URLs

Unity Package Manager cannot resolve one Git package as a transitive dependency of another Git package. Install Unity.WebP first:

1. Open `Window > Package Manager`.
2. Click `+` and choose `Install package from git URL...`.
3. Install Unity.WebP:

   ```text
   https://github.com/netpyoung/unity.webp.git?path=unity_project/Assets/unity.webp#0.3.22
   ```

4. Repeat the process for UnitySplats:

   ```text
   https://github.com/arloopa/UnitySplats.git
   ```

This installs the repository's current default branch.

### Install both Git dependencies from `manifest.json`

Add both direct dependencies to the target project's `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.netpyoung.webp": "https://github.com/netpyoung/unity.webp.git?path=unity_project/Assets/unity.webp#0.3.22",
    "com.arloopa.unitysplats": "https://github.com/arloopa/UnitySplats.git"
  }
}
```

Keep the project's other dependencies in the same object.

### Install from a release archive

Download the UnitySplats release ZIP and extract it outside the target project's `Assets` folder. Configure the OpenUPM registry above, install Unity.WebP using its Git URL, or add a separately downloaded Unity.WebP package. Then open `Window > Package Manager`, choose `+ > Install package from disk...`, and select the UnitySplats `package.json`.

The UnitySplats source archive does not bundle Unity.WebP. Unity only accepts Git dependency URLs in the consuming project's `Packages/manifest.json`; package-to-package automatic resolution therefore requires a scoped registry such as OpenUPM.

## Render-pipeline setup

- **Built-in Render Pipeline:** no additional render feature is required.
- **URP:** open the active Universal Renderer Data asset and add `Gsplat URP Feature`. Unity 6 RenderGraph and compatibility mode are both supported.
- **HDRP:** add a Custom Pass Volume and a `Gsplat HDRP Pass` at the `Before Transparent` injection point.

For maximum performance, prefer Vulkan on Android and Vulkan or Direct3D 12 on Windows. Direct3D 11 and OpenGL automatically select the portable CPU sorter. If Android uses OpenGL, require OpenGL ES 3.1 in Player Settings.

## Import and render a splat

1. Copy or drag a `.ply`, `.sog`, `.spz`, or Gaussian-splat `.glb` file into the project's `Assets` folder.
   For GLB, select the file and choose `Assets > UnitySplats > Choose GLB Importer... > UnitySplats (Gaussian Splat)`. You can also choose `GsplatImporter` from Unity's Importer dropdown in the file Inspector. UnitySplats is an optional GLB importer so it can coexist with glTFast and other packages that handle ordinary GLB models.
   If two other installed packages both register themselves as the default GLB importer, Unity will continue to report a conflict between those packages until one is disabled or changed to an override importer; this does not prevent explicitly selecting UnitySplats for the Gaussian GLB.
2. Select the imported `GsplatAsset`. `Spark` is the recommended packed representation; select `Uncompressed` when exact in-project values matter more than memory and upload speed.
3. Drag the imported asset from the Project window into the Hierarchy or Scene view. UnitySplats automatically creates a GameObject with `GsplatRenderer`, assigns the asset, and sets its local Z rotation to 180 degrees.

You can also create a GameObject manually, add a `Gsplat Renderer` component, and assign the imported asset to its `Gsplat Asset` field.

In the Scene view, click anywhere inside a splat's transformed asset bounds to select its GameObject; a collider is not required. Unity's normal Shift/Ctrl selection modifiers continue to work. Selecting several `GsplatRenderer` GameObjects enables multi-object editing for their shared inspector properties and can fit all attached `BoxCollider` components in one action.

When two or more Spark renderers overlap, leave **Enable Cross-Renderer Sorting** enabled in `Project Settings > Gsplat`. On supported Vulkan, Direct3D 12, and Metal devices this creates one shared per-splat depth order, preventing whole captures from switching in front of one another as the camera moves. The portable CPU sorter used by OpenGL, WebGL, Direct3D 11, and unsupported GPU configurations retains separate draw calls and cannot provide the true merged order.

Most 3DGS content is trained in gamma space. `Gamma To Linear` is enabled by default on `GsplatRenderer`; disable it when the source colors are already authored for the project's linear workflow. Conversion before alpha blending can otherwise reduce visual accuracy.

### Unpacked SOG

Select the SOG `meta.json`, then choose `Assets > UnitySplats > Import unpacked SOG (Spark)`. Keep all referenced files beside the metadata.

## Runtime loading

Runtime loaders return an in-memory `GsplatAsset` and must create/assign Unity objects on the main thread.

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

`LoadFile` accepts filesystem paths. Android APK `StreamingAssets`, HTTP(S), and other URI-based sources should be read with `UnityWebRequest`, then passed to `GsplatRuntimeLoader.Load(byte[], ...)`. Use `Load(Stream, ...)` or `LoadUnpackedSog(...)` for other input forms.

The Package Manager includes an optional **Runtime PLY Loading** sample that demonstrates byte-array loading and runtime asset cleanup.

## Active source ranges

`GsplatRenderer.SetActiveRanges` selects an exact union of source-splat ranges without creating another asset. Ranges use half-open `[Offset, Offset + Count)` semantics and are normalized when they overlap or touch.

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

These methods update Unity GPU buffers and must run on the main thread. Compute cutouts are bypassed while explicit ranges are enabled.

## Format notes

- PLY input must be binary little-endian.
- SOG v1/v2 uses Unity.WebP and its platform libwebp binaries.
- SPZ files containing `SPZ_ADOBE_coordinate_system` use that descriptor automatically.
- GLB primitives must declare `KHR_gaussian_splatting` and the required accessors. UnitySplats also recognizes the legacy logarithmic scale encoding written by `splat-transform` 2.6.0 and older. Scene graphs that instance splat meshes with several node transforms are not flattened into one asset.
## XR support

| XR render mode | Built-in | URP | HDRP |
| --- | --- | --- | --- |
| Multi-pass | Yes | Yes | No |
| Single Pass Instanced | No | Yes | No |

## Documentation

- [Implementation Details](./Documentation~/Implementation%20Details.md)
- [Changelog](./CHANGELOG.md)
- [Third Party Notices](./Third%20Party%20Notices.md)

## License

UnitySplats is distributed under the [MIT License](./LICENSE.md).

- ARLOOPA modifications and packaging: Copyright (c) 2026 ARLOOPA.
- Original `gsplat-unity` work: Copyright (c) 2025 Yize Wu, MIT License.
- PlayCanvas-derived portions: Copyright (c) 2011-2026 PlayCanvas Ltd, MIT License.

Additional source components retain their original notices. See [Third Party Notices](./Third%20Party%20Notices.md) for the complete attribution list, including UnityGaussianSplatting, GPUSorting, Spark, SPZ, ZstdSharp, Unity.WebP, and libwebp.

## Acknowledgements

- [PlayCanvas Engine](https://github.com/playcanvas/engine)
- [wuyize25/gsplat-unity](https://github.com/wuyize25/gsplat-unity)
- [aras-p/UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting)
- [b0nes164/GPUSorting](https://github.com/b0nes164/GPUSorting)
- [sparkjsdev/spark](https://github.com/sparkjsdev/spark)
- [nianticlabs/spz](https://github.com/nianticlabs/spz)
- [oleg-st/ZstdSharp](https://github.com/oleg-st/ZstdSharp)
- [netpyoung/unity.webp](https://github.com/netpyoung/unity.webp)
