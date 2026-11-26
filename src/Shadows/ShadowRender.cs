using System;
using System.Collections.Generic;
using Rhino.Geometry;

namespace ImgTools
{
    /// <summary>A traced pass: one value per pixel (0..1), rows top to bottom.</summary>
    internal sealed class TraceImage
    {
        public int Width, Height;
        public float[] Value;
        public float[] Coverage;   // share of samples that hit a surface, 0..1
        public int Triangles, Casters, Receivers;
    }

    /// <summary>
    /// The raw shadow pass, ray traced on the CPU: for each sample a primary ray finds the
    /// surface the view shows, then shadow rays per light ask whether anything that casts
    /// shadows is in the way. Value = share of light blocked (0 lit, 1 in full shadow).
    /// </summary>
    /// <remarks>
    /// Hard shadows send one ray per light. Soft shadows treat each light as a disc of the
    /// given angular size (the sun is about 0.53°) and send several rays across it, which
    /// gives penumbras: sharp where an object touches the ground, softer further away.
    /// </remarks>
    internal static class ShadowRender
    {
        /// <summary>Of grid×grid stratified rays across the light's disc, how many do not reach it.</summary>
        static int Blocked(ShadowBvh bvh, in SurfaceHit hit, Vector3d start, Vector3d l, int grid, ref Rng rng,
            double cosMax, bool formShadows, double eps, double tMax, int[] stack)
        {
            int blocked = 0;
            Rng.Basis(l, out var bu, out var bv);
            for (int gy = 0; gy < grid; gy++)
            {
                for (int gx = 0; gx < grid; gx++)
                {
                    var d = Rng.Around(l, bu, bv, (gx + rng.NextDouble()) / grid, (gy + rng.NextDouble()) / grid, cosMax, false);
                    // Part of the light below the surface: lit only in the cast-only sense.
                    if (hit.Smooth * d <= 0)
                    {
                        if (formShadows)
                            blocked++;
                        continue;
                    }
                    if (hit.Normal * d > 0 && bvh.Any(start, d, eps, tMax, ShadowBvh.Role.Casts, stack))
                        blocked++;
                }
            }
            return blocked;
        }

        /// <param name="softness">Angular diameter of the lights in degrees; 0 for hard shadows.</param>
        /// <param name="rays">Shadow rays per pixel per light when soft (spread over the anti-alias samples).</param>
        public static TraceImage Render(TraceScene scene, int samples, IList<ShadowLight> lights, bool formShadows,
            double softness, int rays, Func<bool> cancelled = null)
        {
            var bvh = scene.Bvh;
            var origin = bvh.Origin;
            double eps = scene.Eps;
            bool soft = softness > 0;
            double cosMax = Math.Cos(0.5 * softness * Math.PI / 180);
            int s = Math.Max(1, samples);
            int perSample = soft ? Math.Max(1, (rays + s * s - 1) / (s * s)) : 1;
            // Stratify the rays of a sample on a k×k grid over the light's disc.
            int grid = Math.Max(1, (int)Math.Round(Math.Sqrt(perSample)));
            perSample = grid * grid;

            var values = scene.Shade(samples, 0, (hit, stack, rng) =>
            {
                if ((hit.Role & ShadowBvh.Role.Receives) == 0)
                    return 0;
                var world = origin + hit.Point;
                var start = scene.RayStart(hit);
                double dark = 0;
                foreach (var light in lights)
                {
                    light.Towards(world, out var l, out var distance);
                    // Facing from the smooth normal, so curved surfaces turn from the light
                    // smoothly, not facet by facet.
                    if (hit.Smooth * l <= 0 || !light.Reaches(l))
                    {
                        // Turned away from the light (or outside a spot's cone): shading, not a cast shadow.
                        if (formShadows)
                            dark++;
                        continue;
                    }
                    double tMax = double.IsInfinity(distance) ? 4 * bvh.Size + hit.Point.Length : distance - 2 * eps;
                    if (!soft)
                    {
                        // Near the terminator the flat facet may still face away while the smooth
                        // surface faces the light; the facet would then block the ray. Count it lit.
                        if (hit.Normal * l > 0 && bvh.Any(start, l, eps, tMax, ShadowBvh.Role.Casts, stack))
                            dark++;
                        continue;
                    }
                    // A light seen through the same point by every ray: lit, or in full shadow.
                    // Four pilot rays across the light's quadrants tell; only points where they
                    // disagree (the penumbra) get the full set. Most of an image is not penumbra.
                    if (perSample >= 9)
                    {
                        int pilot = Blocked(bvh, hit, start, l, 2, ref rng, cosMax, formShadows, eps, tMax, stack);
                        if (pilot == 0 || pilot == 4)
                        {
                            dark += pilot / 4.0;
                            continue;
                        }
                    }
                    int blocked = Blocked(bvh, hit, start, l, grid, ref rng, cosMax, formShadows, eps, tMax, stack);
                    dark += (double)blocked / perSample;
                }
                return lights.Count == 0 ? 0 : dark / lights.Count;
            }, out var coverage, cancelled);
            if (values == null)
                return null;

            return new TraceImage
            {
                Width = scene.Width,
                Height = scene.Height,
                Value = values,
                Coverage = coverage,
                Triangles = bvh.TriangleCount,
                Casters = scene.Casters,
                Receivers = scene.Receivers,
            };
        }
    }

    /// <summary>
    /// Ambient occlusion: how much of the sky above each visible point is hidden by nearby
    /// geometry. Rays leave the surface in a cosine-weighted hemisphere; a ray that hits
    /// something within the radius occludes by (1 - distance / radius), so contact areas go
    /// dark and the gradient fades out smoothly. Value = 1 open, 0 fully occluded.
    /// </summary>
    internal static class AoRender
    {
        public static TraceImage Render(TraceScene scene, int samples, double radius, int rays, Func<bool> cancelled = null)
        {
            var bvh = scene.Bvh;
            double eps = scene.Eps;
            int s = Math.Max(1, samples);
            int perSample = Math.Max(1, (rays + s * s - 1) / (s * s));
            int grid = Math.Max(1, (int)Math.Round(Math.Sqrt(perSample)));
            perSample = grid * grid;

            var values = scene.Shade(samples, 1, (hit, stack, rng) =>
            {
                var start = scene.RayStart(hit);
                double occlusion = 0;
                Rng.Basis(hit.Smooth, out var bu, out var bv);
                for (int gy = 0; gy < grid; gy++)
                {
                    for (int gx = 0; gx < grid; gx++)
                    {
                        var d = Rng.Around(hit.Smooth, bu, bv, (gx + rng.NextDouble()) / grid, (gy + rng.NextDouble()) / grid, 0, true);
                        // Directions under the flat facet would hit the facet itself: mirror them up.
                        double below = hit.Normal * d;
                        if (below <= 0)
                            d -= 2 * below * hit.Normal;
                        int f = bvh.Nearest(start, d, eps, radius, ShadowBvh.Role.Casts, stack, out var t);
                        if (f >= 0)
                            occlusion += 1 - t / radius;
                    }
                }
                return 1 - occlusion / perSample;
            }, out var coverage, cancelled);
            if (values == null)
                return null;

            return new TraceImage
            {
                Width = scene.Width,
                Height = scene.Height,
                Value = values,
                Coverage = coverage,
                Triangles = bvh.TriangleCount,
                Casters = scene.Casters,
                Receivers = scene.Receivers,
            };
        }
    }
}
