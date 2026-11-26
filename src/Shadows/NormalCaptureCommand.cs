using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Rhino;
using Rhino.Commands;
using Rhino.Input;
using Rhino.Input.Custom;

namespace ImgTools
{
    /// <summary>
    /// Saves a normal pass of the active view: each visible point's smooth surface normal as
    /// colour (rgb = normal * 0.5 + 0.5), in camera or world space, as an 8-bit or 16-bit RGB
    /// PNG or a float EXR (R, G, B = normal, A = coverage). A JSON file next to the image records
    /// space, convention and camera. Lines up pixel for pixel with the other passes and renders.
    /// </summary>
    public class NormalCaptureCommand : Command
    {
        public override string EnglishName => "NormalCapture";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var view = doc.Views.ActiveView;
            if (view == null || ImgToolsPlugin.Instance?.DepthConduit == null)
                return Result.Failure;

            var size = view.ActiveViewport.Size;
            var width = new OptionInteger(size.Width, 16, 16384);
            var height = new OptionInteger(size.Height, 16, 16384);
            while (true)
            {
                var go = new GetOption();
                go.SetCommandPrompt("Normal capture (" + NormalSettings.Describe() + "). Press Enter to save");
                go.AcceptNothing(true);
                var space = new OptionToggle(NormalSettings.CameraSpace, "World", "Camera");
                var green = new OptionToggle(NormalSettings.GreenUp, "Down", "Up");
                var flatOption = new OptionToggle(NormalSettings.FlatBackground, "Black", "Flat");
                var aa = new OptionInteger(NormalSettings.AntiAlias, 1, 4);
                int formatIndex = go.AddOptionList("Format", NormalSettings.Formats, NormalSettings.Format);
                int spaceIndex = go.AddOptionToggle("Space", ref space);
                int greenIndex = NormalSettings.CameraSpace ? go.AddOptionToggle("Green", ref green) : -1;
                int backgroundIndex = go.AddOptionToggle("Background", ref flatOption);
                int aaIndex = go.AddOptionInteger("AntiAlias", ref aa);
                go.AddOptionInteger("Width", ref width);
                go.AddOptionInteger("Height", ref height);
                var res = go.Get();
                if (res == GetResult.Option)
                {
                    int index = go.Option().Index;
                    if (index == formatIndex) NormalSettings.Format = go.Option().CurrentListOptionIndex;
                    else if (index == spaceIndex) NormalSettings.CameraSpace = space.CurrentValue;
                    else if (index == greenIndex) NormalSettings.GreenUp = green.CurrentValue;
                    else if (index == backgroundIndex) NormalSettings.FlatBackground = flatOption.CurrentValue;
                    else if (index == aaIndex) NormalSettings.AntiAlias = aa.CurrentValue;
                    doc.Views.Redraw();   // the Normals view follows these options
                    continue;
                }
                if (res == GetResult.Nothing)
                    break;
                return Result.Cancel;
            }
            int w = width.CurrentValue, h = height.CurrentValue;
            if (!TraceCapture.CheckSize(w, h, "Normal capture"))
                return Result.Failure;

            int format = NormalSettings.Format;
            bool cameraSpace = NormalSettings.CameraSpace, greenUp = NormalSettings.GreenUp, flat = NormalSettings.FlatBackground;
            var path = CaptureIO.GetOutputPath(doc, view, mode, "normal", format == NormalSettings.Exr ? ".exr" : ".png", "Save normal pass");
            if (path == null)
                return Result.Cancel;

            var scene = TraceScene.Build(doc, view, w, h);
            if (scene == null)
            {
                RhinoApp.WriteLine("Normal capture failed.");
                return Result.Failure;
            }
            return Capture(doc, view, scene, path) ? Result.Success : Result.Failure;
        }

        /// <summary>Traces the normal pass with the current options into path (.png or .exr, per Format) plus its JSON.</summary>
        internal static bool Capture(RhinoDoc doc, Rhino.Display.RhinoView view, TraceScene scene, string path)
        {
            int format = NormalSettings.Format;
            bool cameraSpace = NormalSettings.CameraSpace, greenUp = NormalSettings.GreenUp, flat = NormalSettings.FlatBackground;
            var clock = Stopwatch.StartNew();
            var image = NormalRender.Render(scene, NormalSettings.AntiAlias, cameraSpace, greenUp);

            string encoding, background;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                if (format == NormalSettings.Exr)
                {
                    int n = image.Width * image.Height;
                    float[] r = new float[n], g = new float[n], b = new float[n];
                    for (int i = 0; i < n; i++)
                    {
                        r[i] = image.Rgb[3 * i];
                        g[i] = image.Rgb[3 * i + 1];
                        b[i] = image.Rgb[3 * i + 2];
                    }
                    ImageWriters.WriteExr(path, image.Width, image.Height, ("R", r), ("G", g), ("B", b), ("A", image.Coverage));
                    encoding = "R, G, B = normal x, y, z (unit vector, -1..1); A = coverage";
                    background = "R, G, B = 0, A = 0";
                }
                else
                {
                    var rgb = NormalRender.Encode(image, flat);
                    if (format == NormalSettings.Png16)
                        ImageWriters.WritePngRgb16(path, image.Width, image.Height, NormalRender.To16(rgb));
                    else
                        ImageWriters.WritePngRgb8(path, image.Width, image.Height, NormalRender.To8(rgb));
                    encoding = "rgb = normal * 0.5 + 0.5 (x -> red, y -> green, z -> blue); edges blended with the background";
                    background = flat ? "flat normal (0.5, 0.5, 1) = (128, 128, 255)" : "black";
                }
                File.WriteAllText(Path.ChangeExtension(path, ".json"), BuildJson(doc, view, path, image, scene.Camera, format, cameraSpace, greenUp, encoding, background));
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                RhinoApp.WriteLine("Normal capture: could not save {0}: {1}", path, e.Message);
                return false;
            }

            RhinoApp.WriteLine(string.Format(CultureInfo.InvariantCulture, "Normal capture: {0} {1}x{2}, {3}, {4:0.0} s.",
                NormalSettings.Formats[format], image.Width, image.Height, NormalSettings.Describe(), clock.Elapsed.TotalSeconds));
            RhinoApp.WriteLine("Saved {0}", path);
            RhinoApp.WriteLine("Saved {0}", Path.ChangeExtension(path, ".json"));
            return true;
        }

        static string BuildJson(RhinoDoc doc, Rhino.Display.RhinoView view, string path, TraceImage image, DepthCamera camera,
            int format, bool cameraSpace, bool greenUp, string encoding, string background)
        {
            var sb = new StringBuilder();
            TraceCapture.JsonHead(sb, doc, view, path, image);
            sb.Append("  \"format\": ").Append(CaptureIO.Str(NormalSettings.Formats[format])).Append(",\n");
            sb.Append("  \"space\": ").Append(CaptureIO.Str(cameraSpace
                ? "camera: x right, y " + (greenUp ? "up" : "down") + ", z towards the viewer"
                : "world: X, Y, Z of the model")).Append(",\n");
            if (cameraSpace)
                sb.Append("  \"convention\": ").Append(CaptureIO.Str(greenUp ? "OpenGL (green up)" : "DirectX (green down)")).Append(",\n");
            sb.Append("  \"normals\": \"smooth, interpolated from the render mesh's vertex normals, facing the camera\",\n");
            sb.Append("  \"encoding\": ").Append(CaptureIO.Str(encoding)).Append(",\n");
            sb.Append("  \"background\": ").Append(CaptureIO.Str(background)).Append(",\n");
            sb.Append("  \"antialias\": ").Append(NormalSettings.AntiAlias).Append(",\n");
            TraceCapture.JsonCamera(sb, view, camera);
            return sb.ToString();
        }
    }
}
