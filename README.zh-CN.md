# UnitySplats

[![Version](https://img.shields.io/badge/version-v1.1.0-blue.svg)](./CHANGELOG.md)
[![Unity](https://img.shields.io/badge/Unity-6000.0%2B-black.svg)](https://unity.com/)
[![License](https://img.shields.io/badge/license-MIT-green.svg)](./LICENSE.md)

[English](README.md) · 简体中文 · [日本語](README.ja.md)

UnitySplats 是一个跨平台 Unity 6 包，用于导入、加载和渲染[三维高斯泼溅](https://repo-sam.inria.fr/fungraph/3d-gaussian-splatting/)（3DGS）内容。它支持内置渲染管线、URP 和 HDRP，并在运行时选择 GPU 或可移植 CPU 排序路径。

![在 Unity 中渲染的高斯泼溅](Documentation~/Images/splat.png)

## 项目沿革与署名

UnitySplats 是 [wuyize25/gsplat-unity](https://github.com/wuyize25/gsplat-unity) 的修改及大幅扩展分支。原始 Unity 渲染器的版权归 Yize Wu 所有（Copyright (c) 2025），并以 MIT 许可证发布。

本包还采用了 [PlayCanvas Engine](https://github.com/playcanvas/engine) 的部分高斯泼溅架构和格式行为，包括 PLY、SOG 和 GLB 实现中使用的概念。本 Unity 项目参考的 PlayCanvas 版本为 `v2.21.0-beta.14`（`d5fe88878e338936fe763bbce1a58bc315e89cbe`）。PlayCanvas Engine 的版权归 PlayCanvas Ltd 所有（Copyright (c) 2011-2026），并以 MIT 许可证发布。

UnitySplats 是由 ARLOOPA 维护的 Unity 实现。它不是 PlayCanvas 官方产品，也不包含 PlayCanvas JavaScript 引擎。现有上游版权和许可证声明保留在源码及[第三方声明](./Third%20Party%20Notices.md)中。

## 功能

- 导入并在运行时加载标准二进制 3DGS PLY 和 PlayCanvas 压缩 PLY。
- 支持捆绑及解包的 PlayCanvas SOG v1/v2。
- 支持 SPZ v1-v4，包括使用 Zstandard 压缩属性的 SPZ v4。
- 使用 `KHR_gaussian_splatting` 支持高斯泼溅 GLB。
- 从文件路径、字节数组和流中进行运行时加载。
- 支持 Spark 打包和未压缩的 Unity 资源表示形式。
- 异步上传到 GPU，并可选择在上传期间进行渲染。
- 支持正交相机、MSAA、裁切、活动源范围和 XR 集成。
- 在运行时为 Direct3D、Vulkan、Metal、OpenGL 和 WebGL 2 选择图形后端。

## 支持的格式

| 格式 | 导入 | 运行时加载 | 说明 |
| --- | --- | --- | --- |
| `.ply` | 是 | 是 | 二进制小端标准 3DGS 和 PlayCanvas 压缩 PLY |
| `.sog` | 是 | 是 | 捆绑的 SOG v1/v2 |
| 解包的 SOG `meta.json` | 是 | 是 | 需要完整的 SOG 目录 |
| `.spz` | 是 | 是 | SPZ v1-v4，SH 阶数 0-4 |
| `.glb` | 是 | 是 | 需要 `KHR_gaussian_splatting`；请为每个文件选择 UnitySplats 导入器 |

导入器的 `Auto` 坐标模式对 GLB 使用 glTF 的 LUF 坐标系，对 PLY、SOG 和 SPZ 使用常见的 3DGS/OpenGL RUB 坐标系，并将数据转换为 Unity RUF。

## 平台与图形 API 支持

UnitySplats 会在运行时选择速度最快的兼容排序和渲染路径。

| 平台 | 图形 API | 排序/渲染路径 |
| --- | --- | --- |
| Windows | Direct3D 12、Vulkan | 满足 wave/subgroup 要求时使用 GPU 基数排序，否则回退到 CPU |
| Windows | Direct3D 11、OpenGL Core | 异步 CPU 计数排序和 GPU 实例化渲染 |
| Linux | Vulkan、OpenGL Core | 在兼容的 Vulkan 设备上使用 GPU 基数排序，否则回退到 CPU |
| Android | Vulkan | 支持时使用 GPU 基数排序，否则回退到 CPU |
| Android | OpenGL ES 3.1 | 异步 CPU 排序和可移植的采样纹理绘制路径 |
| macOS、iOS | Metal | 支持时使用 GPU 基数排序，否则回退到 CPU |
| Web | WebGL 2 | 主线程 CPU 排序和可移植的采样纹理绘制路径 |

OpenGL ES 路径使用 RGBA8 字节打包纹理和浮点采样纹理，而不是顶点阶段存储缓冲区。这样可以避免部分移动 GPU 虽然报告支持 SSBO，却拒绝 Unity 生成的 `BufferBlock` 绑定时出现的驱动故障。Android 所支持的最低 OpenGL 版本为 OpenGL ES 3.1。

维护者已在多款使用 OpenGL ES、Vulkan 和 Metal 的 Android 与 Apple 设备上完成测试，包括 Samsung 和 HONOR Android 设备以及 Meta Quest 3 和 Meta Quest 3S 头显。Samsung 和 HONOR 设备也用于验证 GLES 兼容路径。设备驱动仍然存在差异，因此列出的 API 代表支持目标，并不保证适用于每种 GPU/操作系统组合。欢迎通过 GitHub Issues 提交硬件报告。

WebGL 2 路径不会创建计算缓冲区或结构化图形缓冲区。它将泼溅数据和绘制顺序存储在采样纹理中，并在 Unity 主线程上运行计数排序。Web 构建应使用 Spark 压缩并保持适中的泼溅数量：由于浏览器内存限制和同步排序，大型未压缩资源的成本会显著高于原生平台。WebGL 不支持全局合并和计算裁切。

### 排序限制

GPU 基数排序路径需要 wave/subgroup 操作和兼容的计算工作组限制。CPU 回退路径会保留 GPU 实例化绘制，但会禁用全局多渲染器合并和计算裁切。它为每个渲染器保留一个排序顺序，并优先使用游戏相机，因此同时存在且视角差异明显的相机会共享该顺序。

默认启用跨渲染器排序。当所有活动渲染器均已完成上传、采用 Spark 打包、使用同一 GameObject 层、保持默认渲染顺序，并符合打包渲染器/索引限制时，它会对重叠渲染器中的泼溅进行全局深度排序，并将其作为一个合并场景绘制。可在 `Project Settings > Gsplat` 中进行配置。其他配置会回退到单独的绘制调用。

## 要求

- Unity 6000.0 或更高版本。随附的开发项目使用 Unity 6000.3.9f1。
- `com.unity.mathematics` 1.3.2 或更高版本。
- 使用 [`com.netpyoung.webp`](https://github.com/netpyoung/unity.webp) 0.3.22 解码 SOG 图片。
- 上表所列的受支持图形 API。

## 安装

### 从 Git URL 安装并自动解析 Unity.WebP

UnitySplats 已将 Unity.WebP 0.3.22 声明为包依赖项。若要让 Unity 自动解析该依赖，请添加面向 `com.netpyoung` 作用域的 OpenUPM 注册表和 UnitySplats Git URL，并将它们写入目标项目的 `Packages/manifest.json`：

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

请将这些条目与现有注册表及依赖项合并。Unity 解析 UnitySplats 时便会自动安装 Unity.WebP。

### 从 Git URL 安装两个包

Unity Package Manager 无法将一个 Git 包解析为另一个 Git 包的传递依赖项。请先安装 Unity.WebP：

1. 打开 `Window > Package Manager`。
2. 单击 `+`，然后选择 `Install package from git URL...`。
3. 安装 Unity.WebP：

   ```text
   https://github.com/netpyoung/unity.webp.git?path=unity_project/Assets/unity.webp#0.3.22
   ```

4. 重复上述过程以安装 UnitySplats：

   ```text
   https://github.com/arloopa/UnitySplats.git
   ```

这将安装仓库当前的默认分支。

### 从 `manifest.json` 安装两个 Git 依赖项

将两个直接依赖项都添加到目标项目的 `Packages/manifest.json`：

```json
{
  "dependencies": {
    "com.netpyoung.webp": "https://github.com/netpyoung/unity.webp.git?path=unity_project/Assets/unity.webp#0.3.22",
    "com.arloopa.unitysplats": "https://github.com/arloopa/UnitySplats.git"
  }
}
```

将项目的其他依赖项保留在同一对象中。

### 从发布归档安装

下载 UnitySplats 发布 ZIP，并将其解压到目标项目 `Assets` 文件夹之外。配置上文所述的 OpenUPM 注册表，使用 Git URL 安装 Unity.WebP，或添加单独下载的 Unity.WebP 包。然后打开 `Window > Package Manager`，选择 `+ > Install package from disk...`，再选择 UnitySplats 的 `package.json`。

UnitySplats 源码归档不捆绑 Unity.WebP。Unity 只接受使用方项目 `Packages/manifest.json` 中的 Git 依赖 URL，因此包到包的自动解析需要 OpenUPM 之类的作用域注册表。

## 渲染管线设置

- **内置渲染管线：**无需额外的渲染功能。
- **URP：**打开活动的 Universal Renderer Data 资源并添加 `Gsplat URP Feature`。同时支持 Unity 6 RenderGraph 和兼容模式。
- **HDRP：**添加 Custom Pass Volume 和 `Gsplat HDRP Pass`，并将后者置于 `Before Transparent` 注入点。

为获得最佳性能，Android 应优先使用 Vulkan，Windows 应优先使用 Vulkan 或 Direct3D 12。Direct3D 11 和 OpenGL 会自动选择可移植 CPU 排序器。如果 Android 使用 OpenGL，请在 Player Settings 中要求 OpenGL ES 3.1。

## 导入并渲染泼溅

1. 将 `.ply`、`.sog`、`.spz` 或高斯泼溅 `.glb` 文件复制或拖入项目的 `Assets` 文件夹。
   对于 GLB，请选择该文件，然后选择 `Assets > UnitySplats > Choose GLB Importer... > UnitySplats (Gaussian Splat)`。也可在文件 Inspector 的 Unity Importer 下拉菜单中选择 `GsplatImporter`。UnitySplats 是可选的 GLB 导入器，因此可以与 glTFast 以及其他处理普通 GLB 模型的包共存。
   如果另外两个已安装的包都将自身注册为默认 GLB 导入器，Unity 会继续报告这两个包之间存在冲突，直到其中一个被禁用或改为覆盖导入器；这不会妨碍为高斯 GLB 明确选择 UnitySplats。
2. 选择已导入的 `GsplatAsset`。建议使用 `Spark` 打包表示形式；如果精确的项目内数值比内存和上传速度更重要，请选择 `Uncompressed`。
3. 将导入的资源从 Project 窗口拖入 Hierarchy 或 Scene 视图。UnitySplats 会自动创建带有 `GsplatRenderer` 的 GameObject、分配资源，并将其局部 Z 轴旋转设置为 180 度。

也可以手动创建 GameObject，添加 `Gsplat Renderer` 组件，并将导入的资源分配给其 `Gsplat Asset` 字段。

在 Scene 视图中，单击泼溅变换后资源边界内的任意位置即可选择其 GameObject，无需碰撞体。Unity 常规的 Shift/Ctrl 选择修饰键仍然有效。选择多个 `GsplatRenderer` GameObject 后，可以对它们共有的 Inspector 属性进行多对象编辑，也可以一次性适配所有附加的 `BoxCollider` 组件。

当两个或更多 Spark 渲染器重叠时，请保持启用 `Project Settings > Gsplat` 中的 **Enable Cross-Renderer Sorting**。在受支持的 Vulkan、Direct3D 12 和 Metal 设备上，这会为每个泼溅创建一个共享深度顺序，防止整个捕获对象随着相机移动而在彼此前后跳换。OpenGL、WebGL、Direct3D 11 和不受支持的 GPU 配置所使用的可移植 CPU 排序器会保留单独的绘制调用，无法提供真正的合并顺序。

大多数 3DGS 内容都在伽马空间中训练。`Gamma To Linear` 在 `GsplatRenderer` 上默认启用；如果源颜色已按项目的线性工作流创作，请将其禁用。否则，在 alpha 混合前进行转换可能会降低视觉精度。

### 解包的 SOG

选择 SOG 的 `meta.json`，然后选择 `Assets > UnitySplats > Import unpacked SOG (Spark)`。请将所有引用的文件与元数据放在一起。

## 运行时加载

运行时加载器返回内存中的 `GsplatAsset`，并且必须在主线程上创建/分配 Unity 对象。

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

`LoadFile` 接受文件系统路径。对于 Android APK `StreamingAssets`、HTTP(S) 和其他基于 URI 的来源，应使用 `UnityWebRequest` 读取，然后传给 `GsplatRuntimeLoader.Load(byte[], ...)`。其他输入形式可使用 `Load(Stream, ...)` 或 `LoadUnpackedSog(...)`。

Package Manager 中包含可选的 **Runtime PLY Loading** 示例，用于演示字节数组加载和运行时资源清理。

## 活动源范围

`GsplatRenderer.SetActiveRanges` 可在不创建其他资源的情况下选择源泼溅范围的精确并集。范围采用半开区间 `[Offset, Offset + Count)` 语义，并在重叠或相接时进行规范化。

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

这些方法会更新 Unity GPU 缓冲区，因此必须在主线程上运行。启用显式范围时会绕过计算裁切。

## 格式说明

- PLY 输入必须是二进制小端格式。
- SOG v1/v2 使用 Unity.WebP 及其平台 libwebp 二进制文件。
- 包含 `SPZ_ADOBE_coordinate_system` 的 SPZ 文件会自动使用该描述符。
- GLB 图元必须声明 `KHR_gaussian_splatting` 和所需的访问器。UnitySplats 也能识别 `splat-transform` 2.6.0 及更早版本写入的旧版对数缩放编码。通过多个节点变换实例化泼溅网格的场景图不会被展平为单个资源。
## XR 支持

| XR 渲染模式 | 内置 | URP | HDRP |
| --- | --- | --- | --- |
| 多通道 | 是 | 是 | 否 |
| 单通道实例化 | 否 | 是 | 否 |

## 文档

- [实现细节](./Documentation~/Implementation%20Details.md)
- [变更日志](./CHANGELOG.md)
- [第三方声明](./Third%20Party%20Notices.md)

## 许可证

UnitySplats 以 [MIT 许可证](./LICENSE.md)发布。

- ARLOOPA 修改和打包：Copyright (c) 2026 ARLOOPA。
- 原始 `gsplat-unity` 工作：Copyright (c) 2025 Yize Wu，MIT License。
- 衍生自 PlayCanvas 的部分：Copyright (c) 2011-2026 PlayCanvas Ltd，MIT License。

其他源码组件保留其原始声明。完整署名列表请参阅[第三方声明](./Third%20Party%20Notices.md)，其中包括 UnityGaussianSplatting、GPUSorting、Spark、SPZ、ZstdSharp、Unity.WebP 和 libwebp。

## 致谢

- [PlayCanvas Engine](https://github.com/playcanvas/engine)
- [wuyize25/gsplat-unity](https://github.com/wuyize25/gsplat-unity)
- [aras-p/UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting)
- [b0nes164/GPUSorting](https://github.com/b0nes164/GPUSorting)
- [sparkjsdev/spark](https://github.com/sparkjsdev/spark)
- [nianticlabs/spz](https://github.com/nianticlabs/spz)
- [oleg-st/ZstdSharp](https://github.com/oleg-st/ZstdSharp)
- [netpyoung/unity.webp](https://github.com/netpyoung/unity.webp)
