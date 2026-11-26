using System.Drawing;
using Rhino.Display;

namespace ImgTools
{
    /// <summary>The display modes of the passes: copies of Shaded with everything but surfaces off.</summary>
    internal static class PassDisplayMode
    {
        /// <summary>
        /// Finds or creates the mode (as a copy of Shaded, so Rhino keeps building render
        /// meshes for it) and strips everything that is not a surface.
        /// </summary>
        public static DisplayModeDescription Ensure(string name, Color fill)
        {
            var mode = DisplayModeDescription.FindByName(name);
            if (mode == null)
            {
                var id = DisplayModeDescription.CopyDisplayMode(DisplayModeDescription.ShadedId, name);
                mode = DisplayModeDescription.GetDisplayMode(id);
                if (mode == null)
                    return null;
            }

            var a = mode.DisplayAttributes;
            // The conduits paint the background themselves; this keeps the mode consistent where Rhino honours it.
            a.FillMode = DisplayPipelineAttributes.FrameBufferFillMode.SolidColor;
            a.SetFill(fill);
            a.ViewSpecificAttributes.DrawGrid = false;
            a.ViewSpecificAttributes.DrawGridAxes = false;
            a.ViewSpecificAttributes.DrawWorldAxes = false;
            a.ShowCurves = false;
            a.ShowPoints = false;
            a.ShowPointClouds = false;
            a.ShowIsoCurves = false;
            a.ShowSurfaceEdges = false;
            a.ShowTangentEdges = false;
            a.ShowTangentSeams = false;
            a.ShowClipIntersectionEdges = false;
            a.MeshSpecificAttributes.ShowMeshWires = false;
            a.ShowAnnotations = false;
            a.ShowText = false;
            a.ShowLights = false;
            a.ShowClippingPlanes = false;
            a.ShowGrips = false;
            a.CastShadows = false;
            // Newer than the RhinoCommon we compile against; set them when the running Rhino has them.
            foreach (var p in new[] { "PostProcessGamma", "ShowSubDEdges", "ShowSubDBoundary", "ShowSubDCreases", "ShowSubDNonmanifoldEdges", "ShowClippingEdges" })
            {
                var property = typeof(DisplayPipelineAttributes).GetProperty(p);
                if (property != null && property.CanWrite && property.PropertyType == typeof(bool))
                    property.SetValue(a, false);
            }

            DisplayModeDescription.UpdateDisplayMode(mode);
            return DisplayModeDescription.FindByName(name);
        }

        /// <summary>
        /// Switches the active viewport into the mode, or back to the mode it had before.
        /// Returns true when the mode is now on.
        /// </summary>
        public static bool Toggle(RhinoViewport viewport, DisplayModeDescription mode, System.Collections.Generic.Dictionary<System.Guid, System.Guid> previous)
        {
            if (viewport.DisplayMode?.Id == mode.Id)
            {
                var before = previous.TryGetValue(viewport.Id, out var id) ? DisplayModeDescription.GetDisplayMode(id) : null;
                viewport.DisplayMode = before ?? DisplayModeDescription.GetDisplayMode(DisplayModeDescription.ShadedId);
                return false;
            }
            if (viewport.DisplayMode != null)
                previous[viewport.Id] = viewport.DisplayMode.Id;
            viewport.DisplayMode = mode;
            return true;
        }
    }
}
