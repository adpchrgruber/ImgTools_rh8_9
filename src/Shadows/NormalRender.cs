using System;
using System.Threading.Tasks;
using Rhino;
using Rhino.Geometry;

namespace ImgTools
{
    /// <summary>
    /// Normal pass options shared by NormalCapture and the live Normals view, saved with Rhino's settings.
    /// </summary>
    internal static class NormalSettings
    {
        static PersistentSettings S => ImgToolsPlugin.Instance?.Settings;

        public static readonly string[] Formats = { "PNG", "PNG16", "EXR" };
        public const int Png = 0, Png16 = 1, Exr = 2;

        /// <summary>True (default): relative to the camera (x right, y up, z towards the viewer). False: world X, Y, Z.</summary>
        public static bool CameraSpace
        {
            get => S?.GetBool("Normal.CameraSpace", true) ?? true;
            set => S?.SetBool("Normal.CameraSpace", value);
        }

        /// <summary>True (default): green = up (OpenGL, Blender, ControlNet). False: green = down (DirectX, Unreal). Camera space only.</summary>
        public static bool GreenUp
        {
            get => S?.GetBool("Normal.GreenUp", true) ?? true;
            set => S?.SetBool("Normal.GreenUp", value);
        }

        /// <summary>True (default): background is the flat normal (128, 128, 255). False: black.</summary>
        public static bool FlatBackground
        {
            get => S?.GetBool("Normal.FlatBackground", true) ?? true;
            set => S?.SetBool("Normal.FlatBackground", value);
        }

        public static int Format
        {
            get => S?.GetInteger("Normal.Format", Png) ?? Png;
            set => S?.SetInteger("Normal.Format", value);
        }

        public static int AntiAlias
        {
            get => S?.GetInteger("Normal.AntiAlias", 2) ?? 2;
            set => S?.SetInteger("Normal.AntiAlias", value);
        }

        public static string Describe() =>
            (CameraSpace ? "camera space, " + (GreenUp ? "green up (OpenGL)" : "green down (DirectX)") : "world space")
            + ", background " + (FlatBackground ? "flat blue" : "black");
    }

    /// <summary>
    /// The normal pass: for every visible point the smooth surface normal (interpolated from the
    /// render mesh's vertex normals, facing the camera), in camera or world space. Ray traced with
    /// the same primary rays as the shadow and AO passes, so it lines up with them and with renders.
    /// </summary>
    internal static class NormalRender
    {
        /// <summary>
        /// Rgb: per pixel the unit normal (x, y, z) in the chosen space, averaged over the samples
        /// that hit a surface, or (0, 0, 0) where none did; Coverage: share of samples that hit.
        /// </summary>
        public static TraceImage Render(TraceScene scene, int samples, bool cameraSpace, bool greenUp, Func<bool> cancelled = null)
        {
            var c = scene.Camera;
            int width = scene.Width, height = scene.Height;
            var normals = new float[3 * width * height];
            var cover = new float[width * height];
            int s = Math.Max(1, samples);
            double inv = 1.0 / (s * s);
            // Camera space: x right, y up (or down), z towards the viewer.
            var towardsViewer = -c.Direction;
            double ySign = cameraSpace && !greenUp ? -1 : 1;

            var result = Parallel.For(0, height, () => new int[scene.Bvh.StackSize], (y, state, stack) =>
            {
                if (cancelled != null && cancelled())
                {
                    state.Stop();
                    return stack;
                }
                for (int x = 0; x < width; x++)
                {
                    var sum = Vector3d.Zero;
                    int hits = 0;
                    for (int sy = 0; sy < s; sy++)
                    {
                        for (int sx = 0; sx < s; sx++)
                        {
                            if (!scene.Primary(x + (sx + 0.5) / s, y + (sy + 0.5) / s, stack, out var hit))
                                continue;
                            hits++;
                            sum += hit.Smooth;
                        }
                    }
                    int i = y * width + x;
                    cover[i] = (float)(hits * inv);
                    if (hits == 0 || !sum.Unitize())
                        continue;
                    var n = cameraSpace ? new Vector3d(sum * c.X, ySign * (sum * c.Y), sum * towardsViewer) : sum;
                    normals[3 * i] = (float)n.X;
                    normals[3 * i + 1] = (float)n.Y;
                    normals[3 * i + 2] = (float)n.Z;
                }
                return stack;
            }, _ => { });
            if (!result.IsCompleted)
                return null;

            return new TraceImage
            {
                Width = width,
                Height = height,
                Rgb = normals,
                Coverage = cover,
                Triangles = scene.Bvh.TriangleCount,
            };
        }

        /// <summary>Background colour (0..1) of the encoded image.</summary>
        public static (double R, double G, double B) Background(bool flat) => flat ? (0.5, 0.5, 1.0) : (0, 0, 0);

        /// <summary>
        /// Encodes normals as colour, rgb = n * 0.5 + 0.5, with edge pixels blended with the
        /// background by coverage. Returns r, g, b per pixel in 0..1.
        /// </summary>
        public static double[] Encode(TraceImage image, bool flatBackground)
        {
            var (br, bg, bb) = Background(flatBackground);
            int count = image.Width * image.Height;
            var rgb = new double[3 * count];
            for (int i = 0; i < count; i++)
            {
                double a = image.Coverage[i];
                rgb[3 * i] = a * (image.Rgb[3 * i] * 0.5 + 0.5) + (1 - a) * br;
                rgb[3 * i + 1] = a * (image.Rgb[3 * i + 1] * 0.5 + 0.5) + (1 - a) * bg;
                rgb[3 * i + 2] = a * (image.Rgb[3 * i + 2] * 0.5 + 0.5) + (1 - a) * bb;
            }
            return rgb;
        }

        public static byte[] To8(double[] rgb) => Array.ConvertAll(rgb, v => (byte)Math.Round(255 * (v < 0 ? 0 : v > 1 ? 1 : v)));

        public static ushort[] To16(double[] rgb) => Array.ConvertAll(rgb, v => (ushort)Math.Round(65535 * (v < 0 ? 0 : v > 1 ? 1 : v)));
    }
}
