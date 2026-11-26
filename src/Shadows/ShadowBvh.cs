using System;
using System.Collections.Generic;
using Rhino.Geometry;

namespace ImgTools
{
    /// <summary>
    /// Triangles of the whole scene in a bounding volume hierarchy, for ray casting.
    /// Vertices are stored relative to <see cref="Origin"/> (the scene centre) as floats,
    /// which keeps precision in large scenes while halving the memory.
    /// </summary>
    internal sealed class ShadowBvh
    {
        /// <summary>What a triangle takes part in.</summary>
        [Flags]
        public enum Role : byte
        {
            None = 0,
            Visible = 1,    // drawn by the view: primary rays hit it
            Casts = 2,      // blocks shadow rays
            Receives = 4,   // shows shadows falling on it
        }

        public readonly Point3d Origin;
        public readonly double Size;    // scene bounding box diagonal

        // Per triangle, in tree order (each leaf's triangles are contiguous):
        // v0, edge1, edge2 (9 floats) and its role.
        float[] _tri;
        float[] _normal;   // per triangle: the three vertex normals (9 floats), world space
        Role[] _role;

        // While building: bounds (6 floats); leaf: start/count into _order; inner: left child (right = left + 1).
        float[] _bounds;
        int[] _first, _count;
        int[] _order;
        int _nodes, _depth;

        /// <summary>A node, 32 bytes: half a cache line, read in one go during traversal.</summary>
        struct Node
        {
            public float X0, Y0, Z0, X1, Y1, Z1;
            public int First;   // leaf: first triangle; inner: left child (right = First + 1)
            public int Count;   // leaf: triangle count; inner: 0
        }

        Node[] _node = new Node[0];

        public int TriangleCount => _role.Length;

        public ShadowBvh(IList<(DepthItem Item, Role Role)> items)
        {
            // Scene bounds, for the origin.
            var box = BoundingBox.Unset;
            int total = 0;
            foreach (var (item, _) in items)
            {
                var v = item.Vertices;
                for (int k = 0; k < v.Length; k += 3)
                {
                    var p = new Point3d(v[k], v[k + 1], v[k + 2]);
                    p.Transform(item.Xform);
                    if (box.IsValid) box.Union(p); else box = new BoundingBox(p, p);
                }
                total += item.Triangles.Length / 3;
            }
            Origin = box.IsValid ? box.Center : Point3d.Origin;
            Size = box.IsValid ? Math.Max(box.Diagonal.Length, 1e-9) : 1;

            _tri = new float[9 * total];
            _normal = new float[9 * total];
            _role = new Role[total];
            var centroid = new float[3 * total];
            int f = 0;
            foreach (var (item, role) in items)
            {
                var v = item.Vertices;
                var x = item.Xform;
                var world = new Point3d[v.Length / 3];
                for (int i = 0, k = 0; i < world.Length; i++, k += 3)
                {
                    var p = new Point3d(v[k], v[k + 1], v[k + 2]);
                    p.Transform(x);
                    world[i] = new Point3d(p.X - Origin.X, p.Y - Origin.Y, p.Z - Origin.Z);
                }
                // Normals transform by the inverse transpose; without them, use the face normal.
                Vector3d[] nrm = null;
                if (item.Normals != null && x.TryGetInverse(out var inverse))
                {
                    var nx = inverse.Transpose();
                    nrm = new Vector3d[world.Length];
                    for (int i = 0, k = 0; i < nrm.Length; i++, k += 3)
                    {
                        var n = new Vector3d(item.Normals[k], item.Normals[k + 1], item.Normals[k + 2]);
                        n.Transform(nx);
                        n.Unitize();
                        nrm[i] = n;
                    }
                }
                var t = item.Triangles;
                for (int k = 0; k < t.Length; k += 3, f++)
                {
                    Point3d a = world[t[k]], b = world[t[k + 1]], c = world[t[k + 2]];
                    int o = 9 * f;
                    _tri[o] = (float)a.X; _tri[o + 1] = (float)a.Y; _tri[o + 2] = (float)a.Z;
                    _tri[o + 3] = (float)(b.X - a.X); _tri[o + 4] = (float)(b.Y - a.Y); _tri[o + 5] = (float)(b.Z - a.Z);
                    _tri[o + 6] = (float)(c.X - a.X); _tri[o + 7] = (float)(c.Y - a.Y); _tri[o + 8] = (float)(c.Z - a.Z);
                    _role[f] = role;
                    for (int j = 0; j < 3; j++)
                    {
                        var n = nrm != null ? nrm[t[k + j]] : Vector3d.CrossProduct(b - a, c - a);
                        if (nrm == null) n.Unitize();
                        _normal[o + 3 * j] = (float)n.X; _normal[o + 3 * j + 1] = (float)n.Y; _normal[o + 3 * j + 2] = (float)n.Z;
                    }
                    centroid[3 * f] = (float)((a.X + b.X + c.X) / 3);
                    centroid[3 * f + 1] = (float)((a.Y + b.Y + c.Y) / 3);
                    centroid[3 * f + 2] = (float)((a.Z + b.Z + c.Z) / 3);
                }
            }

            _order = new int[total];
            for (int i = 0; i < total; i++)
                _order[i] = i;
            int maxNodes = Math.Max(1, 2 * total);
            _bounds = new float[6 * maxNodes];
            _first = new int[maxNodes];
            _count = new int[maxNodes];
            _nodes = 1;
            if (total > 0)
            {
                Build(0, 0, total, centroid, 1);
                Pack();
            }
        }

        /// <summary>
        /// Packs the nodes into one struct array and puts the triangles in tree order, so a
        /// traversal reads memory front to back instead of jumping through an index.
        /// </summary>
        void Pack()
        {
            _node = new Node[_nodes];
            for (int i = 0; i < _nodes; i++)
            {
                int b = 6 * i;
                _node[i] = new Node
                {
                    X0 = _bounds[b], Y0 = _bounds[b + 1], Z0 = _bounds[b + 2],
                    X1 = _bounds[b + 3], Y1 = _bounds[b + 4], Z1 = _bounds[b + 5],
                    First = _first[i], Count = _count[i],
                };
            }
            int total = _order.Length;
            var tri = new float[_tri.Length];
            var normal = new float[_normal.Length];
            var role = new Role[total];
            for (int i = 0; i < total; i++)
            {
                int f = _order[i];
                Array.Copy(_tri, 9 * f, tri, 9 * i, 9);
                Array.Copy(_normal, 9 * f, normal, 9 * i, 9);
                role[i] = _role[f];
            }
            _tri = tri;
            _normal = normal;
            _role = role;
            _bounds = null;
            _first = _count = _order = null;
        }

        /// <summary>Unit triangle normal (not oriented).</summary>
        public Vector3d Normal(int triangle)
        {
            int o = 9 * triangle;
            var n = Vector3d.CrossProduct(new Vector3d(_tri[o + 3], _tri[o + 4], _tri[o + 5]), new Vector3d(_tri[o + 6], _tri[o + 7], _tri[o + 8]));
            n.Unitize();
            return n;
        }

        /// <summary>Unit normal interpolated from the vertex normals at a point on the triangle (relative to <see cref="Origin"/>).</summary>
        public Vector3d SmoothNormal(int triangle, Vector3d p)
        {
            int o = 9 * triangle;
            // Barycentric coordinates of p.
            var e1 = new Vector3d(_tri[o + 3], _tri[o + 4], _tri[o + 5]);
            var e2 = new Vector3d(_tri[o + 6], _tri[o + 7], _tri[o + 8]);
            var d = p - new Vector3d(_tri[o], _tri[o + 1], _tri[o + 2]);
            double d11 = e1 * e1, d12 = e1 * e2, d22 = e2 * e2, dp1 = d * e1, dp2 = d * e2;
            double den = d11 * d22 - d12 * d12;
            if (Math.Abs(den) < 1e-30)
                return Normal(triangle);
            double v = (d22 * dp1 - d12 * dp2) / den, w = (d11 * dp2 - d12 * dp1) / den, u = 1 - v - w;
            var n = new Vector3d(
                u * _normal[o] + v * _normal[o + 3] + w * _normal[o + 6],
                u * _normal[o + 1] + v * _normal[o + 4] + w * _normal[o + 7],
                u * _normal[o + 2] + v * _normal[o + 5] + w * _normal[o + 8]);
            return n.Unitize() ? n : Normal(triangle);
        }

        /// <summary>Typical edge length of a triangle: the square root of twice its area.</summary>
        public double Scale(int triangle)
        {
            int o = 9 * triangle;
            var n = Vector3d.CrossProduct(new Vector3d(_tri[o + 3], _tri[o + 4], _tri[o + 5]), new Vector3d(_tri[o + 6], _tri[o + 7], _tri[o + 8]));
            return Math.Sqrt(n.Length);
        }

        public Role RoleOf(int triangle) => _role[triangle];

        // --- build ---------------------------------------------------------------

        const int LeafSize = 4;
        const int Bins = 12;

        void Build(int node, int start, int count, float[] centroid, int depth)
        {
            // Deepest level so far (subtrees build in parallel).
            int seen = System.Threading.Volatile.Read(ref _depth);
            while (depth > seen)
            {
                int was = System.Threading.Interlocked.CompareExchange(ref _depth, depth, seen);
                if (was == seen)
                    break;
                seen = was;
            }
            // Bounds of the triangles, and of their centroids.
            float bx0 = float.MaxValue, by0 = float.MaxValue, bz0 = float.MaxValue;
            float bx1 = float.MinValue, by1 = float.MinValue, bz1 = float.MinValue;
            float cx0 = float.MaxValue, cy0 = float.MaxValue, cz0 = float.MaxValue;
            float cx1 = float.MinValue, cy1 = float.MinValue, cz1 = float.MinValue;
            for (int i = start; i < start + count; i++)
            {
                int f = _order[i], o = 9 * f;
                for (int j = 0; j < 3; j++)
                {
                    float x = _tri[o], y = _tri[o + 1], z = _tri[o + 2];
                    if (j > 0)
                    {
                        x += _tri[o + 3 * j]; y += _tri[o + 3 * j + 1]; z += _tri[o + 3 * j + 2];
                    }
                    if (x < bx0) bx0 = x; if (x > bx1) bx1 = x;
                    if (y < by0) by0 = y; if (y > by1) by1 = y;
                    if (z < bz0) bz0 = z; if (z > bz1) bz1 = z;
                }
                float u = centroid[3 * f], v = centroid[3 * f + 1], w = centroid[3 * f + 2];
                if (u < cx0) cx0 = u; if (u > cx1) cx1 = u;
                if (v < cy0) cy0 = v; if (v > cy1) cy1 = v;
                if (w < cz0) cz0 = w; if (w > cz1) cz1 = w;
            }
            int b = 6 * node;
            _bounds[b] = bx0; _bounds[b + 1] = by0; _bounds[b + 2] = bz0;
            _bounds[b + 3] = bx1; _bounds[b + 4] = by1; _bounds[b + 5] = bz1;

            if (count <= LeafSize)
            {
                MakeLeaf(node, start, count);
                return;
            }

            // Split along the widest centroid axis, at the best of a few binned SAH candidates.
            float ex = cx1 - cx0, ey = cy1 - cy0, ez = cz1 - cz0;
            int axis = ex >= ey && ex >= ez ? 0 : ey >= ez ? 1 : 2;
            float lo = axis == 0 ? cx0 : axis == 1 ? cy0 : cz0;
            float extent = axis == 0 ? ex : axis == 1 ? ey : ez;
            if (extent <= 0)
            {
                // All centroids coincide: split in the middle of the list.
                Children(node, start, count, count / 2, centroid, depth);
                return;
            }

            var binCount = new int[Bins];
            var binBox = new float[6 * Bins];
            for (int i = 0; i < Bins; i++)
            {
                binBox[6 * i] = binBox[6 * i + 1] = binBox[6 * i + 2] = float.MaxValue;
                binBox[6 * i + 3] = binBox[6 * i + 4] = binBox[6 * i + 5] = float.MinValue;
            }
            float scale = Bins / extent;
            for (int i = start; i < start + count; i++)
            {
                int f = _order[i];
                int bin = Math.Min(Bins - 1, (int)((centroid[3 * f + axis] - lo) * scale));
                binCount[bin]++;
                int o = 9 * f, q = 6 * bin;
                for (int j = 0; j < 3; j++)
                {
                    float x = _tri[o], y = _tri[o + 1], z = _tri[o + 2];
                    if (j > 0)
                    {
                        x += _tri[o + 3 * j]; y += _tri[o + 3 * j + 1]; z += _tri[o + 3 * j + 2];
                    }
                    if (x < binBox[q]) binBox[q] = x; if (x > binBox[q + 3]) binBox[q + 3] = x;
                    if (y < binBox[q + 1]) binBox[q + 1] = y; if (y > binBox[q + 4]) binBox[q + 4] = y;
                    if (z < binBox[q + 2]) binBox[q + 2] = z; if (z > binBox[q + 5]) binBox[q + 5] = z;
                }
            }
            // Sweep: cost of splitting after bin s.
            var leftArea = new double[Bins];
            var leftCount = new int[Bins];
            var acc = new[] { float.MaxValue, float.MaxValue, float.MaxValue, float.MinValue, float.MinValue, float.MinValue };
            int n = 0;
            for (int s = 0; s < Bins; s++)
            {
                Grow(acc, binBox, s);
                n += binCount[s];
                leftCount[s] = n;
                leftArea[s] = Area(acc);
            }
            acc = new[] { float.MaxValue, float.MaxValue, float.MaxValue, float.MinValue, float.MinValue, float.MinValue };
            double bestCost = double.MaxValue;
            int bestSplit = -1;
            n = 0;
            for (int s = Bins - 1; s > 0; s--)
            {
                Grow(acc, binBox, s);
                n += binCount[s];
                int left = leftCount[s - 1];
                if (left == 0 || n == 0)
                    continue;
                double cost = leftArea[s - 1] * left + Area(acc) * n;
                if (cost < bestCost)
                {
                    bestCost = cost;
                    bestSplit = s;
                }
            }
            if (bestSplit < 0)
            {
                Children(node, start, count, count / 2, centroid, depth);
                return;
            }

            // Partition by bin.
            int iL = start, iR = start + count - 1;
            while (iL <= iR)
            {
                int f = _order[iL];
                int bin = Math.Min(Bins - 1, (int)((centroid[3 * f + axis] - lo) * scale));
                if (bin < bestSplit)
                    iL++;
                else
                {
                    _order[iL] = _order[iR];
                    _order[iR--] = f;
                }
            }
            int leftSize = iL - start;
            if (leftSize == 0 || leftSize == count)
                leftSize = count / 2;
            Children(node, start, count, leftSize, centroid, depth);
        }

        /// <summary>Subtrees at least this large build on their own thread.</summary>
        const int ParallelBuild = 20000;

        void Children(int node, int start, int count, int leftSize, float[] centroid, int depth)
        {
            // Children ranges of _order and node slots are disjoint, so subtrees can build at once.
            int left = System.Threading.Interlocked.Add(ref _nodes, 2) - 2;
            _first[node] = left;
            _count[node] = 0;   // inner
            if (count >= ParallelBuild)
            {
                System.Threading.Tasks.Parallel.Invoke(
                    () => Build(left, start, leftSize, centroid, depth + 1),
                    () => Build(left + 1, start + leftSize, count - leftSize, centroid, depth + 1));
                return;
            }
            Build(left, start, leftSize, centroid, depth + 1);
            Build(left + 1, start + leftSize, count - leftSize, centroid, depth + 1);
        }

        void MakeLeaf(int node, int start, int count)
        {
            _first[node] = start;
            _count[node] = count;
        }

        static void Grow(float[] acc, float[] box, int i)
        {
            int q = 6 * i;
            if (box[q] > box[q + 3])
                return;   // empty bin
            for (int k = 0; k < 3; k++)
            {
                if (box[q + k] < acc[k]) acc[k] = box[q + k];
                if (box[q + 3 + k] > acc[3 + k]) acc[3 + k] = box[q + 3 + k];
            }
        }

        static double Area(float[] a)
        {
            if (a[0] > a[3])
                return 0;
            double x = a[3] - a[0], y = a[4] - a[1], z = a[5] - a[2];
            return x * y + y * z + z * x;
        }

        // --- queries -------------------------------------------------------------

        /// <summary>
        /// Nearest hit with distance in (tMin, tMax) on a triangle with any of the roles.
        /// The ray origin is relative to <see cref="Origin"/>; the direction need not be unit.
        /// Returns the triangle, or -1. Visits the nearer child first and skips any node that
        /// starts behind the best hit so far.
        /// </summary>
        public int Nearest(Vector3d origin, Vector3d direction, double tMin, double tMax, Role mask, int[] stack, out double t)
        {
            t = tMax;
            if (TriangleCount == 0)
                return -1;
            var nodes = _node;
            float ox = (float)origin.X, oy = (float)origin.Y, oz = (float)origin.Z;
            float dx = (float)direction.X, dy = (float)direction.Y, dz = (float)direction.Z;
            float ix = 1f / dx, iy = 1f / dy, iz = 1f / dz;
            float best = (float)tMax, min = (float)tMin;
            int hit = -1;
            if (!Box(in nodes[0], ox, oy, oz, ix, iy, iz, min, best, out _))
                return -1;
            int sp = 0, node = 0;
            while (true)
            {
                ref readonly var n = ref nodes[node];
                if (n.Count > 0)
                {
                    for (int f = n.First, end = f + n.Count; f < end; f++)
                    {
                        if ((_role[f] & mask) == 0)
                            continue;
                        float d = Intersect(f, ox, oy, oz, dx, dy, dz);
                        if (d > min && d < best)
                        {
                            best = d;
                            hit = f;
                        }
                    }
                }
                else
                {
                    int left = n.First;
                    bool hl = Box(in nodes[left], ox, oy, oz, ix, iy, iz, min, best, out float tl);
                    bool hr = Box(in nodes[left + 1], ox, oy, oz, ix, iy, iz, min, best, out float tr);
                    if (hl && hr)
                    {
                        // Near child now, far child later (with its entry distance).
                        int far = tl <= tr ? left + 1 : left;
                        stack[sp++] = far;
                        stack[sp++] = Bits(tl <= tr ? tr : tl);
                        node = tl <= tr ? left : left + 1;
                        continue;
                    }
                    if (hl || hr)
                    {
                        node = hl ? left : left + 1;
                        continue;
                    }
                }
                // Next node on the stack that still starts before the best hit.
                while (true)
                {
                    if (sp == 0)
                    {
                        t = best;
                        return hit;
                    }
                    float entry = Float(stack[--sp]);
                    node = stack[--sp];
                    if (entry <= best * 1.00001f + 1e-6f)
                        break;
                }
            }
        }

        /// <summary>True when any triangle with the roles lies on the ray within (tMin, tMax).</summary>
        public bool Any(Vector3d origin, Vector3d direction, double tMin, double tMax, Role mask, int[] stack)
        {
            if (TriangleCount == 0)
                return false;
            var nodes = _node;
            float ox = (float)origin.X, oy = (float)origin.Y, oz = (float)origin.Z;
            float dx = (float)direction.X, dy = (float)direction.Y, dz = (float)direction.Z;
            float ix = 1f / dx, iy = 1f / dy, iz = 1f / dz;
            float max = (float)tMax, min = (float)tMin;
            if (!Box(in nodes[0], ox, oy, oz, ix, iy, iz, min, max, out _))
                return false;
            int sp = 0, node = 0;
            while (true)
            {
                ref readonly var n = ref nodes[node];
                if (n.Count > 0)
                {
                    for (int f = n.First, end = f + n.Count; f < end; f++)
                    {
                        if ((_role[f] & mask) == 0)
                            continue;
                        float d = Intersect(f, ox, oy, oz, dx, dy, dz);
                        if (d > min && d < max)
                            return true;
                    }
                }
                else
                {
                    int left = n.First;
                    bool hl = Box(in nodes[left], ox, oy, oz, ix, iy, iz, min, max, out _);
                    bool hr = Box(in nodes[left + 1], ox, oy, oz, ix, iy, iz, min, max, out _);
                    if (hl && hr)
                    {
                        stack[sp++] = left + 1;
                        node = left;
                        continue;
                    }
                    if (hl || hr)
                    {
                        node = hl ? left : left + 1;
                        continue;
                    }
                }
                if (sp == 0)
                    return false;
                node = stack[--sp];
            }
        }

        /// <summary>Stack size large enough for this tree (two entries per level for Nearest).</summary>
        public int StackSize => 2 * (_depth + 8);

        /// <summary>Slab test; tNear is where the ray enters the box (clamped to tMin).</summary>
        static bool Box(in Node b, float ox, float oy, float oz, float ix, float iy, float iz, float tMin, float tMax, out float tNear)
        {
            float t0 = (b.X0 - ox) * ix, t1 = (b.X1 - ox) * ix;
            if (t0 > t1) { var s = t0; t0 = t1; t1 = s; }
            float u0 = (b.Y0 - oy) * iy, u1 = (b.Y1 - oy) * iy;
            if (u0 > u1) { var s = u0; u0 = u1; u1 = s; }
            float w0 = (b.Z0 - oz) * iz, w1 = (b.Z1 - oz) * iz;
            if (w0 > w1) { var s = w0; w0 = w1; w1 = s; }
            // NaN (0 * inf, ray in a box face) compares false and leaves the bound unchanged.
            float near = tMin, far = tMax;
            if (t0 > near) near = t0;
            if (u0 > near) near = u0;
            if (w0 > near) near = w0;
            if (t1 < far) far = t1;
            if (u1 < far) far = u1;
            if (w1 < far) far = w1;
            tNear = near;
            // Slack: float rounding must never lose a triangle lying in a box face.
            return near <= far * 1.00001f + 1e-6f;
        }

        // Float <-> int bits for the traversal stack (BitConverter's versions are missing from .NET Framework 4.8).
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
        struct FloatBits
        {
            [System.Runtime.InteropServices.FieldOffset(0)] public float F;
            [System.Runtime.InteropServices.FieldOffset(0)] public int I;
        }

        static int Bits(float f) => new FloatBits { F = f }.I;

        static float Float(int i) => new FloatBits { I = i }.F;

        /// <summary>Möller–Trumbore, two-sided. Returns the ray parameter, or NaN.</summary>
        float Intersect(int f, float ox, float oy, float oz, float dx, float dy, float dz)
        {
            var tri = _tri;
            int o = 9 * f;
            float e1x = tri[o + 3], e1y = tri[o + 4], e1z = tri[o + 5];
            float e2x = tri[o + 6], e2y = tri[o + 7], e2z = tri[o + 8];
            float px = dy * e2z - dz * e2y, py = dz * e2x - dx * e2z, pz = dx * e2y - dy * e2x;
            float det = e1x * px + e1y * py + e1z * pz;
            if (det == 0)
                return float.NaN;
            float inv = 1f / det;
            float sx = ox - tri[o], sy = oy - tri[o + 1], sz = oz - tri[o + 2];
            float u = (sx * px + sy * py + sz * pz) * inv;
            if (u < 0 || u > 1)
                return float.NaN;
            float qx = sy * e1z - sz * e1y, qy = sz * e1x - sx * e1z, qz = sx * e1y - sy * e1x;
            float v = (dx * qx + dy * qy + dz * qz) * inv;
            if (v < 0 || u + v > 1)
                return float.NaN;
            return (e2x * qx + e2y * qy + e2z * qz) * inv;
        }
    }
}
