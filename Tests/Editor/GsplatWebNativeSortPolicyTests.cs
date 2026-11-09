// SPDX-License-Identifier: MIT
using System.Reflection;
using System.Runtime.InteropServices;
using NUnit.Framework;

namespace Gsplat.Tests
{
    public class GsplatWebNativeSortPolicyTests
    {
        [Test]
        public void NativeTimingLayoutMatchesFourDoubles()
        {
            Assert.AreEqual(32, Marshal.SizeOf<GsplatWebNativeSort.NativeTimings>());
        }

        [Test]
        public void EditorNeverInvokesTheWebNativeBackend()
        {
            var type = typeof(GsplatSorter).Assembly.GetType("Gsplat.GsplatWebNativeSort", true);
            var requested = type.GetProperty("Requested", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(requested);
            Assert.IsFalse((bool)requested.GetValue(null));
        }
    }
}
