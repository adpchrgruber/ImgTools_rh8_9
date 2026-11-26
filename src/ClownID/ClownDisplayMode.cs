using System.Drawing;
using Rhino.Display;

namespace ImgTools
{
    /// <summary>Creates and maintains the "Clown ID" display mode.</summary>
    internal static class ClownDisplayMode
    {
        public const string Name = "Clown ID";

        public static DisplayModeDescription Find() => DisplayModeDescription.FindByName(Name);

        /// <summary>Finds or creates the mode; see <see cref="PassDisplayMode.Ensure"/>.</summary>
        public static DisplayModeDescription Ensure() => PassDisplayMode.Ensure(Name, ClownColors.Background);
    }
}
