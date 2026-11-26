using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace ImgTools
{
    /// <summary>A surface point a primary ray found. Positions are relative to the BVH origin.</summary>
    internal struct SurfaceHit
    {
        public Vector3d Point;
        public Vector3d Normal;   // flat facet normal, facing the camera
        public Vector3d Smooth;   // interpolated vertex normal, same side as Normal
        public double Lift;       // how far secondary rays start off curved surfaces
        public ShadowBvh.Role Role;
    }

    /// <summary>
    /// The scene of a view for CPU ray tracing: its exact camera (from a probe capture, as
    /// ZDepthCapture uses), the triangles of every shown object in a BVH, and the clipping
    /// planes. Shared by the shadow and ambient occlusion passes.
    /// </summary>
    internal sealed class TraceScene
    {
        public DepthCamera Camera;
        public ShadowBvh Bvh;
        public (Vector3d n, double d)[] Clips;
        public double Eps;
        public int Casters, Receivers;
        public int Width => Camera.Width;
        public int Height => Camera.Height;

        public static TraceScene Build(RhinoDoc doc, RhinoView view, int width, int height)
        {
            var camera = DepthCapture.Probe(ImgToolsPlugin.Instance.DepthConduit, view, width, height, out var drawn);
            if (camera == null)
                return null;
            var items = CollectItems(doc, drawn, out int casters, out int receivers);
            var bvh = new ShadowBvh(items);
            return new TraceScene
            {
                Camera = camera,
                Bvh = bvh,
                Clips = ClipsFor(doc, view.ActiveViewport, bvh.Origin),
                Eps = 1e-6 * bvh.Size,
                Casters = casters,
                Receivers = receivers,
            };
        }

        /// <summary>
        /// Triangles with their roles. Visible: the given objects (what the view draws; all
        /// shown objects when null). Casters: every shown object that casts shadows, also those
        /// outside the frame, whose shadows can still fall into it. Must run on the UI thread.
        /// </summary>
        public static List<(DepthItem, ShadowBvh.Role)> CollectItems(RhinoDoc doc, IEnumerable<RhinoObject> visible,
            out int casters, out int receivers)
        {
            var settings = new ObjectEnumeratorSettings { HiddenObjects = false, LockedObjects = true, IncludeLights = false, IncludeGrips = false, VisibleFilter = true };
            var shown = doc.Objects.GetObjectList(settings).Where(o => !o.IsInstanceDefinitionGeometry).ToList();
            var roles = new Dictionary<uint, (RhinoObject Obj, ShadowBvh.Role Role)>();
            foreach (var obj in visible ?? shown)
                roles[obj.RuntimeSerialNumber] = (obj, ShadowBvh.Role.Visible);
            foreach (var obj in shown)
            {
                if (!obj.Attributes.CastsShadows)
                    continue;
                roles.TryGetValue(obj.RuntimeSerialNumber, out var r);
                roles[obj.RuntimeSerialNumber] = (obj, r.Role | ShadowBvh.Role.Casts);
            }
            var items = new List<(DepthItem, ShadowBvh.Role)>();
            casters = receivers = 0;
            foreach (var (obj, role0) in roles.Values)
            {
                var role = role0;
                if ((role & ShadowBvh.Role.Visible) != 0 && obj.Attributes.ReceivesShadows)
                {
                    role |= ShadowBvh.Role.Receives;
                    receivers++;
                }
                if ((role & ShadowBvh.Role.Casts) != 0)
                    casters++;
                foreach (var item in DepthScene.Collect(doc, new[] { obj }))
                    items.Add((item, role));
            }
            return items;
        }

        /// <summary>The viewport's clipping planes as (normal, offset) relative to origin: keep n.p + d >= 0.</summary>
        public static (Vector3d n, double d)[] ClipsFor(RhinoDoc doc, RhinoViewport viewport, Point3d origin) =>
            DepthCapture.WorldClippingPlanes(doc, viewport)
                .Select(p => (n: p.Normal, d: (origin - p.Origin) * p.Normal))
                .ToArray();

        /// <summary>
        /// Runs <paramref name="shade"/> for s×s samples per pixel that hit a surface and
        /// averages: value[i] = mean over all samples (misses count as <paramref name="miss"/>).
        /// shade gets the hit, the sample index within the pixel, a per-thread stack and a
        /// random generator seeded by pixel and sample, so images are repeatable.
        /// </summary>
        /// <remarks>Returns null when <paramref name="cancelled"/> turns true before the image is done.</remarks>
        public float[] Shade(int samples, double miss, Func<SurfaceHit, int[], Rng, double> shade, out float[] coverage,
            Func<bool> cancelled = null)
        {
            int width = Width, height = Height;
            var values = new float[width * height];
            var cover = new float[width * height];
            int s = Math.Max(1, samples);
            double inv = 1.0 / (s * s);
            var result = Parallel.For(0, height, () => new int[Bvh.StackSize], (y, state, stack) =>
            {
                if (cancelled != null && cancelled())
                {
                    state.Stop();
                    return stack;
                }
                for (int x = 0; x < width; x++)
                {
                    double sum = 0, hits = 0;
                    for (int sy = 0; sy < s; sy++)
                    {
                        for (int sx = 0; sx < s; sx++)
                        {
                            double px = x + (sx + 0.5) / s, py = y + (sy + 0.5) / s;
                            if (!Primary(px, py, stack, out var hit))
                            {
                                sum += miss;
                                continue;
                            }
                            hits++;
                            var rng = new Rng((uint)(y * width + x), (uint)(sy * s + sx));
                            sum += shade(hit, stack, rng);
                        }
                    }
                    int i = y * width + x;
                    values[i] = (float)(sum * inv);
                    cover[i] = (float)(hits * inv);
                }
                return stack;
            }, _ => { });
            coverage = cover;
            return result.IsCompleted ? values : null;
        }

        /// <summary>
        /// The visible surface at a pixel position: nearest hit inside the view's near/far
        /// range and on the kept side of every clipping plane.
        /// </summary>
        public bool Primary(double px, double py, int[] stack, out SurfaceHit hit)
        {
            var c = Camera;
            var bvh = Bvh;
            double x = c.Left + px / c.Width * (c.Right - c.Left);
            double y = c.Top - py / c.Height * (c.Top - c.Bottom);
            Vector3d origin, direction;
            double depthPerT;   // depth along the view direction per unit t
            if (c.Parallel)
            {
                origin = (Vector3d)(c.Location - bvh.Origin) + x * c.X + y * c.Y;
                direction = c.Direction;
                depthPerT = 1;
            }
            else
            {
                origin = (Vector3d)(c.Location - bvh.Origin);
                direction = x * c.X + y * c.Y + c.Near * c.Direction;
                depthPerT = c.Near;
            }
            double tMin = c.Near / depthPerT, tMax = c.Far / depthPerT;

            hit = default;
            // Step past hits a clipping plane removes.
            for (int guard = 0; guard < 64; guard++)
            {
                int f = bvh.Nearest(origin, direction, tMin, tMax, ShadowBvh.Role.Visible, stack, out var t);
                if (f < 0)
                    return false;
                var p = origin + t * direction;
                bool clipped = false;
                foreach (var (n, d) in Clips)
                {
                    if (n * p + d < 0)
                    {
                        clipped = true;
                        break;
                    }
                }
                if (clipped)
                {
                    tMin = t;
                    continue;
                }
                var normal = bvh.Normal(f);
                var smooth = bvh.SmoothNormal(f, p);
                if (normal * direction > 0)
                    normal = -normal;
                // Vertex normals follow the mesh's orientation, which may face away from the camera.
                if (smooth * normal < 0)
                    smooth = -smooth;
                hit = new SurfaceHit
                {
                    Point = p,
                    Normal = normal,
                    Smooth = smooth,
                    // On curved surfaces the facets bulge in and out of the true surface by a share
                    // of their size; secondary rays start that far off it, or near the terminator
                    // they graze neighbouring facets and leave specks. Flat surfaces need no lift.
                    Lift = 1 - smooth * normal > 1e-6 ? 0.05 * bvh.Scale(f) : 0,
                    Role = bvh.RoleOf(f),
                };
                return true;
            }
            return false;
        }

        /// <summary>Where secondary rays leave a hit: just off the facet, lifted on curved surfaces.</summary>
        public Vector3d RayStart(in SurfaceHit hit) => hit.Point + 4 * Eps * hit.Normal + hit.Lift * hit.Smooth;
    }

    /// <summary>Small, fast, repeatable random numbers (PCG32), one stream per pixel sample.</summary>
    internal struct Rng
    {
        ulong _state;

        public Rng(uint pixel, uint sample)
        {
            _state = 0x853c49e6748fea9bUL ^ ((ulong)pixel << 20) ^ sample;
            Next();
            _state += 0xda3e39cb94b95bdbUL * (sample + 1);
            Next();
        }

        public uint Next()
        {
            ulong old = _state;
            _state = old * 6364136223846793005UL + 1442695040888963407UL;
            uint xorshifted = (uint)(((old >> 18) ^ old) >> 27);
            int rot = (int)(old >> 59);
            return (xorshifted >> rot) | (xorshifted << ((-rot) & 31));
        }

        /// <summary>Uniform in [0, 1).</summary>
        public double NextDouble() => Next() * (1.0 / 4294967296.0);

        /// <summary>
        /// A unit vector around <paramref name="axis"/>: uniform on the cap of half angle
        /// θ (cosMax = cos θ) when cosine is false, cosine-weighted on the hemisphere when true.
        /// (u1, u2) in [0,1)² choose it, so stratified pairs give stratified directions.
        /// </summary>
        public static Vector3d Around(Vector3d axis, double u1, double u2, double cosMax, bool cosine)
        {
            Basis(axis, out var u, out var v);
            return Around(axis, u, v, u1, u2, cosMax, cosine);
        }

        /// <summary>Two unit vectors that make an orthonormal frame with the axis.</summary>
        public static void Basis(Vector3d axis, out Vector3d u, out Vector3d v)
        {
            var a = Math.Abs(axis.X) > 0.9 ? Vector3d.YAxis : Vector3d.XAxis;
            u = Vector3d.CrossProduct(a, axis);
            u.Unitize();
            v = Vector3d.CrossProduct(axis, u);
        }

        /// <summary>As <see cref="Around(Vector3d, double, double, double, bool)"/>, with the frame computed once by the caller.</summary>
        public static Vector3d Around(Vector3d axis, Vector3d u, Vector3d v, double u1, double u2, double cosMax, bool cosine)
        {
            double z, r;
            if (cosine)
            {
                r = Math.Sqrt(u1);
                z = Math.Sqrt(Math.Max(0, 1 - u1));
            }
            else
            {
                z = 1 - u1 * (1 - cosMax);
                r = Math.Sqrt(Math.Max(0, 1 - z * z));
            }
            double phi = 2 * Math.PI * u2;
            return r * Math.Cos(phi) * u + r * Math.Sin(phi) * v + z * axis;
        }
    }
}
