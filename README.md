# UnitySplats

[![Version](https://img.shields.io/badge/version-v2.0.0-blue.svg)](./CHANGELOG.md)
[![Unity](https://img.shields.io/badge/Unity-6000.0%2B-black.svg)](https://unity.com/)
[![License](https://img.shields.io/badge/license-MIT-green.svg)](./LICENSE.md)

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
- Runtime graphics-backend selection for Direct3D, Vulkan, Metal, OpenGL, WebGL 2, and WebGPU.

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
| Web | WebGPU | Optional native-worker CPU sort and CPU sort optimization; main-thread fallback; splat textures with an order buffer |

The OpenGL ES path uses RGBA8 byte-packed and floating-point sampled textures instead of vertex-stage storage buffers. This avoids driver failures seen when some mobile GPUs report SSBO support but reject Unity's generated `BufferBlock` bindings. OpenGL ES 3.1 is the minimum supported Android OpenGL version.

Maintainer testing has covered multiple Android and Apple devices using OpenGL ES, Vulkan, and Metal, including Samsung and HONOR Android hardware and Meta Quest 3 and Meta Quest 3S headsets. Samsung and HONOR devices were also used to validate the GLES compatibility path. Device drivers still vary, so a listed API is a supported target rather than a guarantee for every GPU/OS combination. Hardware reports are welcome through GitHub Issues.

The WebGL 2 path does not create compute or structured graphics buffers. It stores splat data and draw order in sampled textures and runs the counting sort on Unity's main thread. Use Spark compression and moderate splat counts for Web builds: browser memory limits and synchronous sorting make large uncompressed assets substantially more expensive than on native platforms. Global merge and compute cutouts are unavailable on WebGL.

### WebGPU and native CPU sorting

On Unity 6000+, enable WebGPU and native C/C++ multithreading in Player Settings, then enable **Native Web Sorting > Enable Native Web Sort** in `Project Settings > Gsplat` (off by default). The sorter is capped by the precreated workers actually supplied by Unity. Enable **Show Debug Log** before native-pool initialization to log the actual worker count; enabling it after initialization does not replay that one-time message. The internal request cap does not imply that many workers are available and does not increase Unity's worker allocation. Confirm `Native WebGPU radix sorting active` in the player log. This uses CPU workers, not subgroup-dependent GPU sorting; CPU-skinned models retain the main-thread fallback.

Serve the player page over HTTPS (localhost is fine for development), and set the following HTTP response headers on the player page and worker script responses. COEP is a choice between two options; `credentialless` is shown below. If you choose `require-corp`, simply replace the corresponding line.

```http
Cross-Origin-Opener-Policy: same-origin
Cross-Origin-Embedder-Policy: credentialless
```

The page must be cross-origin isolated (`crossOriginIsolated === true`); cross-origin resources must also satisfy COEP through appropriate CORS or CORP responses. Missing native capabilities select main-thread sorting after the player starts. A threaded player that cannot start without shared memory needs a separate non-threaded build/loader fallback. Workers persist until the page closes. Their allocation is determined by the player build/runtime, not a public Gsplat worker-count setting; rebuild/reload as appropriate when that allocation changes.

### CPU sort optimization

Enable **CPU Sort Optimization > Enable Cpu Sort Optimization** in `Project Settings > Gsplat` (off by default). This applies only to CPU sorting; it does not change the GPU radix path or allocate an octree when GPU sorting is active. Fully uploaded static uncompressed/Spark assets and active source selections are supported. CPU-skinned models and unsupported sources keep the existing sorter.

The octree selects visible source IDs, which then enter one global camera-space **view-depth** sort. It is not a collection of independently sorted spatial blocks. Selected points within **Near Exact Distance** (default 40 world units) use full 32-bit depth keys; far points use the high 18 bits of the sortable float key. This is an absolute camera-space depth distance, not a fraction of the model's depth span or point count. The boundary bucket joins the exact range to preserve the far-to-near order between the two ranges. Set the distance to **0** to sort all selected points exactly; this does not disable culling or asynchronous execution.

Desktop CPU paths use a managed background task. Web requires **Enable Native Web Sort** and the worker setup described above. If native workers are unavailable, the Web optimization is skipped before building the octree and the existing main-thread CPU sorter is used. Threadless WebGL therefore does not receive these optimizations. Disable **Enable Cpu Sort Optimization** to use the existing full-precision CPU sorter; **Enable Native Web Sort** independently controls whether supported WebGPU sorting runs on native workers.

The previous completed order stays drawable while the next job runs. Completed results are collected and uploaded before replacement work starts in the same CPU tick, so the output storage is not overwritten before upload. Unchanged inputs do not resubmit. Refresh depends on sorting time and the CPU tick, without an extra collect-only frame or upload budget. The managed hybrid sorter keeps input-ordinal depth keys and two reusable ID buffers; no separate CPU publication copy is needed. Small exact ranges use stable insertion sort, and equal-key ranges retain input order without radix passes. Both managed and native hybrid sorting preserve equal-key input order and reuse bucket counts as scatter offsets in place. Culling margins, leaf size and bucket width are internal implementation choices.

On WebGPU, CPU draw order uses a **StructuredBuffer**, while splat data remains in sampled textures. Only the visible order prefix is uploaded, directly from native output when available. This storage choice is automatic, including when both feature switches are off. WebGL keeps the RGBA8 order texture and uploads its full payload.

Each renderer still has one preferred-camera order. Enabled cameras are checked before CPU drawing; camera gathers and SRP callbacks also observe cameras that may consume the shared order, including cameras without the sorting Feature. This is conservative: a camera need not ultimately draw the renderer to be counted. Layer-incompatible, Preview and relighting-proxy cameras are excluded. Multiple compatible cameras observed in the current or previous frame disable frustum culling. On that transition, an old culled job is drained and all original source IDs are uploaded before asynchronous sorting resumes. A newly observed manual camera takes effect at the next CPU tick. Culling can resume with one observed compatible viewpoint; separate per-camera orders are not created.

The tree, selection buffer and sorting scratch add CPU memory proportional to splat count. Tree construction is synchronous and can add an initial preparation delay; failed construction releases native sessions and pinned inputs. Single-camera visibility is asynchronous, so a fast turn or teleport can temporarily leave newly visible points outside the last completed selection.

**Show Debug Log** reports timings for native Web sorting, the existing full CPU sorter and the optimized path. Summaries include selected-point and exact near-point counts, job age, view-depth-axis change and upload-call time. The angle includes model transforms as well as camera motion. Upload timings measure the CPU call, not GPU completion. Logging is optional and does not affect job scheduling.

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

## Compare Gaussian lighting modes

![Red and blue point lights moving around a Gaussian splat capture, relit in real time by Gaussian Relighting in Volume mode](Documentation~/Images/volume-relighting.gif)

Add **Gaussian Relighting** to the same GameObject as `GsplatRenderer`, then choose its **Mode**. **Volume** is the default; the same component preserves the settings for both paths when the mode changes:

- **Volume:** automatically combines the strongest active directional, point, and spot lights affecting the renderer (up to four), with Unity-style inverse-square and range falloff for local lights. It treats each Gaussian as a surfel, uses the thinnest rotated ellipsoid axis as a two-sided normal, and ray-marches a compact opacity field for soft self-occlusion. No mesh, manual light assignment, or extra camera is required. Single Override remains available when one light needs to be isolated.
- **Proxy Mesh:** assign an aligned `MeshRenderer` or `SkinnedMeshRenderer` child. The inspector automatically installs the preserved proxy backend, neutral Lit material, dedicated layer, source-camera exclusion, and screen-space capture. This is the existing proxy relighting implementation, now selected through the unified component.

Switching **Mode** never deletes the other mode's setup. In Volume mode, `Blend` crossfades between the captured appearance and the directly lit result; `Shadow Strength`, `Shadow Steps`, and `Shadow Bias` control opacity-derived self-occlusion. Use **Rebuild Gaussian Opacity Field** after replacing or permanently editing the source asset. Disabling the unified component still allows a preserved legacy proxy binding to act as a fallback.

The opacity texture belongs to one renderer, so selecting Volume mode temporarily falls back from cross-renderer merged sorting to independent draws. Animated skinning transforms the estimated surfel normals, but the opacity field remains in the source asset's local frame; rebuild it after baking permanent deformation into the asset.

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
