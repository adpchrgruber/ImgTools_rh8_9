using System;
using System.Collections.Generic;
using System.Drawing;
using Rhino;
using Rhino.Commands;
using Rhino.Display;

namespace ImgTools
{
    /// <summary>The display modes of the live traced views.</summary>
    internal static class TraceDisplayModes
    {
        public const string ShadowsName = "Shadows";
        public const string AoName = "Ambient Occlusion";

        public static DisplayModeDescription Shadows() => PassDisplayMode.Ensure(ShadowsName, Color.Black);

        public static DisplayModeDescription Ao() => PassDisplayMode.Ensure(AoName, Color.White);
    }

    /// <summary>
    /// Toggles the active viewport between the live Shadows view and the mode it had before.
    /// Appearance: ImgToolsOptions (right-click the button), shared with ShadowCapture.
    /// </summary>
    public class ShadowViewCommand : Command
    {
        readonly Dictionary<Guid, Guid> _previousModes = new Dictionary<Guid, Guid>();

        public override string EnglishName => "ShadowView";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode) =>
            TraceViews.Toggle(doc, TraceDisplayModes.Shadows(), _previousModes, "Shadows", "ShadowView", ShadowSettings.Describe());
    }

    /// <summary>
    /// Toggles the active viewport between the live Ambient Occlusion view and the mode it had
    /// before. Appearance: ImgToolsOptions (right-click the button), shared with AOCapture.
    /// </summary>
    public class AOViewCommand : Command
    {
        readonly Dictionary<Guid, Guid> _previousModes = new Dictionary<Guid, Guid>();

        public override string EnglishName => "AOView";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode) =>
            TraceViews.Toggle(doc, TraceDisplayModes.Ao(), _previousModes, "Ambient occlusion", "AOView",
                "radius " + AoSettings.Radius(doc).ToString("G4", System.Globalization.CultureInfo.InvariantCulture) + " " + doc.GetUnitSystemName(true, true, false, true) + ", " + AoSettings.Rays + " rays");
    }

    internal static class TraceViews
    {
        public static Result Toggle(RhinoDoc doc, DisplayModeDescription displayMode, Dictionary<Guid, Guid> previous, string what, string command, string settings)
        {
            var view = doc.Views.ActiveView;
            if (view == null || displayMode == null)
                return Result.Failure;
            var viewport = view.ActiveViewport;
            if (PassDisplayMode.Toggle(viewport, displayMode, previous))
                RhinoApp.WriteLine(string.Format("{0} on in {1} ({2}). Run {3} again to switch back; ImgToolsOptions to change how it looks.",
                    what, viewport.Name, settings, command));
            else
                RhinoApp.WriteLine("{0} off in {1}.", what, viewport.Name);
            view.Redraw();
            return Result.Success;
        }
    }
}
