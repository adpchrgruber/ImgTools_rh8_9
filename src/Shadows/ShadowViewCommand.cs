using System;
using System.Collections.Generic;
using System.Drawing;
using Rhino;
using Rhino.Commands;
using Rhino.Display;

namespace ImgTools
{
    /// <summary>Creates and maintains the "Shadows" display mode (the live shadow pass).</summary>
    internal static class ShadowDisplayMode
    {
        public const string Name = "Shadows";

        public static DisplayModeDescription Ensure() => PassDisplayMode.Ensure(Name, Color.Black);
    }

    /// <summary>
    /// Toggles the active viewport between the live Shadows view and the mode it had before.
    /// The view uses ShadowCapture's options (source, softness, cast/form, colours).
    /// </summary>
    public class ShadowViewCommand : Command
    {
        readonly Dictionary<Guid, Guid> _previousModes = new Dictionary<Guid, Guid>();

        public override string EnglishName => "ShadowView";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var view = doc.Views.ActiveView;
            var shadows = ShadowDisplayMode.Ensure();
            if (view == null || shadows == null)
                return Result.Failure;
            var viewport = view.ActiveViewport;
            if (PassDisplayMode.Toggle(viewport, shadows, _previousModes))
                RhinoApp.WriteLine("Shadows on in {0} ({1}). Run ShadowView again to switch back; ShadowCapture to change the options or save.",
                    viewport.Name, ShadowSettings.Describe());
            else
                RhinoApp.WriteLine("Shadows off in {0}.", viewport.Name);
            view.Redraw();
            return Result.Success;
        }
    }
}
