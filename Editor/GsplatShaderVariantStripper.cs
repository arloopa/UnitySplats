// Copyright (c) 2026 ARLOOPA
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using UnityEditor.Build;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace Gsplat.Editor
{
    /// <summary>
    /// GSPLAT_ORDER_BUFFER variants are drawn only by WebGPU players, and only together with
    /// sampled splat textures (the structured-buffer path already reads _OrderBuffer). Stripping
    /// every other combination keeps Gsplat/Standard at its previous variant count elsewhere.
    /// </summary>
    sealed class GsplatShaderVariantStripper : IPreprocessShaders
    {
        static readonly ShaderKeyword s_orderBuffer = new(GsplatUtils.k_OrderBufferKeyword);
        static readonly ShaderKeyword s_vertexBuffers = new(GsplatUtils.k_VertexBufferKeyword);

        public int callbackOrder => 0;

        public void OnProcessShader(Shader shader, ShaderSnippetData snippet,
            IList<ShaderCompilerData> data)
        {
            for (int i = data.Count - 1; i >= 0; --i)
            {
                ShaderKeywordSet keywords = data[i].shaderKeywordSet;
                if (ShouldStrip(data[i].shaderCompilerPlatform, keywords.IsEnabled(s_orderBuffer),
                        keywords.IsEnabled(s_vertexBuffers)))
                    data.RemoveAt(i);
            }
        }

        internal static bool ShouldStrip(ShaderCompilerPlatform platform, bool orderBuffer,
            bool vertexBuffers) =>
            orderBuffer && (platform != ShaderCompilerPlatform.WebGPU || vertexBuffers);
    }
}
