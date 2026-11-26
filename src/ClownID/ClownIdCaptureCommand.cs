using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Rhino;
using Rhino.Commands;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Input;
using Rhino.Input.Custom;

namespace ImgTools
{
    /// <summary>
    /// Captures the active view as a clean clown pass (PNG, one exact colour per object,
    /// black background) plus a JSON file mapping each colour to its object.
    /// </summary>
    public class ClownIdCaptureCommand : Command
    {
        public override string EnglishName => "ClownIDCapture";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var view = doc.Views.ActiveView;
            if (view == null)
                return Result.Failure;
            var clown = ClownDisplayMode.Ensure();
            if (clown == null)
                return Result.Failure;

            // Size: defaults to the viewport; match your render size so the passes line up.
            var size = view.ActiveViewport.Size;
            var width = new OptionInteger(size.Width, 16, 16384);
            var height = new OptionInteger(size.Height, 16, 16384);
            var go = new GetOption();
            go.SetCommandPrompt("Clown ID capture size. Press Enter to continue");
            go.AcceptNothing(true);
            go.AddOptionInteger("Width", ref width);
            go.AddOptionInteger("Height", ref height);
            while (true)
            {
                var res = go.Get();
                if (res == GetResult.Option)
                    continue;
                if (res == GetResult.Nothing)
                    break;
                return Result.Cancel;
            }

            var path = CaptureIO.GetOutputPath(doc, view, mode, "clownid", ".png", "Save Clown ID pass");
            if (path == null)
                return Result.Cancel;

            return Capture(doc, view, width.CurrentValue, height.CurrentValue, path) ? Result.Success : Result.Failure;
        }

        /// <summary>Captures the view at the size as a clean clown pass to path (PNG) plus its JSON.</summary>
        internal static bool Capture(RhinoDoc doc, RhinoView view, int width, int height, string path)
        {
            var clown = ClownDisplayMode.Ensure();
            if (clown == null)
                return false;
            // Capture through ViewCapture so grid and axes can be switched off.
            var viewport = view.ActiveViewport;
            var previous = viewport.DisplayMode;
            System.Drawing.Bitmap bitmap;
            try
            {
                viewport.DisplayMode = clown;
                var capture = new ViewCapture
                {
                    Width = width,
                    Height = height,
                    DrawGrid = false,
                    DrawAxes = false,
                    DrawGridAxes = false,
                    ScaleScreenItems = false,
                    TransparentBackground = false,
                };
                bitmap = capture.CaptureToBitmap(view);
            }
            finally
            {
                viewport.DisplayMode = previous;
                view.Redraw();
            }
            if (bitmap == null)
            {
                RhinoApp.WriteLine("Clown ID capture failed.");
                return false;
            }

            // Colour → objects, for snapping and for the JSON.
            var owners = new Dictionary<int, List<RhinoObject>>();
            var settings = new ObjectEnumeratorSettings { HiddenObjects = false, LockedObjects = true, IncludeLights = false, IncludeGrips = false };
            foreach (var obj in doc.Objects.GetObjectList(settings))
            {
                if (obj.IsInstanceDefinitionGeometry)
                    continue;
                int argb = ClownColors.Argb(ClownColors.For(obj.Id));
                if (!owners.TryGetValue(argb, out var list))
                    owners[argb] = list = new List<RhinoObject>();
                list.Add(obj);
            }
            int background = ClownColors.Argb(ClownColors.Background);
            var palette = new HashSet<int>(owners.Keys) { background };

            int snapped;
            var counts = new Dictionary<int, int>();
            int imageWidth, imageHeight;
            using (bitmap)
            using (var stream = new MemoryStream())
            {
                bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                stream.Position = 0;
                // Rhino's Mac System.Drawing cannot LockBits; Eto can.
                using (var image = new Eto.Drawing.Bitmap(stream))
                {
                    imageWidth = image.Width;
                    imageHeight = image.Height;
                    var pixels = new int[imageWidth * imageHeight];
                    if (!ReadPixels(image, pixels))
                    {
                        RhinoApp.WriteLine("Clown ID capture: unexpected pixel format.");
                        return false;
                    }
                    snapped = ClownMask.Snap(pixels, imageWidth, imageHeight, palette);
                    foreach (var p in pixels)
                        counts[p] = counts.TryGetValue(p, out var n) ? n + 1 : 1;
                    WritePixels(image, pixels);
                    image.Save(path, Eto.Drawing.ImageFormat.Png);
                }
            }

            CaptureIO.SaveJson(path, BuildJson(doc, view, path, imageWidth, imageHeight, snapped, background, owners, counts));

            int visible = owners.Keys.Count(k => counts.ContainsKey(k));
            int collisions = owners.Values.Count(l => l.Count > 1);
            RhinoApp.WriteLine(string.Format("Clown ID: {0}x{1}, {2} objects visible, {3} edge pixels snapped.", imageWidth, imageHeight, visible, snapped));
            if (collisions > 0)
                RhinoApp.WriteLine("Clown ID: {0} colour(s) shared by more than one object; see \"collisions\" in the JSON.", collisions);
            RhinoApp.WriteLine("Saved {0}", path);
            return true;
        }

        static bool ReadPixels(Eto.Drawing.Bitmap image, int[] pixels)
        {
            using (var data = image.Lock())
            {
                if (data.BytesPerPixel != 4)
                    return false;
                var row = new int[image.Width];
                for (int y = 0; y < image.Height; y++)
                {
                    Marshal.Copy(data.Data + y * data.ScanWidth, row, 0, row.Length);
                    for (int x = 0; x < row.Length; x++)
                        pixels[y * row.Length + x] = data.TranslateDataToArgb(row[x]) | unchecked((int)0xFF000000);
                }
            }
            return true;
        }

        static void WritePixels(Eto.Drawing.Bitmap image, int[] pixels)
        {
            using (var data = image.Lock())
            {
                var row = new int[image.Width];
                for (int y = 0; y < image.Height; y++)
                {
                    for (int x = 0; x < row.Length; x++)
                        row[x] = data.TranslateArgbToData(pixels[y * row.Length + x]);
                    Marshal.Copy(row, 0, data.Data + y * data.ScanWidth, row.Length);
                }
            }
        }

        static string BuildJson(RhinoDoc doc, RhinoView view, string imagePath, int width, int height, int snapped,
            int background, Dictionary<int, List<RhinoObject>> owners, Dictionary<int, int> counts)
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"image\": ").Append(CaptureIO.Str(Path.GetFileName(imagePath))).Append(",\n");
            sb.Append("  \"document\": ").Append(CaptureIO.Str(doc.Path ?? "")).Append(",\n");
            sb.Append("  \"view\": ").Append(CaptureIO.Str(view.ActiveViewport.Name)).Append(",\n");
            sb.Append("  \"width\": ").Append(width).Append(",\n");
            sb.Append("  \"height\": ").Append(height).Append(",\n");
            sb.Append("  \"background\": ").Append(CaptureIO.Str(ClownColors.Hex(background))).Append(",\n");
            sb.Append("  \"backgroundPixels\": ").Append(counts.TryGetValue(background, out var bg) ? bg : 0).Append(",\n");
            sb.Append("  \"snappedEdgePixels\": ").Append(snapped).Append(",\n");

            var visible = owners
                .Where(kv => counts.ContainsKey(kv.Key))
                .SelectMany(kv => kv.Value.Select(obj => (color: kv.Key, obj, pixels: counts[kv.Key])))
                .OrderByDescending(t => t.pixels)
                .ToList();
            sb.Append("  \"objects\": [");
            for (int i = 0; i < visible.Count; i++)
            {
                var (color, obj, pixels) = visible[i];
                var layer = obj.Attributes.LayerIndex >= 0 && obj.Attributes.LayerIndex < doc.Layers.Count
                    ? doc.Layers[obj.Attributes.LayerIndex].FullPath : "";
                sb.Append(i == 0 ? "\n" : ",\n");
                sb.Append("    {\"color\": ").Append(CaptureIO.Str(ClownColors.Hex(color)))
                  .Append(", \"id\": ").Append(CaptureIO.Str(obj.Id.ToString()))
                  .Append(", \"name\": ").Append(CaptureIO.Str(obj.Attributes.Name ?? ""))
                  .Append(", \"layer\": ").Append(CaptureIO.Str(layer))
                  .Append(", \"type\": ").Append(CaptureIO.Str(obj.ObjectType.ToString()))
                  .Append(", \"pixels\": ").Append(pixels.ToString(CultureInfo.InvariantCulture))
                  .Append('}');
            }
            sb.Append(visible.Count > 0 ? "\n  ],\n" : "],\n");

            var shared = owners.Where(kv => kv.Value.Count > 1).ToList();
            sb.Append("  \"collisions\": [");
            for (int i = 0; i < shared.Count; i++)
            {
                sb.Append(i == 0 ? "\n" : ",\n");
                sb.Append("    {\"color\": ").Append(CaptureIO.Str(ClownColors.Hex(shared[i].Key))).Append(", \"ids\": [")
                  .Append(string.Join(", ", shared[i].Value.Select(o => CaptureIO.Str(o.Id.ToString())))).Append("]}");
            }
            sb.Append(shared.Count > 0 ? "\n  ]\n" : "]\n");
            sb.Append("}\n");
            return sb.ToString();
        }

    }
}
