// Copyright (c) 2026 ARLOOPA
// SPDX-License-Identifier: MIT

using System;
using Gsplat.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Gsplat.Tests
{
    public class GsplatEditorFeatureTests
    {
        [Test]
        public void RendererInspectorSupportsMultiObjectEditing()
        {
            Assert.IsTrue(Attribute.IsDefined(typeof(GsplatRendererEditor),
                typeof(CanEditMultipleObjects), true));
        }

        [Test]
        public void ScenePickerIntersectsTransformedAssetBounds()
        {
            var transform = Matrix4x4.TRS(
                new Vector3(3f, -2f, 7f),
                Quaternion.Euler(15f, 35f, -20f),
                new Vector3(2f, 0.5f, 3f));
            var bounds = new Bounds(new Vector3(0.25f, -0.5f, 0.75f),
                new Vector3(4f, 2f, 6f));
            Vector3 worldCenter = transform.MultiplyPoint3x4(bounds.center);
            Vector3 direction = (worldCenter - new Vector3(-12f, 8f, -20f)).normalized;
            var hitRay = new Ray(worldCenter - direction * 50f, direction);

            Assert.IsTrue(GsplatScenePicker.IntersectLocalBounds(
                hitRay, transform.inverse, bounds, out float distance));
            Assert.Greater(distance, 0f);

            var missRay = new Ray(hitRay.origin + Vector3.up * 100f, hitRay.direction);
            Assert.IsFalse(GsplatScenePicker.IntersectLocalBounds(
                missRay, transform.inverse, bounds, out _));
        }

        [Test]
        public void NewSettingsEnableCrossRendererSorting()
        {
            var settings = ScriptableObject.CreateInstance<GsplatSettings>();
            try
            {
                Assert.IsTrue(settings.EnableGlobalSort);
                Assert.AreEqual(GsplatSettings.DefaultCpuNearExactDistance, settings.CpuNearExactDistance);
                Assert.AreEqual(40f, settings.CpuNearExactDistance);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

    }
}
