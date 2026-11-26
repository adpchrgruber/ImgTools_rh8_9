using System;
using System.Globalization;
using Rhino;

namespace ImgTools
{
    /// <summary>
    /// Ambient occlusion options shared by AOCapture and the live AO view. The radius belongs to
    /// the scene, so it is saved in the document; the rest with Rhino's settings.
    /// </summary>
    internal static class AoSettings
    {
        const string Section = "ImgTools.AO";

        static PersistentSettings S => ImgToolsPlugin.Instance?.Settings;

        /// <summary>Rays per pixel. 16 with denoising looks like 64 without, at a quarter of the time.</summary>
        public static int Rays
        {
            get => S?.GetInteger("AO.Rays", 16) ?? 16;
            set => S?.SetInteger("AO.Rays", value);
        }

        /// <summary>True (default): smooth the grain with an edge-aware filter.</summary>
        public static bool Denoise
        {
            get => S?.GetBool("AO.Denoise", true) ?? true;
            set => S?.SetBool("AO.Denoise", value);
        }

        public static int AntiAlias
        {
            get => S?.GetInteger("AO.AntiAlias", 2) ?? 2;
            set => S?.SetInteger("AO.AntiAlias", value);
        }

        /// <summary>True (default): white open, black occluded.</summary>
        public static bool OccludedIsBlack
        {
            get => S?.GetBool("AO.OccludedIsBlack", true) ?? true;
            set => S?.SetBool("AO.OccludedIsBlack", value);
        }

        /// <summary>The document's radius in model units; a suggestion from the scene's size until set.</summary>
        public static double Radius(RhinoDoc doc)
        {
            if (doc != null && double.TryParse(doc.Strings.GetValue(Section, "Radius"), NumberStyles.Float, CultureInfo.InvariantCulture, out var r) && r > 0)
                return r;
            return SuggestRadius(doc);
        }

        public static void SetRadius(RhinoDoc doc, double radius)
        {
            if (doc != null && radius > 0)
                doc.Strings.SetString(Section, "Radius", radius.ToString("R", CultureInfo.InvariantCulture));
        }

        /// <summary>A starting radius: 5% of the size of everything shown, rounded to 1, 2 or 5 × 10ⁿ.</summary>
        public static double SuggestRadius(RhinoDoc doc)
        {
            var box = doc?.Objects.BoundingBoxVisible ?? Rhino.Geometry.BoundingBox.Empty;
            if (!box.IsValid)
                return 1;
            var r = 0.05 * box.Diagonal.Length;
            double p = Math.Pow(10, Math.Floor(Math.Log10(r)));
            double m = r / p;
            return (m < 1.5 ? 1 : m < 3.5 ? 2 : 5) * p;
        }
    }
}
