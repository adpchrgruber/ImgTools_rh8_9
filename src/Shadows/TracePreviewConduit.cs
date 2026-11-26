using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Rhino;
using Rhino.Display;
using Rhino.Geometry;

namespace ImgTools
{
    /// <summary>The traced passes that have a live view.</summary>
    internal enum TracePass
    {
        Shadows,
        AmbientOcclusion,
        Normals,
    }

    /// <summary>
    /// The scene's triangles for the live views, shared by them and rebuilt (the BVH in the
    /// background) when the document changes.
    /// </summary>
    internal static class PreviewScene
    {
        static int _version;
        static int _builtVersion = -1;
        static Task<ShadowBvh> _bvh;
        static bool _hooked;

        public static int Version => _version;

        public static void Hook()
        {
            if (_hooked)
                return;
            _hooked = true;
            EventHandler<Rhino.DocObjects.RhinoObjectEventArgs> changed = (s, e) => _version++;
            RhinoDoc.AddRhinoObject += changed;
            RhinoDoc.DeleteRhinoObject += changed;
            RhinoDoc.UndeleteRhinoObject += changed;
            RhinoDoc.ModifyObjectAttributes += (s, e) => _version++;       // shown/hidden, casts/receives shadows
            RhinoDoc.LayerTableEvent += (s, e) => _version++;              // layer on/off
            RhinoDoc.InstanceDefinitionTableEvent += (s, e) => _version++; // block edits
            RhinoDoc.DocumentPropertiesChanged += (s, e) => _version++;    // render mesh quality
            RhinoDoc.EndOpenDocument += (s, e) => _version++;
            RhinoDoc.CloseDocument += (s, e) => _version++;
        }

        /// <summary>The current scene's BVH; collects meshes (UI thread) when the document changed.</summary>
        public static Task<ShadowBvh> Get(RhinoDoc doc)
        {
            if (_builtVersion != _version || _bvh == null)
            {
                var items = TraceScene.CollectItems(doc, null, out _, out _);
                _bvh = Task.Run(() => new ShadowBvh(items));
                _builtVersion = _version;
            }
            return _bvh;
        }
    }

    /// <summary>
    /// A live traced view. In viewports using its display mode it hides Rhino's own drawing and
    /// shows the pass instead, traced in the background with the same code and options as the
    /// capture command: first a quick low-resolution image, then the full one. Tracing restarts
    /// when the camera, the scene, the lights or the options change; a stale trace is cancelled.
    /// </summary>
    internal sealed class TracePreviewConduit : DisplayConduit
    {
        readonly Guid _modeId;
        readonly TracePass _pass;
        readonly Dictionary<Guid, ViewState> _views = new Dictionary<Guid, ViewState>();

        sealed class ViewState
        {
            public DisplayBitmap Bitmap;   // what is shown
            public string BitmapKey;
            public bool Final;
            public string JobKey;          // what is being traced
            public CancellationTokenSource Cancel;
        }

        public TracePreviewConduit(Guid modeId, TracePass pass)
        {
            _modeId = modeId;
            _pass = pass;
            PreviewScene.Hook();
            RhinoDoc.CloseDocument += (s, e) => ClearViews();
        }

        string Label => _pass == TracePass.Shadows ? "Shadows" : _pass == TracePass.AmbientOcclusion ? "AO" : "Normals";

        bool IsActive(RhinoViewport viewport) => viewport.DisplayMode?.Id == _modeId;

        // A capture's probe (ZDepth conduit) owns the frame; draw nothing then.
        static bool Probing => ImgToolsPlugin.Instance?.DepthConduit?.Probe != null;

        /// <summary>Colour where there is no surface: "no shadow" / "open sky".</summary>
        Color Background => _pass == TracePass.Shadows ? (ShadowSettings.ShadowIsWhite ? Color.Black : Color.White)
            : _pass == TracePass.AmbientOcclusion ? (AoSettings.OccludedIsBlack ? Color.White : Color.Black)
            : (NormalSettings.FlatBackground ? Color.FromArgb(128, 128, 255) : Color.Black);

        protected override void PreDrawObjects(DrawEventArgs e)
        {
            if (Probing || !IsActive(e.Viewport))
                return;
            // Rhino does not reliably apply the mode's solid fill in every view.
            var background = Background;
            var b = e.Viewport.Bounds;
            e.Display.Draw2dRectangle(new Rectangle(-2, -2, b.Width + 4, b.Height + 4), background, 0, background);
        }

        protected override void PreDrawObject(DrawObjectEventArgs e)
        {
            if (Probing || !IsActive(e.Viewport))
                return;
            e.DrawObject = false;
        }

        protected override void PostDrawObjects(DrawEventArgs e)
        {
            if (Probing || !IsActive(e.Viewport))
                return;
            var viewport = e.Viewport;
            var doc = e.RhinoDoc;
            var size = viewport.Size;
            if (doc == null || size.Width < 2 || size.Height < 2)
                return;

            if (!_views.TryGetValue(viewport.Id, out var state))
                _views[viewport.Id] = state = new ViewState();

            string problem = null;
            var lights = _pass == TracePass.Shadows
                ? ShadowLights.Collect(doc, ShadowSettings.Source, out problem)
                : new List<ShadowLight>();
            bool ready = _pass != TracePass.Shadows || lights.Count > 0;
            var camera = DepthCamera.FromViewport(viewport, size.Width, size.Height);
            string key = Key(doc, camera, lights);

            if (ready && !(state.BitmapKey == key && state.Final) && state.JobKey != key)
                Start(doc, viewport, state, camera, lights, key);

            if (state.Bitmap != null)
                e.Display.DrawBitmap(state.Bitmap, 0, BitmapTop(size.Height));

            string status = !ready ? problem
                : state.BitmapKey != key || !state.Final ? Label + ": tracing…"
                : null;
            if (status != null)
                e.Display.Draw2dText(status, Color.Gray, new Point2d(12, size.Height - 14), false, 12);
        }

        /// <summary>
        /// The y to pass to DrawBitmap for an image covering the viewport. Rhino 8 for Mac takes
        /// it as the image's bottom edge (Rhino 9 and Windows: the top edge).
        /// </summary>
        static int BitmapTop(int height) => BottomOrigin ? height : 0;

        static readonly bool BottomOrigin = RhinoApp.ExeVersion < 9 && Rhino.Runtime.HostUtils.RunningOnOSX;

        /// <summary>Everything the image depends on.</summary>
        string Key(RhinoDoc doc, DepthCamera c, List<ShadowLight> lights)
        {
            var sb = new StringBuilder();
            // Nine significant digits: Rhino's offscreen copies of a viewport (screenshots,
            // ViewCapture) differ from it in the last digits, which must not restart the trace.
            void N(double v) => sb.Append(v.ToString("G9", CultureInfo.InvariantCulture)).Append(';');
            N(c.Location.X); N(c.Location.Y); N(c.Location.Z);
            N(c.Direction.X); N(c.Direction.Y); N(c.Direction.Z);
            N(c.Y.X); N(c.Y.Y); N(c.Y.Z);
            N(c.Left); N(c.Right); N(c.Bottom); N(c.Top); N(c.Near); N(c.Far);
            sb.Append(c.Parallel).Append(c.Width).Append('x').Append(c.Height).Append('|').Append(PreviewScene.Version).Append('|');
            if (_pass == TracePass.Shadows)
            {
                foreach (var l in lights)
                {
                    N(l.ToLight.X); N(l.ToLight.Y); N(l.ToLight.Z);
                    N(l.Position.X); N(l.Position.Y); N(l.Position.Z);
                }
                sb.Append(ShadowSettings.FormShadows).Append(';');
                N(ShadowSettings.Softness);
                sb.Append(ShadowSettings.Rays).Append(ShadowSettings.ShadowIsWhite);
            }
            else if (_pass == TracePass.AmbientOcclusion)
            {
                N(AoSettings.Radius(doc));
                sb.Append(AoSettings.Rays).Append(AoSettings.OccludedIsBlack).Append(AoSettings.Denoise);
            }
            else
                sb.Append(NormalSettings.CameraSpace).Append(NormalSettings.GreenUp).Append(NormalSettings.FlatBackground);
            return sb.ToString();
        }

        void Start(RhinoDoc doc, RhinoViewport viewport, ViewState state, DepthCamera camera, List<ShadowLight> lights, string key)
        {
            state.Cancel?.Cancel();
            state.JobKey = key;

            // Meshes are read here, on the UI thread; the BVH is built in the background.
            var bvhTask = PreviewScene.Get(doc);
            var clipPlanes = DepthCapture.WorldClippingPlanes(doc, viewport);
            var cancel = new CancellationTokenSource();
            state.Cancel = cancel;
            bool quick = state.BitmapKey != key;   // nothing for this view yet: show a fast pass first
            var pass = _pass;
            bool form = ShadowSettings.FormShadows;
            double softness = ShadowSettings.Softness;
            int shadowRays = ShadowSettings.Rays;
            double radius = AoSettings.Radius(doc);
            int aoRays = AoSettings.Rays;
            bool aoDenoise = AoSettings.Denoise;
            // Grey = value, or 1 - value: shadow white / occlusion black are the defaults.
            bool invert = pass == TracePass.Shadows ? !ShadowSettings.ShadowIsWhite : !AoSettings.OccludedIsBlack;
            bool cameraSpace = NormalSettings.CameraSpace, greenUp = NormalSettings.GreenUp, flat = NormalSettings.FlatBackground;
            var viewportId = viewport.Id;
            uint docSerial = doc.RuntimeSerialNumber;

            Task.Run(async () =>
            {
                var bvh = await bvhTask.ConfigureAwait(false);
                if (cancel.IsCancellationRequested)
                    return;
                var origin = bvh.Origin;
                var clips = Array.ConvertAll(clipPlanes.ToArray(), p => (n: p.Normal, d: (origin - p.Origin) * p.Normal));
                Func<bool> cancelled = () => cancel.IsCancellationRequested;

                TraceImage Render(DepthCamera cam, bool fast)
                {
                    var scene = new TraceScene { Camera = cam, Bvh = bvh, Clips = clips, Eps = 1e-6 * bvh.Size };
                    return pass == TracePass.Shadows
                        ? ShadowRender.Render(scene, 1, lights, form, softness, fast ? Math.Min(shadowRays, 4) : shadowRays, cancelled)
                        : pass == TracePass.AmbientOcclusion
                        ? AoRender.Render(scene, 1, radius, fast ? Math.Min(aoRays, 4) : aoRays, cancelled, aoDenoise)
                        : NormalRender.Render(scene, 1, cameraSpace, greenUp, cancelled);
                }

                if (quick)
                {
                    // About a ninth of the pixels and few rays: there within a frame or two.
                    int f = 3;
                    var image = Render(Scaled(camera, Math.Max(2, camera.Width / f), Math.Max(2, camera.Height / f)), true);
                    if (image == null)
                        return;
                    Publish(docSerial, viewportId, key, false, Png(image, camera.Width, camera.Height, invert, flat), cancel);
                }
                var full = Render(camera, false);
                if (full == null)
                    return;
                Publish(docSerial, viewportId, key, true, Png(full, camera.Width, camera.Height, invert, flat), cancel);
            });
        }

        static DepthCamera Scaled(DepthCamera c, int width, int height) => new DepthCamera
        {
            Location = c.Location, X = c.X, Y = c.Y, Direction = c.Direction, Parallel = c.Parallel,
            Left = c.Left, Right = c.Right, Bottom = c.Bottom, Top = c.Top, Near = c.Near, Far = c.Far,
            Width = width, Height = height,
        };

        /// <summary>
        /// The image as an RGBA PNG of the viewport's size (a smaller quick pass is scaled up,
        /// nearest neighbour). Grey passes: value or 1 - value; the normal pass: its colours.
        /// </summary>
        static byte[] Png(TraceImage image, int width, int height, bool invert, bool flatBackground)
        {
            bool colour = image.Rgb != null;
            var encoded = colour ? NormalRender.To8(NormalRender.Encode(image, flatBackground)) : null;
            var rgb = new byte[3 * width * height];
            for (int y = 0; y < height; y++)
            {
                int sy = Math.Min(image.Height - 1, y * image.Height / height);
                for (int x = 0; x < width; x++)
                {
                    int src = sy * image.Width + Math.Min(image.Width - 1, x * image.Width / width);
                    int dst = 3 * (y * width + x);
                    if (colour)
                    {
                        rgb[dst] = encoded[3 * src];
                        rgb[dst + 1] = encoded[3 * src + 1];
                        rgb[dst + 2] = encoded[3 * src + 2];
                        continue;
                    }
                    double v = invert ? 1 - image.Value[src] : image.Value[src];
                    rgb[dst] = rgb[dst + 1] = rgb[dst + 2] = (byte)Math.Round(255 * (v < 0 ? 0 : v > 1 ? 1 : v));
                }
            }
            using (var stream = new MemoryStream())
            {
                ImageWriters.WritePngRgba(stream, width, height, rgb);
                return stream.ToArray();
            }
        }

        /// <summary>Hands a finished image to the UI thread, which turns it into a bitmap and redraws.</summary>
        void Publish(uint docSerial, Guid viewportId, string key, bool final, byte[] png, CancellationTokenSource cancel)
        {
            RhinoApp.InvokeOnUiThread(new Action(() =>
            {
                if (cancel.IsCancellationRequested || !_views.TryGetValue(viewportId, out var state) || state.JobKey != key)
                    return;
                DisplayBitmap bitmap;
                try
                {
                    using (var stream = new MemoryStream(png))
                    using (var image = new Bitmap(stream))
                        bitmap = new DisplayBitmap(image);
                }
                catch (Exception e)
                {
                    RhinoApp.WriteLine(Label + " view: could not show the image: " + e.Message);
                    return;
                }
                state.Bitmap?.Dispose();
                state.Bitmap = bitmap;
                state.BitmapKey = key;
                state.Final = final;
                if (final)
                    state.JobKey = null;
                var doc = RhinoDoc.FromRuntimeSerialNumber(docSerial);
                doc?.Views.Find(viewportId)?.Redraw();
            }));
        }

        void ClearViews()
        {
            foreach (var state in _views.Values)
            {
                state.Cancel?.Cancel();
                state.Bitmap?.Dispose();
            }
            _views.Clear();
        }
    }
}
