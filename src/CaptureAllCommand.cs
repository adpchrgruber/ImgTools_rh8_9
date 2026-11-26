using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Rhino;
using Rhino.Commands;
using Rhino.Display;
using Rhino.Input;
using Rhino.Input.Custom;

namespace ImgTools
{
    /// <summary>Which passes CaptureAll saves; saved with Rhino's settings.</summary>
    internal static class CaptureAllSettings
    {
        static PersistentSettings S => ImgToolsPlugin.Instance?.Settings;

        static bool Get(string key) => S?.GetBool("All." + key, true) ?? true;

        static void Set(string key, bool value) => S?.SetBool("All." + key, value);

        public static bool View { get => Get("View"); set => Set("View", value); }
        public static bool Clown { get => Get("Clown"); set => Set("Clown", value); }
        public static bool Depth { get => Get("Depth"); set => Set("Depth", value); }
        public static bool Shadows { get => Get("Shadows"); set => Set("Shadows", value); }
        public static bool Ao { get => Get("AO"); set => Set("AO", value); }
        public static bool Normals { get => Get("Normals"); set => Set("Normals", value); }
    }

    /// <summary>
    /// Saves the active view and every pass in one go, all at the same size and from the same
    /// camera, side by side with matching names: &lt;name&gt;_view.png (the view as it looks),
    /// _clownid, _zdepth, _shadows, _ao and _normal, each with its JSON, plus &lt;name&gt;_passes.json
    /// listing them. Each pass uses its own options (ImgToolsOptions, ZDepthRange).
    /// </summary>
    public class CaptureAllCommand : Command
    {
        public override string EnglishName => "CaptureAll";

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
                go.SetCommandPrompt("Capture all passes. Press Enter to save");
                go.AcceptNothing(true);
                var v = new OptionToggle(CaptureAllSettings.View, "Off", "On");
                var c = new OptionToggle(CaptureAllSettings.Clown, "Off", "On");
                var d = new OptionToggle(CaptureAllSettings.Depth, "Off", "On");
                var s = new OptionToggle(CaptureAllSettings.Shadows, "Off", "On");
                var a = new OptionToggle(CaptureAllSettings.Ao, "Off", "On");
                var n = new OptionToggle(CaptureAllSettings.Normals, "Off", "On");
                var j = new OptionToggle(CaptureIO.WriteJson, "Off", "On");
                int vi = go.AddOptionToggle("View", ref v);
                int ci = go.AddOptionToggle("ClownID", ref c);
                int di = go.AddOptionToggle("ZDepth", ref d);
                int si = go.AddOptionToggle("Shadows", ref s);
                int ai = go.AddOptionToggle("AO", ref a);
                int ni = go.AddOptionToggle("Normals", ref n);
                int ji = go.AddOptionToggle("Json", ref j);
                go.AddOptionInteger("Width", ref width);
                go.AddOptionInteger("Height", ref height);
                var res = go.Get();
                if (res == GetResult.Option)
                {
                    int index = go.Option().Index;
                    if (index == vi) CaptureAllSettings.View = v.CurrentValue;
                    else if (index == ci) CaptureAllSettings.Clown = c.CurrentValue;
                    else if (index == di) CaptureAllSettings.Depth = d.CurrentValue;
                    else if (index == si) CaptureAllSettings.Shadows = s.CurrentValue;
                    else if (index == ai) CaptureAllSettings.Ao = a.CurrentValue;
                    else if (index == ni) CaptureAllSettings.Normals = n.CurrentValue;
                    else if (index == ji) CaptureIO.WriteJson = j.CurrentValue;
                    continue;
                }
                if (res == GetResult.Nothing)
                    break;
                return Result.Cancel;
            }
            int w = width.CurrentValue, h = height.CurrentValue;
            if (!TraceCapture.CheckSize(w, h, "Capture all"))
                return Result.Failure;

            // One name for the set: <name>_view.png is what the dialog suggests; the others follow it.
            var chosen = CaptureIO.GetOutputPath(doc, view, mode, "view", ".png", "Save all passes (name of the set)");
            if (chosen == null)
                return Result.Cancel;
            var folder = Path.GetDirectoryName(chosen);
            var stem = Path.GetFileNameWithoutExtension(chosen);
            if (stem.EndsWith("_view", StringComparison.OrdinalIgnoreCase))
                stem = stem.Substring(0, stem.Length - "_view".Length);
            string PathFor(string suffix, string extension) => Path.Combine(folder, stem + "_" + suffix + extension);

            var clock = Stopwatch.StartNew();
            var saved = new List<(string Pass, string File)>();
            var failed = new List<string>();
            void Run(string pass, string file, Func<bool> capture)
            {
                RhinoApp.WriteLine("Capture all: {0}...", pass);
                RhinoApp.Wait();
                bool ok;
                try { ok = capture(); }
                catch (Exception e) { RhinoApp.WriteLine("Capture all: {0} failed: {1}", pass, e.Message); ok = false; }
                if (ok) saved.Add((pass, Path.GetFileName(file)));
                else failed.Add(pass);
            }

            Directory.CreateDirectory(folder);
            if (CaptureAllSettings.View)
            {
                var file = PathFor("view", ".png");
                Run("view", file, () => SaveView(view, w, h, file));
            }
            if (CaptureAllSettings.Clown)
            {
                var file = PathFor("clownid", ".png");
                Run("clown ID", file, () => ClownIdCaptureCommand.Capture(doc, view, w, h, file));
            }
            if (CaptureAllSettings.Depth)
            {
                int format = ZDepthCaptureCommand.Format;
                var file = PathFor("zdepth", format == 2 ? ".exr" : ".png");
                Run("Z depth", file, () => ZDepthCaptureCommand.Capture(doc, view, w, h, file, format));
            }
            if (CaptureAllSettings.Shadows || CaptureAllSettings.Ao || CaptureAllSettings.Normals)
            {
                // One scene for the three traced passes.
                RhinoApp.WriteLine("Capture all: preparing the scene...");
                RhinoApp.Wait();
                var scene = TraceScene.Build(doc, view, w, h);
                if (scene == null)
                {
                    RhinoApp.WriteLine("Capture all: could not prepare the scene for shadows, AO and normals.");
                    failed.Add("shadows/AO/normals");
                }
                else
                {
                    if (CaptureAllSettings.Shadows)
                    {
                        var file = PathFor("shadows", ".png");
                        Run("shadows", file, () => ShadowCaptureCommand.Capture(doc, view, scene, file));
                    }
                    if (CaptureAllSettings.Ao)
                    {
                        var file = PathFor("ao", ".png");
                        Run("ambient occlusion", file, () => AOCaptureCommand.Capture(doc, view, scene, file));
                    }
                    if (CaptureAllSettings.Normals)
                    {
                        var file = PathFor("normal", NormalSettings.Format == NormalSettings.Exr ? ".exr" : ".png");
                        Run("normals", file, () => NormalCaptureCommand.Capture(doc, view, scene, file));
                    }
                }
            }

            var manifest = PathFor("passes", ".json");
            try
            {
                if (CaptureIO.WriteJson)
                    File.WriteAllText(manifest, Manifest(doc, view, w, h, saved));
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                RhinoApp.WriteLine("Capture all: could not save {0}: {1}", manifest, e.Message);
            }

            RhinoApp.WriteLine(string.Format(CultureInfo.InvariantCulture, "Capture all: {0} pass(es) saved in {1:0.0} s to {2}{3}",
                saved.Count, clock.Elapsed.TotalSeconds, folder,
                failed.Count > 0 ? "; failed: " + string.Join(", ", failed) : "."));
            return failed.Count == 0 ? Result.Success : Result.Failure;
        }

        /// <summary>The view as it looks (its current display mode), without grid and axes.</summary>
        static bool SaveView(RhinoView view, int width, int height, string path)
        {
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
            using (var bitmap = capture.CaptureToBitmap(view))
            {
                if (bitmap == null)
                    return false;
                bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
            RhinoApp.WriteLine("Saved {0}", path);
            return true;
        }

        static string Manifest(RhinoDoc doc, RhinoView view, int width, int height, List<(string Pass, string File)> saved)
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"document\": ").Append(CaptureIO.Str(doc.Path ?? "")).Append(",\n");
            sb.Append("  \"view\": ").Append(CaptureIO.Str(view.ActiveViewport.Name)).Append(",\n");
            sb.Append("  \"displayMode\": ").Append(CaptureIO.Str(view.ActiveViewport.DisplayMode?.EnglishName ?? "")).Append(",\n");
            sb.Append("  \"width\": ").Append(width).Append(",\n");
            sb.Append("  \"height\": ").Append(height).Append(",\n");
            sb.Append("  \"passes\": [");
            for (int i = 0; i < saved.Count; i++)
            {
                sb.Append(i == 0 ? "\n" : ",\n");
                sb.Append("    {\"pass\": ").Append(CaptureIO.Str(saved[i].Pass)).Append(", \"file\": ").Append(CaptureIO.Str(saved[i].File)).Append('}');
            }
            sb.Append(saved.Count > 0 ? "\n  ]\n" : "]\n");
            sb.Append("}\n");
            return sb.ToString();
        }
    }
}
