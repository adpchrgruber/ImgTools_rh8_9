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
    /// <summary>
    /// Saves the raw shadows of the active view as a greyscale PNG: the shadows the sun
    /// and/or the document's lights cast, without materials, textures or shading. Hard by
    /// default; with Softness the lights get a size and the shadows soft edges. A JSON file
    /// next to the image records lights, encoding and camera. Like ClownIDCapture and
    /// ZDepthCapture it lines up pixel for pixel with renders of the same size.
    /// </summary>
    public class ShadowCaptureCommand : Command
    {
        // Options live in ShadowSettings, shared with the live Shadows view.
        static int _source { get => ShadowSettings.Source; set => ShadowSettings.Source = value; }
        static bool _formShadows { get => ShadowSettings.FormShadows; set => ShadowSettings.FormShadows = value; }
        static bool _shadowIsWhite { get => ShadowSettings.ShadowIsWhite; set => ShadowSettings.ShadowIsWhite = value; }
        static int _antialias { get => ShadowSettings.AntiAlias; set => ShadowSettings.AntiAlias = value; }
        static double _softness { get => ShadowSettings.Softness; set => ShadowSettings.Softness = value; }
        static int _rays { get => ShadowSettings.Rays; set => ShadowSettings.Rays = value; }

        public override string EnglishName => "ShadowCapture";

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
                go.SetCommandPrompt(string.Format(CultureInfo.InvariantCulture,
                    "Shadow capture ({0}). Press Enter to save", _softness > 0 ? "soft, " + _softness.ToString("0.##", CultureInfo.InvariantCulture) + "°" : "hard"));
                go.AcceptNothing(true);
                var form = new OptionToggle(_formShadows, "CastOnly", "CastAndForm");
                var white = new OptionToggle(_shadowIsWhite, "Black", "White");
                var aa = new OptionInteger(_antialias, 1, 4);
                var softness = new OptionDouble(_softness, 0, 45);
                var rays = new OptionInteger(_rays, 1, 1024);
                int sourceIndex = go.AddOptionList("Source", ShadowLights.Sources, _source);
                int formIndex = go.AddOptionToggle("Shadows", ref form);
                int softIndex = go.AddOptionDouble("Softness", ref softness, "Light size in degrees (0 = hard, sun ≈ 0.53)");
                int raysIndex = _softness > 0 ? go.AddOptionInteger("Rays", ref rays, "Shadow rays per pixel per light") : -1;
                int whiteIndex = go.AddOptionToggle("ShadowIs", ref white);
                int aaIndex = go.AddOptionInteger("AntiAlias", ref aa);
                go.AddOptionInteger("Width", ref width);
                go.AddOptionInteger("Height", ref height);
                var res = go.Get();
                if (res == GetResult.Option)
                {
                    int index = go.Option().Index;
                    if (index == sourceIndex) _source = go.Option().CurrentListOptionIndex;
                    else if (index == formIndex) _formShadows = form.CurrentValue;
                    else if (index == softIndex) _softness = softness.CurrentValue;
                    else if (index == raysIndex) _rays = rays.CurrentValue;
                    else if (index == whiteIndex) _shadowIsWhite = white.CurrentValue;
                    else if (index == aaIndex) _antialias = aa.CurrentValue;
                    doc.Views.Redraw();   // the Shadows view follows these options
                    continue;
                }
                if (res == GetResult.Nothing)
                    break;
                return Result.Cancel;
            }
            int w = width.CurrentValue, h = height.CurrentValue;
            if (!TraceCapture.CheckSize(w, h, "Shadow capture"))
                return Result.Failure;

            var lights = ShadowLights.Collect(doc, _source, out var problem);
            if (lights.Count == 0)
            {
                RhinoApp.WriteLine("Shadow capture: " + problem);
                return Result.Failure;
            }
            if (problem != null)
                RhinoApp.WriteLine("Shadow capture: " + problem);

            var path = CaptureIO.GetOutputPath(doc, view, mode, "shadows", ".png", "Save shadow pass");
            if (path == null)
                return Result.Cancel;

            RhinoApp.WriteLine(string.Format("Shadow capture: tracing {0}x{1} at {2}x{2} samples, {3} light(s)...", w, h, _antialias, lights.Count));
            var scene = TraceScene.Build(doc, view, w, h);
            if (scene == null)
            {
                RhinoApp.WriteLine("Shadow capture failed.");
                return Result.Failure;
            }
            return Capture(doc, view, scene, path, lights) ? Result.Success : Result.Failure;
        }

        /// <summary>Traces the shadow pass with the current options into path (PNG) plus its JSON.</summary>
        internal static bool Capture(RhinoDoc doc, RhinoView view, TraceScene scene, string path, IList<ShadowLight> lights = null)
        {
            if (lights == null)
            {
                lights = ShadowLights.Collect(doc, _source, out var problem);
                if (lights.Count == 0)
                {
                    RhinoApp.WriteLine("Shadow capture: " + problem);
                    return false;
                }
            }
            var clock = Stopwatch.StartNew();
            var image = ShadowRender.Render(scene, _antialias, lights, _formShadows, _softness, _rays);

            // Shadow amount to grey; background and lit surfaces are "no shadow".
            long shadowed = 0;
            foreach (var v in image.Value)
                if (v > 0.5)
                    shadowed++;
            var json = BuildJson(doc, view, path, image, scene.Camera, lights);
            if (!TraceCapture.Save(path, image, invert: !_shadowIsWhite, json, "Shadow capture"))
                return false;

            RhinoApp.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "Shadow capture: {0}x{1}, {2:0.#}% of the image in shadow, {3} triangles, {4:0.0} s.",
                image.Width, image.Height, 100.0 * shadowed / image.Value.Length, image.Triangles, clock.Elapsed.TotalSeconds));
            RhinoApp.WriteLine("Saved {0}", path);
            return true;
        }

        static string BuildJson(RhinoDoc doc, RhinoView view, string imagePath, TraceImage image, DepthCamera camera, IList<ShadowLight> lights)
        {
            var sb = new StringBuilder();
            TraceCapture.JsonHead(sb, doc, view, imagePath, image);
            sb.Append("  \"source\": ").Append(CaptureIO.Str(ShadowLights.Sources[_source])).Append(",\n");
            sb.Append("  \"shadows\": ").Append(CaptureIO.Str(_formShadows
                ? "cast shadows and surfaces turned away from the light"
                : "cast shadows only")).Append(",\n");
            sb.Append("  \"softness\": ").Append(CaptureIO.Num(_softness)).Append(",\n");
            sb.Append("  \"softnessUnit\": \"degrees, angular diameter of each light (0 = hard)\",\n");
            if (_softness > 0)
                sb.Append("  \"rays\": ").Append(_rays).Append(",\n");
            sb.Append("  \"encoding\": ").Append(CaptureIO.Str(_shadowIsWhite
                ? "grey = 255 * share of light blocked (white = shadow, black = lit)"
                : "grey = 255 * share of light reaching (black = shadow, white = lit)")).Append(",\n");
            sb.Append("  \"background\": ").Append(CaptureIO.Str(_shadowIsWhite ? "0 (black, no shadow)" : "255 (white, no shadow)")).Append(",\n");
            sb.Append("  \"antialias\": ").Append(_antialias).Append(",\n");
            sb.Append("  \"lights\": [");
            for (int i = 0; i < lights.Count; i++)
            {
                var l = lights[i];
                sb.Append(i == 0 ? "\n" : ",\n");
                sb.Append("    {\"name\": ").Append(CaptureIO.Str(l.Name ?? ""));
                if (l.Directional)
                    sb.Append(", \"type\": \"directional\", \"towardsLight\": ").Append(CaptureIO.Vec(l.ToLight));
                else
                    sb.Append(", \"type\": ").Append(CaptureIO.Str(l.SpotAxis.IsZero ? "point" : "spot"))
                      .Append(", \"position\": ").Append(CaptureIO.Vec(l.Position));
                sb.Append('}');
            }
            sb.Append(lights.Count > 0 ? "\n  ],\n" : "],\n");
            TraceCapture.JsonCamera(sb, view, camera);
            return sb.ToString();
        }
    }

    /// <summary>
    /// Saves an ambient occlusion pass of the active view: white where a point sees open sky,
    /// darker where nearby geometry hides it, so creases, corners and contact areas get soft
    /// gradients. Independent of lights. Multiply it over a render, or over the shadow pass.
    /// </summary>
    public class AOCaptureCommand : Command
    {
        // Options live in AoSettings, shared with the live AO view.
        static int _rays { get => AoSettings.Rays; set => AoSettings.Rays = value; }
        static int _antialias { get => AoSettings.AntiAlias; set => AoSettings.AntiAlias = value; }
        static bool _occludedIsBlack { get => AoSettings.OccludedIsBlack; set => AoSettings.OccludedIsBlack = value; }

        public override string EnglishName => "AOCapture";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var view = doc.Views.ActiveView;
            if (view == null || ImgToolsPlugin.Instance?.DepthConduit == null)
                return Result.Failure;

            double _radius = AoSettings.Radius(doc);
            var size = view.ActiveViewport.Size;
            var width = new OptionInteger(size.Width, 16, 16384);
            var height = new OptionInteger(size.Height, 16, 16384);
            while (true)
            {
                var go = new GetOption();
                go.SetCommandPrompt(string.Format(CultureInfo.InvariantCulture, "Ambient occlusion capture (radius {0:G4} {1}). Press Enter to save",
                    _radius, doc.GetUnitSystemName(true, true, false, true)));
                go.AcceptNothing(true);
                var radius = new OptionDouble(_radius, 1e-9, 1e12);
                var rays = new OptionInteger(_rays, 1, 4096);
                var aa = new OptionInteger(_antialias, 1, 4);
                var black = new OptionToggle(_occludedIsBlack, "White", "Black");
                var denoise = new OptionToggle(AoSettings.Denoise, "Off", "On");
                int radiusIndex = go.AddOptionDouble("Radius", ref radius, "How far geometry still occludes, in model units");
                int raysIndex = go.AddOptionInteger("Rays", ref rays, "Rays per pixel");
                int blackIndex = go.AddOptionToggle("OccludedIs", ref black);
                int denoiseIndex = go.AddOptionToggle("Denoise", ref denoise);
                int aaIndex = go.AddOptionInteger("AntiAlias", ref aa);
                go.AddOptionInteger("Width", ref width);
                go.AddOptionInteger("Height", ref height);
                var res = go.Get();
                if (res == GetResult.Option)
                {
                    int index = go.Option().Index;
                    if (index == radiusIndex) AoSettings.SetRadius(doc, _radius = radius.CurrentValue);
                    else if (index == raysIndex) _rays = rays.CurrentValue;
                    else if (index == blackIndex) _occludedIsBlack = black.CurrentValue;
                    else if (index == denoiseIndex) AoSettings.Denoise = denoise.CurrentValue;
                    else if (index == aaIndex) _antialias = aa.CurrentValue;
                    doc.Views.Redraw();   // the AO view follows these options
                    continue;
                }
                if (res == GetResult.Nothing)
                    break;
                return Result.Cancel;
            }
            int w = width.CurrentValue, h = height.CurrentValue;
            if (!TraceCapture.CheckSize(w, h, "AO capture"))
                return Result.Failure;

            var path = CaptureIO.GetOutputPath(doc, view, mode, "ao", ".png", "Save ambient occlusion pass");
            if (path == null)
                return Result.Cancel;

            RhinoApp.WriteLine(string.Format("AO capture: tracing {0}x{1} at {2}x{2} samples, {3} rays per pixel...", w, h, _antialias, _rays));
            var scene = TraceScene.Build(doc, view, w, h);
            if (scene == null)
            {
                RhinoApp.WriteLine("AO capture failed.");
                return Result.Failure;
            }
            return Capture(doc, view, scene, path) ? Result.Success : Result.Failure;
        }

        /// <summary>Traces the AO pass with the current options into path (PNG) plus its JSON.</summary>
        internal static bool Capture(RhinoDoc doc, RhinoView view, TraceScene scene, string path)
        {
            double _radius = AoSettings.Radius(doc);
            var clock = Stopwatch.StartNew();
            var image = AoRender.Render(scene, _antialias, _radius, _rays, denoise: AoSettings.Denoise);

            var sb = new StringBuilder();
            TraceCapture.JsonHead(sb, doc, view, path, image);
            sb.Append("  \"radius\": ").Append(CaptureIO.Num(_radius)).Append(",\n");
            sb.Append("  \"units\": ").Append(CaptureIO.Str(doc.ModelUnitSystem.ToString())).Append(",\n");
            sb.Append("  \"rays\": ").Append(_rays).Append(",\n");
            sb.Append("  \"falloff\": \"a hit at distance d occludes by 1 - d / radius\",\n");
            sb.Append("  \"denoise\": ").Append(AoSettings.Denoise ? "\"edge-aware (depth and normal guided), 2 passes\"" : "false").Append(",\n");
            sb.Append("  \"encoding\": ").Append(CaptureIO.Str(_occludedIsBlack
                ? "grey = 255 * openness (white = open, black = occluded)"
                : "grey = 255 * occlusion (black = open, white = occluded)")).Append(",\n");
            sb.Append("  \"background\": ").Append(CaptureIO.Str(_occludedIsBlack ? "255 (white, open)" : "0 (black, open)")).Append(",\n");
            sb.Append("  \"antialias\": ").Append(_antialias).Append(",\n");
            TraceCapture.JsonCamera(sb, view, scene.Camera);
            if (!TraceCapture.Save(path, image, invert: !_occludedIsBlack, sb.ToString(), "AO capture"))
                return false;

            RhinoApp.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "AO capture: {0}x{1}, radius {2:G4}, {3} triangles, {4:0.0} s.",
                image.Width, image.Height, _radius, image.Triangles, clock.Elapsed.TotalSeconds));
            RhinoApp.WriteLine("Saved {0}", path);
            return true;
        }
    }

    /// <summary>Saving and describing the traced passes.</summary>
    internal static class TraceCapture
    {
        const long MaxPixels = 100000000;

        public static bool CheckSize(int w, int h, string what)
        {
            if ((long)w * h <= MaxPixels)
                return true;
            RhinoApp.WriteLine(string.Format("{0}: {1}x{2} is too large; at most {3} million pixels.", what, w, h, MaxPixels / 1000000));
            return false;
        }

        /// <summary>Writes value (0..1) as 8-bit grey, or 1 - value when inverted, plus the JSON.</summary>
        public static bool Save(string path, TraceImage image, bool invert, string json, string what)
        {
            var pixels = new byte[image.Value.Length];
            for (int i = 0; i < pixels.Length; i++)
            {
                double v = image.Value[i];
                if (invert)
                    v = 1 - v;
                pixels[i] = (byte)Math.Round(255 * (v < 0 ? 0 : v > 1 ? 1 : v));
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                ImageWriters.WritePng8(path, image.Width, image.Height, pixels);
                CaptureIO.SaveJson(path, json);
                return true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                RhinoApp.WriteLine("{0}: could not save {1}: {2}", what, path, e.Message);
                return false;
            }
        }

        public static void JsonHead(StringBuilder sb, RhinoDoc doc, RhinoView view, string imagePath, TraceImage image)
        {
            sb.Append("{\n");
            sb.Append("  \"image\": ").Append(CaptureIO.Str(Path.GetFileName(imagePath))).Append(",\n");
            sb.Append("  \"document\": ").Append(CaptureIO.Str(doc.Path ?? "")).Append(",\n");
            sb.Append("  \"view\": ").Append(CaptureIO.Str(view.ActiveViewport.Name)).Append(",\n");
            sb.Append("  \"width\": ").Append(image.Width).Append(",\n");
            sb.Append("  \"height\": ").Append(image.Height).Append(",\n");
            sb.Append("  \"triangles\": ").Append(image.Triangles).Append(",\n");
        }

        public static void JsonCamera(StringBuilder sb, RhinoView view, DepthCamera camera)
        {
            sb.Append("  \"camera\": {\n");
            sb.Append("    \"projection\": ").Append(CaptureIO.Str(camera.Parallel ? "parallel" : "perspective")).Append(",\n");
            sb.Append("    \"location\": ").Append(CaptureIO.Vec(camera.Location)).Append(",\n");
            sb.Append("    \"direction\": ").Append(CaptureIO.Vec(camera.Direction)).Append(",\n");
            sb.Append("    \"up\": ").Append(CaptureIO.Vec(camera.Y)).Append(",\n");
            sb.Append("    \"lensLength\": ").Append(CaptureIO.Num(view.ActiveViewport.Camera35mmLensLength)).Append("\n");
            sb.Append("  }\n");
            sb.Append("}\n");
        }
    }
}
