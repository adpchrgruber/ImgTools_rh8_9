using System.Drawing;
using Rhino.Display;

namespace ImgTools
{
    /// <summary>Creates and maintains the "Z Depth" display mode.</summary>
    internal static class ZDepthDisplayMode
    {
        public const string Name = "Z Depth";

        public static DisplayModeDescription Find() => DisplayModeDescription.FindByName(Name);

        /// <summary>Finds or creates the mode; see <see cref="PassDisplayMode.Ensure"/>.</summary>
        public static DisplayModeDescription Ensure() => PassDisplayMode.Ensure(Name, Color.Black);
    }
}
