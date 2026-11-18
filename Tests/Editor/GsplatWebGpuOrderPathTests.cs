// Copyright (c) 2026 ARLOOPA
// SPDX-License-Identifier: MIT

using Gsplat.Editor;
using NUnit.Framework;
using UnityEditor.Rendering;
using UnityEngine.Rendering;

namespace Gsplat.Tests
{
    public class GsplatWebGpuOrderPathTests
    {
        [Test]
        public void OrderBufferRequiresWebGpuWithVertexStorageBuffers()
        {
            Assert.IsTrue(GsplatUtils.UsesOrderBuffer(GraphicsDeviceType.WebGPU, 8));
            Assert.IsFalse(GsplatUtils.UsesOrderBuffer(GraphicsDeviceType.WebGPU, 0),
                "WebGPU devices without vertex storage buffers must keep the order texture.");
            Assert.IsFalse(GsplatUtils.UsesOrderBuffer(GraphicsDeviceType.OpenGLES3, 8));
            Assert.IsFalse(GsplatUtils.UsesOrderBuffer(GraphicsDeviceType.Vulkan, 8));
        }

        [Test]
        public void OrderBufferVariantsAreKeptOnlyForWebGpuTexturePath()
        {
            Assert.IsFalse(GsplatShaderVariantStripper.ShouldStrip(
                ShaderCompilerPlatform.WebGPU, orderBuffer: true, vertexBuffers: false));
            Assert.IsTrue(GsplatShaderVariantStripper.ShouldStrip(
                ShaderCompilerPlatform.WebGPU, orderBuffer: true, vertexBuffers: true));
            Assert.IsTrue(GsplatShaderVariantStripper.ShouldStrip(
                ShaderCompilerPlatform.Vulkan, orderBuffer: true, vertexBuffers: false));
            Assert.IsTrue(GsplatShaderVariantStripper.ShouldStrip(
                ShaderCompilerPlatform.Metal, orderBuffer: true, vertexBuffers: true));
            Assert.IsFalse(GsplatShaderVariantStripper.ShouldStrip(
                ShaderCompilerPlatform.D3D, orderBuffer: false, vertexBuffers: true),
                "Variants without the order-buffer keyword must never be stripped.");
            Assert.IsFalse(GsplatShaderVariantStripper.ShouldStrip(
                ShaderCompilerPlatform.WebGPU, orderBuffer: false, vertexBuffers: false),
                "WebGPU devices without vertex storage buffers need the texture-order variant.");
        }
    }
}
