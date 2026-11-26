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
    /// <summary>
    /// The live Shadows view. In viewports using the "Shadows" display mode it hides Rhino's
    /// own drawing and shows the shadow pass instead, traced in the background with the same
    /// code and options as ShadowCapture: first a quick low-resolution pass, then the full one.
    /// Tracing restarts when the camera, the scene, the lights or the options change; a stale
    /// trace is cancelled.
    /// </summary>
    internal sealed class ShadowPreviewConduit : DisplayConduit
    {
        readonly Guid _modeId;

        // The scene's triangles, rebuilt (BVH in the background) when the document changes.
        int _version;
        int _builtVersion = -1;
        Task<ShadowBvh> _bvh;
        int _casters, _receivers;

        readonly Dictionary<Guid, ViewState> _views = new Dictionary<Guid, ViewState>();

        sealed class ViewState
        {
            public DisplayBitmap Bitmap;   // what is shown
            public string BitmapKey;
            public bool Final;
            public string JobKey;          // what is being traced
            public CancellationTokenSource Cancel;
        }

        public ShadowPreviewConduit(Guid modeId)
        {
            _modeId = modeId;
            EventHandler<Rhino.DocObjects.RhinoObjectEventArgs> changed = (s, e) => Invalidate();
            RhinoDoc.AddRhinoObject += changed;
            RhinoDoc.DeleteRhinoObject += changed;
            RhinoDoc.UndeleteRhinoObject += changed;
            RhinoDoc.ModifyObjectAttributes += (s, e) => Invalidate();       // shown/hidden, casts/receives shadows
            RhinoDoc.LayerTableEvent += (s, e) => Invalidate();              // layer on/off
            RhinoDoc.InstanceDefinitionTableEvent += (s, e) => Invalidate(); // block edits
            RhinoDoc.DocumentPropertiesChanged += (s, e) => Invalidate();    // render mesh quality
            RhinoDoc.EndOpenDocument += (s, e) => Invalidate();
            RhinoDoc.CloseDocument += (s, e) => { Invalidate(); ClearViews(); };
        }

        void Invalidate() => _version++;

        bool IsActive(RhinoViewport viewport) => viewport.DisplayMode?.Id == _modeId;

        // A capture's probe (ZDepth conduit) owns the frame; draw nothing then.
        static bool Probing => ImgToolsPlugin.Instance?.DepthConduit?.Probe != null;

        protected override void PreDrawObjects(DrawEventArgs e)
        {
            if (Probing || !IsActive(e.Viewport))
                return;
            // Background = "no shadow". Rhino does not reliably apply the mode's solid fill in every view.
            var background = ShadowSettings.ShadowIsWhite ? Color.Black : Color.White;
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

            var lights = ShadowLights.Collect(doc, ShadowSettings.Source, out var problem);
            var camera = DepthCamera.FromViewport(viewport, size.Width, size.Height);
            string key = Key(camera, lights);

            bool upToDate = state.BitmapKey == key && state.Final;
            if (!upToDate && state.JobKey != key)
                Start(doc, viewport, state, camera, lights, key);

            if (state.Bitmap != null)
                e.Display.DrawBitmap(state.Bitmap, 0, 0);

            string status = lights.Count == 0 ? problem
                : state.BitmapKey != key || !state.Final ? "Shadows: tracing…"
                : null;
            if (status != null)
                e.Display.Draw2dText(status, ShadowSettings.ShadowIsWhite ? Color.Gray : Color.DimGray, new Point2d(12, size.Height - 14), false, 12);
        }

        /// <summary>Everything the image depends on.</summary>
        string Key(DepthCamera c, List<ShadowLight> lights)
        {
            var sb = new StringBuilder();
            // Nine significant digits: Rhino's offscreen copies of a viewport (screenshots,
            // ViewCapture) differ from it in the last digits, which must not restart the trace.
            void N(double v) => sb.Append(v.ToString("G9", CultureInfo.InvariantCulture)).Append(';');
            N(c.Location.X); N(c.Location.Y); N(c.Location.Z);
            N(c.Direction.X); N(c.Direction.Y); N(c.Direction.Z);
            N(c.Y.X); N(c.Y.Y); N(c.Y.Z);
            N(c.Left); N(c.Right); N(c.Bottom); N(c.Top); N(c.Near); N(c.Far);
            sb.Append(c.Parallel).Append(c.Width).Append('x').Append(c.Height).Append('|');
            foreach (var l in lights)
            {
                N(l.ToLight.X); N(l.ToLight.Y); N(l.ToLight.Z);
                N(l.Position.X); N(l.Position.Y); N(l.Position.Z);
            }
            sb.Append('|').Append(_version).Append('|')
              .Append(ShadowSettings.FormShadows).Append(ShadowSettings.Softness).Append(';')
              .Append(ShadowSettings.Rays).Append(ShadowSettings.ShadowIsWhite);
            return sb.ToString();
        }

        void Start(RhinoDoc doc, RhinoViewport viewport, ViewState state, DepthCamera camera, List<ShadowLight> lights, string key)
        {
            state.Cancel?.Cancel();
            state.JobKey = key;
            if (lights.Count == 0)
                return;

            // Meshes are read here, on the UI thread; the BVH is built in the background.
            if (_builtVersion != _version || _bvh == null)
            {
                var items = TraceScene.CollectItems(doc, null, out _casters, out _receivers);
                _bvh = Task.Run(() => new ShadowBvh(items));
                _builtVersion = _version;
            }
            var bvhTask = _bvh;
            var clipPlanes = DepthCapture.WorldClippingPlanes(doc, viewport);
            var cancel = new CancellationTokenSource();
            state.Cancel = cancel;
            bool quick = state.BitmapKey != key;   // nothing for this view yet: show a fast pass first
            bool form = ShadowSettings.FormShadows, white = ShadowSettings.ShadowIsWhite;
            double softness = ShadowSettings.Softness;
            int rays = ShadowSettings.Rays;
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

                if (quick)
                {
                    // About a ninth of the pixels and few rays: there within a frame or two.
                    int f = 3;
                    var small = Scaled(camera, Math.Max(2, camera.Width / f), Math.Max(2, camera.Height / f));
                    var scene = new TraceScene { Camera = small, Bvh = bvh, Clips = clips, Eps = 1e-6 * bvh.Size };
                    var image = ShadowRender.Render(scene, 1, lights, form, softness, Math.Min(rays, 4), cancelled);
                    if (image == null)
                        return;
                    Publish(docSerial, viewportId, key, false, Png(Upscale(image, camera.Width, camera.Height), camera.Width, camera.Height, white), cancel);
                }
                {
                    var scene = new TraceScene { Camera = camera, Bvh = bvh, Clips = clips, Eps = 1e-6 * bvh.Size };
                    var image = ShadowRender.Render(scene, 1, lights, form, softness, rays, cancelled);
                    if (image == null)
                        return;
                    Publish(docSerial, viewportId, key, true, Png(image.Value, camera.Width, camera.Height, white), cancel);
                }
            });
        }

        static DepthCamera Scaled(DepthCamera c, int width, int height) => new DepthCamera
        {
            Location = c.Location, X = c.X, Y = c.Y, Direction = c.Direction, Parallel = c.Parallel,
            Left = c.Left, Right = c.Right, Bottom = c.Bottom, Top = c.Top, Near = c.Near, Far = c.Far,
            Width = width, Height = height,
        };

        /// <summary>Nearest-neighbour upscale of a small preview to the viewport size.</summary>
        static float[] Upscale(TraceImage image, int width, int height)
        {
            var result = new float[width * height];
            for (int y = 0; y < height; y++)
            {
                int sy = Math.Min(image.Height - 1, y * image.Height / height);
                for (int x = 0; x < width; x++)
                    result[y * width + x] = image.Value[sy * image.Width + Math.Min(image.Width - 1, x * image.Width / width)];
            }
            return result;
        }

        static byte[] Png(float[] values, int width, int height, bool shadowIsWhite)
        {
            var pixels = new byte[values.Length];
            for (int i = 0; i < pixels.Length; i++)
            {
                double v = shadowIsWhite ? values[i] : 1 - values[i];
                pixels[i] = (byte)Math.Round(255 * (v < 0 ? 0 : v > 1 ? 1 : v));
            }
            using (var stream = new MemoryStream())
            {
                ImageWriters.WritePng8(stream, width, height, pixels);
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
                    RhinoApp.WriteLine("Shadows view: could not show the image: " + e.Message);
                    return;
                }
                state.Bitmap?.Dispose();
                state.Bitmap = bitmap;
                state.BitmapKey = key;
                state.Final = final;
                if (final)
                    state.JobKey = null;
                var doc = RhinoDoc.FromRuntimeSerialNumber(docSerial);
                var view = doc?.Views.Find(viewportId);
                view?.Redraw();
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
