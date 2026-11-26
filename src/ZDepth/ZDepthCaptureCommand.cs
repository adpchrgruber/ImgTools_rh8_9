using System;
using System.Globalization;
using System.IO;
using System.Text;
using Rhino;
using Rhino.Commands;
using Rhino.Display;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace ImgTools
{
    /// <summary>
    /// Saves the active view's depth at any size: 8-bit or 16-bit greyscale PNG, or a float
    /// EXR in model units. The depth is rendered on the CPU from the objects the view draws,
    /// so it is exact and unaffected by display settings. A JSON file next to the image
    /// records range, units, encoding and camera.
    /// </summary>
    public class ZDepthCaptureCommand : Command
    {
        static readonly string[] Formats = { "PNG", "PNG16", "EXR" };
        const int Png = 0, Png16 = 1, Exr = 2;

        /// <summary>Depth written to an EXR where no surface was hit (as Blender does).</summary>
        const float ExrBackground = 1e10f;

        /// <summary>Largest image, in pixels (a float per pixel while rendering).</summary>
        const long MaxPixels = 100000000;

        /// <summary>Saved with Rhino's settings, shared with CaptureAll.</summary>
        internal static int Format
        {
            get => ImgToolsPlugin.Instance?.Settings.GetInteger("ZDepth.Format", Png) ?? Png;
            set => ImgToolsPlugin.Instance?.Settings.SetInteger("ZDepth.Format", value);
        }

        static int _format { get => Format; set => Format = value; }

        public override string EnglishName => "ZDepthCapture";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var view = doc.Views.ActiveView;
            var conduit = ImgToolsPlugin.Instance.DepthConduit;
            if (view == null || conduit == null)
                return Result.Failure;

            // Size: defaults to the viewport; match your render size so the passes line up.
            var size = view.ActiveViewport.Size;
            var width = new OptionInteger(size.Width, 16, 16384);
            var height = new OptionInteger(size.Height, 16, 16384);
            while (true)
            {
                var go = new GetOption();
                go.SetCommandPrompt("Z Depth capture (" + ZDepthSettings.Describe(doc) + "). Press Enter to save");
                go.AcceptNothing(true);
                int formatIndex = go.AddOptionList("Format", Formats, _format);
                go.AddOptionInteger("Width", ref width);
                go.AddOptionInteger("Height", ref height);
                var res = go.Get();
                if (res == GetResult.Option)
                {
                    if (go.Option().Index == formatIndex)
                        _format = go.Option().CurrentListOptionIndex;
                    continue;
                }
                if (res == GetResult.Nothing)
                    break;
                return Result.Cancel;
            }
            if ((long)width.CurrentValue * height.CurrentValue > MaxPixels)
            {
                RhinoApp.WriteLine("Z Depth: {0}x{1} is too large; at most {2} million pixels.", width.CurrentValue, height.CurrentValue, MaxPixels / 1000000);
                return Result.Failure;
            }

            var path = CaptureIO.GetOutputPath(doc, view, mode, "zdepth", _format == Exr ? ".exr" : ".png", "Save Z Depth");
            if (path == null)
                return Result.Cancel;

            return Capture(doc, view, width.CurrentValue, height.CurrentValue, path, _format) ? Result.Success : Result.Failure;
        }

        /// <summary>Saves the view's depth at the size to path (format: PNG, PNG16 or EXR) plus its JSON.</summary>
        internal static bool Capture(RhinoDoc doc, RhinoView view, int width, int height, string path, int format)
        {
            var conduit = ImgToolsPlugin.Instance?.DepthConduit;
            if (conduit == null)
                return false;
            var image = DepthCapture.Render(conduit, doc, view, width, height, out var camera);
            if (image == null)
            {
                RhinoApp.WriteLine("Z Depth capture failed.");
                return false;
            }
            if (image.Hits == 0)
                RhinoApp.WriteLine("Z Depth: no surfaces visible in {0}.", view.ActiveViewport.Name);

            bool fixedRange = ZDepthSettings.TryGetFixedRange(doc, out var near, out var far);
            var range = fixedRange ? new Interval(near, far)
                : image.GeometryRange.IsValid ? image.GeometryRange : new Interval(0, 1);
            bool white = ZDepthSettings.NearIsWhite;

            string encoding, background;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                int w = image.Width, h = image.Height;
                var depth = image.Depth;
                if (format == Exr)
                {
                    var z = new float[depth.Length];
                    for (int i = 0; i < z.Length; i++)
                        z[i] = float.IsNaN(depth[i]) ? ExrBackground : depth[i];
                    ImageWriters.WriteExr(path, w, h, z);
                    encoding = "Z = depth in model units (float)";
                    background = ExrBackground.ToString("R", CultureInfo.InvariantCulture);
                }
                else
                {
                    int max = format == Png16 ? 65535 : 255;
                    var levels = new int[depth.Length];
                    for (int i = 0; i < levels.Length; i++)
                    {
                        if (float.IsNaN(depth[i]))
                        {
                            levels[i] = white ? 0 : max;
                            continue;
                        }
                        double t = (depth[i] - range.T0) / range.Length;
                        t = t < 0 ? 0 : t > 1 ? 1 : t;
                        levels[i] = (int)Math.Round(max * (white ? 1 - t : t));
                    }
                    if (format == Png16)
                        ImageWriters.WritePng16(path, w, h, Array.ConvertAll(levels, v => (ushort)v));
                    else
                        ImageWriters.WritePng8(path, w, h, Array.ConvertAll(levels, v => (byte)v));
                    encoding = white
                        ? "grey = " + max + " * (far - depth) / (far - near), clamped"
                        : "grey = " + max + " * (depth - near) / (far - near), clamped";
                    background = white ? "0 (black)" : max + " (white)";
                }
                CaptureIO.SaveJson(path, BuildJson(doc, view, path, image, camera, range, fixedRange, encoding, background));
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                RhinoApp.WriteLine("Z Depth: could not save {0}: {1}", path, e.Message);
                return false;
            }

            RhinoApp.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "Z Depth: {0} {1}x{2}, near {3:G6}, far {4:G6} {5}.", Formats[format], image.Width, image.Height,
                range.T0, range.T1, doc.ModelUnitSystem));
            RhinoApp.WriteLine("Saved {0}", path);
            return true;
        }

        static string BuildJson(RhinoDoc doc, RhinoView view, string imagePath, DepthImage image, DepthCamera camera,
            Interval range, bool fixedRange, string encoding, string background)
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"image\": ").Append(CaptureIO.Str(Path.GetFileName(imagePath))).Append(",\n");
            sb.Append("  \"document\": ").Append(CaptureIO.Str(doc.Path ?? "")).Append(",\n");
            sb.Append("  \"view\": ").Append(CaptureIO.Str(view.ActiveViewport.Name)).Append(",\n");
            sb.Append("  \"width\": ").Append(image.Width).Append(",\n");
            sb.Append("  \"height\": ").Append(image.Height).Append(",\n");
            sb.Append("  \"depth\": \"distance from the camera along the view direction\",\n");
            sb.Append("  \"units\": ").Append(CaptureIO.Str(doc.ModelUnitSystem.ToString())).Append(",\n");
            sb.Append("  \"range\": ").Append(CaptureIO.Str(fixedRange ? "fixed" : "auto")).Append(",\n");
            sb.Append("  \"near\": ").Append(CaptureIO.Num(range.T0)).Append(",\n");
            sb.Append("  \"far\": ").Append(CaptureIO.Num(range.T1)).Append(",\n");
            if (image.VisibleRange.IsValid)
                sb.Append("  \"visibleDepth\": [").Append(CaptureIO.Num(image.VisibleRange.T0)).Append(", ").Append(CaptureIO.Num(image.VisibleRange.T1)).Append("],\n");
            sb.Append("  \"encoding\": ").Append(CaptureIO.Str(encoding)).Append(",\n");
            sb.Append("  \"background\": ").Append(CaptureIO.Str(background)).Append(",\n");
            sb.Append("  \"camera\": {\n");
            sb.Append("    \"projection\": ").Append(CaptureIO.Str(camera.Parallel ? "parallel" : "perspective")).Append(",\n");
            sb.Append("    \"location\": ").Append(CaptureIO.Vec(camera.Location)).Append(",\n");
            sb.Append("    \"direction\": ").Append(CaptureIO.Vec(camera.Direction)).Append(",\n");
            sb.Append("    \"up\": ").Append(CaptureIO.Vec(camera.Y)).Append(",\n");
            sb.Append("    \"lensLength\": ").Append(CaptureIO.Num(view.ActiveViewport.Camera35mmLensLength)).Append(",\n");
            sb.Append("    \"frustum\": {\"left\": ").Append(CaptureIO.Num(camera.Left)).Append(", \"right\": ").Append(CaptureIO.Num(camera.Right))
              .Append(", \"bottom\": ").Append(CaptureIO.Num(camera.Bottom)).Append(", \"top\": ").Append(CaptureIO.Num(camera.Top))
              .Append(", \"near\": ").Append(CaptureIO.Num(camera.Near)).Append("}\n");
            sb.Append("  }\n");
            sb.Append("}\n");
            return sb.ToString();
        }
    }
}
