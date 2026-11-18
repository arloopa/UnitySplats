# Changelog

All notable changes to UnitySplats are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Opt-in native pthread CPU sorting for Unity 6000+ WebGPU. An internal request cap is limited by Unity's available precreated workers; it is not a public worker-count setting and does not increase Unity's allocation. The existing main-thread sorter remains the fallback when native sorting is unavailable.
- Opt-in CPU sort optimization for fully uploaded static uncompressed/Spark sources: octree visibility selection followed by one global near-exact/far-bucket view-depth sort. The previous completed order remains drawable while the next job runs. When multiple compatible cameras share a renderer, frustum culling is disabled and the complete source-ID set is used.
- `Near Exact Distance` (default 40 world units) controls the exact/far split. Nearby selected points use full 32-bit depth keys; far points use the high 18 bits of the sortable depth key. Setting the distance to `0` sorts all selected points exactly without disabling culling.
- Opt-in `Show Debug Log` timing summaries for CPU sorting and uploads, including selected-point count, exact near-point count, job age and view-depth-axis change.

### Changed

- WebGPU CPU draw order now uses a StructuredBuffer and can upload only the visible prefix, directly from native output when native sorting is active. Splat data remains in sampled textures. This storage choice applies even with the new features disabled; WebGL retains its RGBA8 order texture.

## [2.0.0] - 2026-10-21

### Added

- Merged proxy-mesh and volume relighting into one root-level **Gaussian Relighting** component with a **Volume** (default) / **Proxy Mesh** mode selector. The existing proxy binding remains as a hidden compatibility backend and is not removed from old scenes.
- Added the proxy-free **Volume** mode, which derives a two-sided surfel normal from each Gaussian's thinnest rotated axis, automatically combines up to four affecting directional/point/spot lights, and estimates soft self-occlusion through a renderer-local opacity field. Local lights use Unity-style inverse-square and range attenuation; an optional single-light override supports focused comparisons.
- Added a rebuildable opacity-field inspector and editor tests for the unified lighting component. Existing proxy-mesh Gaussian Relighting remains available through Proxy Mesh mode and as a disabled-controller fallback.

### Fixed

- Fixed Volume relighting re-scanning every splat and allocating a new opacity volume on every frame when its opacity field could not be built (for example an asset without CPU-side splat data or a non-finite splat center). A failed build is now kept until its inputs change, and lighting continues without self-occlusion instead of turning off.
- Reduced Volume relighting memory: the opacity field is built in native scratch memory that is freed immediately (previously up to 18 MB of managed garbage per rebuild), smoothing runs in place, and renderers that draw the same asset with the same field settings share one 3D texture.
- Reduced Volume relighting GPU cost: lights that do not reach a splat no longer march its shadow ray, the march stops once a ray is fully occluded, opacity texels decode without a per-sample `log()`, and per-renderer constants are computed on the CPU. Devices without R8 3D-texture support fall back to lighting without self-occlusion.

## [1.2.0] - 2026-08-18

### Fixed

- Fixed incorrect splat compositing on Direct3D 11 when a large camera-depth range caused nearby splats to collide in the former 16-bit CPU sort. The fallback now preserves full 32-bit float depth order.
- Fixed Direct3D 11 Scene views consuming an order sorted from the Main camera. The CPU fallback now follows the active Scene/Game viewport and discards in-flight results when the selected viewport changes.
- Matched the GPU sort's stable handling of equal view depths in the CPU fallback, preventing localized ordering artifacts at camera angles where SPARK half-float centers produce tied depth keys.

## [1.1.0] - 2026-07-22

### Added

- Added Scene-view selection of `GsplatRenderer` GameObjects through transformed splat bounds without requiring a collider.
- Added multi-object editing to the renderer inspector, including mixed values and batch `BoxCollider` fitting.
- Added cross-renderer sorting controls and compatibility guidance to `Project Settings > Gsplat`.
- Added hover tooltips for every editable `GsplatRenderer` inspector control.

### Changed

- Enabled cross-renderer per-splat sorting by default for compatible Spark renderers; existing project settings migrate once while preserving subsequent manual changes.

## [1.0.1] - 2026-07-22

### Fixed

- Fixed Unity 6.5 compilation by replacing deprecated 32-bit `GetInstanceID()` cache keys with direct Unity object references, compatible with Unity's 64-bit `EntityId` transition.

### Documentation

- Added an OpenUPM installation option that lets Unity resolve the declared Unity.WebP dependency automatically.

## [1.0.0] - 2026-07-22

### Added

- Added PlayCanvas standard/compressed PLY, SOG v1/v2, SPZ v1-v4, and Gaussian-splat GLB decoding.
- Added editor import and runtime byte/stream loading with automatic coordinate conversion.
- Added asynchronous CPU sorting for D3D11, OpenGL Core, and OpenGL ES 3.1 while retaining GPU radix sorting on supported D3D12, Vulkan, and Metal devices.
- Added a texture-backed OpenGL draw path for devices that report zero vertex-stage storage-buffer slots, preventing `Gsplat/Standard` GLSL link failures on affected GLES drivers.
- Added Unity 6 URP RenderGraph and compatibility-mode integration.
- Added 3DGS antialiasing opacity compensation and corrected degree-4 spherical-harmonic evaluation.
- Added SPZ coordinate-system extension parsing, including rotated Adobe coordinate frames.
- Added exact per-renderer active source ranges, including async-upload prefix safety and mapped CPU/GPU sorting. Explicit ranges bypass compute cutouts until cleared.
- Added Project-to-Hierarchy and Project-to-Scene drag-and-drop creation of configured `GsplatRenderer` GameObjects.
- Set Project-to-Hierarchy and Project-to-Scene drag-created splats to 180 degrees local Z rotation.
- Added an experimental WebGL 2 renderer using sampled splat/order textures and main-thread CPU sorting, with no compute or structured-buffer allocation.
- Added optional global depth sorting across multiple `GsplatRenderer` instances. Global sorting supports Spark compression and falls back to per-renderer sorting when its compatibility requirements are not met. ([upstream #28](https://github.com/wuyize25/gsplat-unity/pull/28))
- Added runtime PLY loading from byte arrays. ([upstream #34](https://github.com/wuyize25/gsplat-unity/pull/34))
- Added automatic `BoxCollider` fitting from imported splat bounds, with an inspector action for manual refitting.
- Added a per-file Gaussian-splat GLB importer override and `Assets > UnitySplats > Choose GLB Importer...` so UnitySplats can coexist with general-purpose GLB importers.
- Added an optional Package Manager sample demonstrating runtime PLY byte loading and asset cleanup.

### Changed

- Rebranded the package as UnitySplats and changed its Unity Package Manager identifier from `wu.yize.gsplat` to `com.arloopa.unitysplats`.
- Changed the OpenGL packed-data fallback to RGBA8 byte textures so Mali devices that reject sampled `R32_UInt` textures can render.
- Enabled the texture-backed draw path on every OpenGL ES device because some Honor/Mali drivers advertise vertex SSBO slots but still reject Unity's generated buffer bindings.
- Enabled `GammaToLinear` by default and added a one-time serialized migration that preserves later manual changes.

### Fixed

- Hardened mobile portability with 256-thread compute kernels, non-subnormal mesh instance IDs, and cross-API-safe structured-buffer layouts.
- Made asynchronous GPU uploads resource-owned and cancellation-safe, and fixed renderer/global-buffer release paths.
- Prevented global-merge buffer aliasing for four or more renderers and preserved layer, queue, and packed-index constraints through automatic fallback.
- Released partially created GPU resources when runtime capability checks fail.
- Added compatibility decoding for logarithmic GLB scales emitted by `splat-transform` 2.6.0 and older while preserving strict `KHR_gaussian_splatting` validation for current exporters.
- Fixed PLY header parsing to count only vertex element properties, including files exported by Apple's ml-sharp. ([upstream #33](https://github.com/wuyize25/gsplat-unity/pull/33))
- Fixed URP compilation on Unity 6000.4 and newer, where the legacy `ScriptableRenderPass.Execute` override was removed in favor of RenderGraph.
- Fixed Project-to-Hierarchy drag-and-drop on Unity 6000.4 and newer by using the 64-bit `EntityId` handler instead of the obsolete 32-bit `InstanceID` delegate.

[unreleased]: https://github.com/arloopa/UnitySplats/compare/v2.0.0...HEAD
[2.0.0]: https://github.com/arloopa/UnitySplats/compare/v1.2.0...v2.0.0
[1.2.0]: https://github.com/arloopa/UnitySplats/compare/v1.1.0...v1.2.0
[1.1.0]: https://github.com/arloopa/UnitySplats/compare/v1.0.1...v1.1.0
[1.0.1]: https://github.com/arloopa/UnitySplats/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/arloopa/UnitySplats/releases/tag/v1.0.0
