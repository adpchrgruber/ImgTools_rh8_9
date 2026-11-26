using Eto.Drawing;
using Eto.Forms;
using Rhino;
using Rhino.Commands;

namespace ImgTools
{
    /// <summary>
    /// Opens the ImgTools options window: how the Shadows, Ambient Occlusion and Normals passes
    /// look, for the live views and the captures alike. Right-click a View button.
    /// </summary>
    public class ImgToolsOptionsCommand : Rhino.Commands.Command
    {
        public override string EnglishName => "ImgToolsOptions";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            OptionsDialog.ShowFor(doc);
            return Result.Success;
        }
    }

    /// <summary>
    /// A small floating window. Every change is saved at once and redraws the views, so the live
    /// Shadows and AO views update while you adjust; ShadowCapture and AOCapture use the same values.
    /// </summary>
    internal sealed class OptionsDialog : Form
    {
        static OptionsDialog _open;

        RhinoDoc _doc;
        readonly NumericStepper _radius;
        readonly Label _radiusUnits;

        public static void ShowFor(RhinoDoc doc)
        {
            if (_open != null)
            {
                _open._doc = doc;
                _open.Refresh();
                _open.BringToFront();
                return;
            }
            _open = new OptionsDialog(doc);
            _open.Closed += (s, e) => _open = null;
            _open.Show();
        }

        OptionsDialog(RhinoDoc doc)
        {
            _doc = doc;
            Title = "ImgTools Options";
            Resizable = false;
            Minimizable = false;
            Maximizable = false;
            Topmost = true;
            Padding = new Padding(12);

            // --- Shadows -----------------------------------------------------------
            var source = Drop(new[] { "Sun", "Lights", "Sun and lights" }, ShadowSettings.Source, i => ShadowSettings.Source = i);
            var kind = Drop(new[] { "Cast shadows only", "Cast and form shadows" }, ShadowSettings.FormShadows ? 1 : 0, i => ShadowSettings.FormShadows = i == 1);
            var softness = Stepper(ShadowSettings.Softness, 0, 45, 2, 0.25, v => ShadowSettings.Softness = v);
            softness.ToolTip = "The light's size in degrees. 0 = hard shadows; the real sun is about 0.53.";
            var shadowRays = Stepper(ShadowSettings.Rays, 1, 1024, 0, 4, v => ShadowSettings.Rays = (int)v);
            shadowRays.ToolTip = "Shadow rays per pixel per light, for soft shadows. More = smoother, slower.";
            var shadowColour = Drop(new[] { "White on black", "Black on white" }, ShadowSettings.ShadowIsWhite ? 0 : 1, i => ShadowSettings.ShadowIsWhite = i == 0);
            var shadowAa = Stepper(ShadowSettings.AntiAlias, 1, 4, 0, 1, v => ShadowSettings.AntiAlias = (int)v);
            shadowAa.ToolTip = "Samples per pixel axis in ShadowCapture (the live view uses 1).";

            // --- Ambient occlusion ---------------------------------------------------
            _radius = Stepper(AoSettings.Radius(doc), 0.1, 1e9, 1, 10, v => AoSettings.SetRadius(_doc, v));
            _radius.ToolTip = "How far away geometry still darkens a point, in model units. Saved with the document.";
            _radiusUnits = new Label { VerticalAlignment = VerticalAlignment.Center };
            var suggest = new Button { Text = "Suggest" };
            suggest.Click += (s, e) => { _radius.Value = AoSettings.SuggestRadius(_doc); };
            var aoRays = Stepper(AoSettings.Rays, 1, 4096, 0, 8, v => AoSettings.Rays = (int)v);
            aoRays.ToolTip = "Rays per pixel. With denoising 16 is usually enough; raise it if the pass looks blotchy.";
            var aoDenoise = new CheckBox { Text = "Smooth the grain (edge-aware)", Checked = AoSettings.Denoise };
            aoDenoise.CheckedChanged += (s, e) => { AoSettings.Denoise = aoDenoise.Checked == true; RhinoDoc.ActiveDoc?.Views.Redraw(); };
            aoDenoise.ToolTip = "Averages the noise across each surface, keeping edges and creases sharp. 16 rays + this look like 64 rays without, about 5x faster.";
            var aoColour = Drop(new[] { "Occluded black (white open)", "Occluded white (black open)" }, AoSettings.OccludedIsBlack ? 0 : 1, i => AoSettings.OccludedIsBlack = i == 0);
            var aoAa = Stepper(AoSettings.AntiAlias, 1, 4, 0, 1, v => AoSettings.AntiAlias = (int)v);
            aoAa.ToolTip = "Samples per pixel axis in AOCapture (the live view uses 1).";

            // --- Normals ----------------------------------------------------------------
            var space = Drop(new[] { "Camera (x right, y up, z to viewer)", "World (model X, Y, Z)" }, NormalSettings.CameraSpace ? 0 : 1, i => NormalSettings.CameraSpace = i == 0);
            var green = Drop(new[] { "Up (OpenGL, Blender, ControlNet)", "Down (DirectX, Unreal)" }, NormalSettings.GreenUp ? 0 : 1, i => NormalSettings.GreenUp = i == 0);
            green.ToolTip = "Camera space only.";
            var normalBackground = Drop(new[] { "Flat blue (128, 128, 255)", "Black" }, NormalSettings.FlatBackground ? 0 : 1, i => NormalSettings.FlatBackground = i == 0);
            var normalFormat = Drop(new[] { "PNG (8-bit)", "PNG (16-bit)", "EXR (float, with alpha)" }, NormalSettings.Format, i => NormalSettings.Format = i);
            var normalAa = Stepper(NormalSettings.AntiAlias, 1, 4, 0, 1, v => NormalSettings.AntiAlias = (int)v);
            normalAa.ToolTip = "Samples per pixel axis in NormalCapture (the live view uses 1).";

            var close = new Button { Text = "Close" };
            close.Click += (s, e) => Close();

            var shadows = new TableLayout
            {
                Spacing = new Size(8, 6),
                Rows =
                {
                    Row("Light", source),
                    Row("Shadows", kind),
                    Row("Softness (°)", softness),
                    Row("Soft rays", shadowRays),
                    Row("Colours", shadowColour),
                    Row("Anti-alias (capture)", shadowAa),
                },
            };
            var ao = new TableLayout
            {
                Spacing = new Size(8, 6),
                Rows =
                {
                    Row("Radius", new StackLayout { Orientation = Orientation.Horizontal, Spacing = 6, Items = { _radius, _radiusUnits, suggest } }),
                    Row("Rays", aoRays),
                    Row("Denoise", aoDenoise),
                    Row("Colours", aoColour),
                    Row("Anti-alias (capture)", aoAa),
                },
            };
            var normalsLayout = new TableLayout
            {
                Spacing = new Size(8, 6),
                Rows =
                {
                    Row("Space", space),
                    Row("Green", green),
                    Row("Background", normalBackground),
                    Row("Format (capture)", normalFormat),
                    Row("Anti-alias (capture)", normalAa),
                },
            };
            Content = new StackLayout
            {
                Spacing = 10,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    new GroupBox { Text = "Shadows  (ShadowView, ShadowCapture)", Content = shadows, Padding = new Padding(8) },
                    new GroupBox { Text = "Ambient occlusion  (AOView, AOCapture)", Content = ao, Padding = new Padding(8) },
                    new GroupBox { Text = "Normals  (NormalView, NormalCapture)", Content = normalsLayout, Padding = new Padding(8) },
                    new StackLayout { Orientation = Orientation.Horizontal, Items = { null, close } },
                },
            };
            Refresh();
        }

        /// <summary>Shows the active document's radius and units.</summary>
        void Refresh()
        {
            if (_doc == null)
                return;
            _radiusUnits.Text = _doc.GetUnitSystemName(true, true, false, true);
            var r = AoSettings.Radius(_doc);
            if (_radius.Value != r)
                _radius.Value = r;
        }

        static TableRow Row(string label, Control control) =>
            new TableRow(new TableCell(new Label { Text = label, VerticalAlignment = VerticalAlignment.Center }), new TableCell(control, true));

        static DropDown Drop(string[] items, int selected, System.Action<int> set)
        {
            var d = new DropDown();
            foreach (var item in items)
                d.Items.Add(item);
            d.SelectedIndex = selected;
            d.SelectedIndexChanged += (s, e) =>
            {
                if (d.SelectedIndex < 0)
                    return;
                set(d.SelectedIndex);
                RhinoDoc.ActiveDoc?.Views.Redraw();
            };
            return d;
        }

        static NumericStepper Stepper(double value, double min, double max, int decimals, double increment, System.Action<double> set)
        {
            var n = new NumericStepper { MinValue = min, MaxValue = max, DecimalPlaces = decimals, Increment = increment, Value = value, Width = 90 };
            n.ValueChanged += (s, e) =>
            {
                set(n.Value);
                RhinoDoc.ActiveDoc?.Views.Redraw();
            };
            return n;
        }
    }
}
