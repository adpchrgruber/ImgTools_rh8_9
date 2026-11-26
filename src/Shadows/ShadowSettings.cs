namespace ImgTools
{
    /// <summary>
    /// Shadow options shared by ShadowCapture and the live Shadows view, so the view shows what
    /// the capture will save. Saved with Rhino's settings. Changing one redraws the views.
    /// </summary>
    internal static class ShadowSettings
    {
        static Rhino.PersistentSettings S => ImgToolsPlugin.Instance?.Settings;

        /// <summary>Index into <see cref="ShadowLights.Sources"/>.</summary>
        public static int Source
        {
            get => S?.GetInteger("Shadow.Source", ShadowLights.Sun) ?? ShadowLights.Sun;
            set => S?.SetInteger("Shadow.Source", value);
        }

        public static bool FormShadows
        {
            get => S?.GetBool("Shadow.FormShadows", false) ?? false;
            set => S?.SetBool("Shadow.FormShadows", value);
        }

        public static bool ShadowIsWhite
        {
            get => S?.GetBool("Shadow.ShadowIsWhite", true) ?? true;
            set => S?.SetBool("Shadow.ShadowIsWhite", value);
        }

        /// <summary>Light size in degrees; 0 = hard shadows.</summary>
        public static double Softness
        {
            get => S?.GetDouble("Shadow.Softness", 0) ?? 0;
            set => S?.SetDouble("Shadow.Softness", value);
        }

        public static int Rays
        {
            get => S?.GetInteger("Shadow.Rays", 32) ?? 32;
            set => S?.SetInteger("Shadow.Rays", value);
        }

        public static int AntiAlias
        {
            get => S?.GetInteger("Shadow.AntiAlias", 2) ?? 2;
            set => S?.SetInteger("Shadow.AntiAlias", value);
        }

        /// <summary>A short text for prompts and the view: "Sun, hard" or "Sun, soft 0.53°".</summary>
        public static string Describe() =>
            ShadowLights.Sources[Source] + ", " + (Softness > 0
                ? "soft " + Softness.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "°"
                : "hard") + (FormShadows ? ", cast and form" : "");
    }
}
