// SPDX-License-Identifier: MIT
using System;
using UnityEngine;

namespace Gsplat
{
    // View-depth key shared by the CPU sorters. Unity view Z is negative in front of the
    // camera, so ascending keys are far-to-near and match the native m20..m23 row transform.
    internal static class GsplatCpuDepth
    {
        internal static float ViewDepth(Vector3 point, Matrix4x4 localToView) =>
            localToView.m20 * point.x + localToView.m21 * point.y + localToView.m22 * point.z + localToView.m23;

        internal static uint ViewDepthKey(Vector3 point, Matrix4x4 localToView) =>
            DepthKey(ViewDepth(point, localToView));

        internal static uint DepthKey(float depth)
        {
            if (float.IsNaN(depth)) depth = float.PositiveInfinity;
            uint bits = unchecked((uint)BitConverter.SingleToInt32Bits(depth));
            return bits ^ ((bits & 0x80000000u) != 0 ? uint.MaxValue : 0x80000000u);
        }
    }
}
