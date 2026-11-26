using Rhino;
using Rhino.PlugIns;

namespace ImgTools
{
    ///<summary>
    /// ImgTools: image passes for compositing renders made in Rhino 8.
    ///   Clown ID   – every object in its own flat ID colour (ClownID, ClownIDCapture)
    ///   Z Depth    – the depth pass, live and as exact maps (ZDepth, ZDepthCapture, ZDepthRange)
    ///   Shadows    – the raw cast shadows of the sun or lights as a mask, hard or soft (ShadowCapture)
    ///   AO         – ambient occlusion (AOCapture)
    ///</summary>
    public class ImgToolsPlugin : PlugIn
    {
        public ImgToolsPlugin()
        {
            Instance = this;
        }

        ///<summary>Gets the only instance of the ImgTools plug-in.</summary>
        public static ImgToolsPlugin Instance { get; private set; }

        internal ClownIdConduit ClownConduit { get; private set; }

        internal ZDepthConduit DepthConduit { get; private set; }

        // Load with Rhino so the display modes are never selected without their conduits behind them.
        public override PlugInLoadTime LoadTime => PlugInLoadTime.AtStartup;

        protected override LoadReturnCode OnLoad(ref string errorMessage)
        {
            var clown = ClownDisplayMode.Ensure();
            if (clown == null)
            {
                errorMessage = "Could not create the \"" + ClownDisplayMode.Name + "\" display mode.";
                return LoadReturnCode.ErrorShowDialog;
            }
            var depth = ZDepthDisplayMode.Ensure();
            if (depth == null)
            {
                errorMessage = "Could not create the \"" + ZDepthDisplayMode.Name + "\" display mode.";
                return LoadReturnCode.ErrorShowDialog;
            }

            ClownConduit = new ClownIdConduit(clown.Id) { Enabled = true };

            DepthConduit = new ZDepthConduit(depth.Id) { Enabled = true };
            RhinoDoc.DeleteRhinoObject += (s, e) => DepthConduit.Forget(e.TheObject.RuntimeSerialNumber);
            RhinoDoc.CloseDocument += (s, e) => DepthConduit.ForgetAll();
            RhinoDoc.ModifyObjectAttributes += (s, e) => DepthConduit.Forget(e.RhinoObject.RuntimeSerialNumber); // per-object mesh settings
            RhinoDoc.InstanceDefinitionTableEvent += (s, e) => DepthConduit.ForgetAll();     // block edits change instances
            RhinoDoc.DocumentPropertiesChanged += (s, e) => DepthConduit.ForgetAll();        // render mesh quality
            return LoadReturnCode.Success;
        }

        protected override void OnShutdown()
        {
            if (ClownConduit != null)
                ClownConduit.Enabled = false;
            if (DepthConduit != null)
                DepthConduit.Enabled = false;
        }
    }
}
