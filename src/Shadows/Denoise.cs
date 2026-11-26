using System;
using System.Threading.Tasks;

namespace ImgTools
{
    /// <summary>
    /// Edge-aware smoothing of a noisy traced pass (an "à-trous" wavelet filter): a 5×5 blur
    /// applied three times with growing gaps (1, 2, 4 pixels), so it reaches 29 pixels across at
    /// the cost of 75 taps. Each neighbour counts only as much as it lies on the same surface:
    /// similar depth and similar normal. Edges, creases and contact lines therefore stay sharp
    /// while the grain of a low ray count is averaged away.
    /// </summary>
    internal static class Denoise
    {
        static readonly float[] Kernel = { 1f / 16, 1f / 4, 3f / 8, 1f / 4, 1f / 16 };

        /// <param name="depthTolerance">Relative depth difference that halves a neighbour's weight.</param>
        /// <param name="normalPower">Sharpness of the normal test (higher: creases stay crisper).</param>
        public static float[] Filter(float[] values, PixelGuide guide, int width, int height,
            int iterations = 3, float depthTolerance = 0.01f, float normalPower = 32, Func<bool> cancelled = null)
        {
            var src = (float[])values.Clone();
            var dst = new float[values.Length];
            for (int it = 0; it < iterations; it++)
            {
                int step = 1 << it;
                var a = src;
                var b = dst;
                var result = Parallel.For(0, height, (y, state) =>
                {
                    if (cancelled != null && cancelled())
                    {
                        state.Stop();
                        return;
                    }
                    for (int x = 0; x < width; x++)
                    {
                        int i = y * width + x;
                        float d = guide.Depth[i];
                        if (float.IsNaN(d))
                        {
                            b[i] = a[i];   // background: nothing to smooth
                            continue;
                        }
                        float nx = guide.Nx[i], ny = guide.Ny[i], nz = guide.Nz[i];
                        float tolerance = depthTolerance * Math.Abs(d) + 1e-6f;
                        float sum = 0, weight = 0;
                        for (int ky = -2; ky <= 2; ky++)
                        {
                            int qy = y + ky * step;
                            if (qy < 0 || qy >= height)
                                continue;
                            for (int kx = -2; kx <= 2; kx++)
                            {
                                int qx = x + kx * step;
                                if (qx < 0 || qx >= width)
                                    continue;
                                int q = qy * width + qx;
                                float dq = guide.Depth[q];
                                if (float.IsNaN(dq))
                                    continue;
                                float facing = nx * guide.Nx[q] + ny * guide.Ny[q] + nz * guide.Nz[q];
                                if (facing <= 0)
                                    continue;
                                float dd = (dq - d) / tolerance;
                                float w = Kernel[kx + 2] * Kernel[ky + 2]
                                    * (float)Math.Pow(facing, normalPower)
                                    / (1 + dd * dd);
                                sum += w * a[q];
                                weight += w;
                            }
                        }
                        b[i] = weight > 0 ? sum / weight : a[i];
                    }
                });
                if (!result.IsCompleted)
                    return null;
                var swap = src;
                src = dst;
                dst = swap;
            }
            return src;
        }
    }
}
