using System;
using System.Globalization;
using System.IO;
using System.Text;
using Rhino;
using Rhino.Commands;
using Rhino.Display;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace ImgTools
{
    /// <summary>Output paths and JSON pieces shared by the capture commands.</summary>
    internal static class CaptureIO
    {
        /// <summary>
        /// Asks where to save: a save dialog when interactive, the command line when scripted.
        /// Default name: &lt;document&gt;_&lt;view&gt;_&lt;suffix&gt;&lt;extension&gt;, next to the document.
        /// </summary>
        public static string GetOutputPath(RhinoDoc doc, RhinoView view, RunMode mode, string suffix, string extension, string title)
        {
            var baseName = string.IsNullOrEmpty(doc.Name) ? "untitled" : Path.GetFileNameWithoutExtension(doc.Name);
            var fileName = baseName + "_" + view.ActiveViewport.Name + "_" + suffix + extension;
            var folder = string.IsNullOrEmpty(doc.Path)
                ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                : Path.GetDirectoryName(doc.Path);

            string path;
            if (mode == RunMode.Interactive)
            {
                var dialog = new Rhino.UI.SaveFileDialog
                {
                    Title = title,
                    Filter = extension == ".exr" ? "OpenEXR image (*.exr)|*.exr" : "PNG image (*.png)|*.png",
                    DefaultExt = extension.Substring(1),
                    FileName = fileName,
                    InitialDirectory = folder,
                };
                if (!dialog.ShowSaveDialog())
                    return null;
                path = dialog.FileName;
            }
            else
            {
                var gs = new GetString();
                gs.SetCommandPrompt("File name");
                gs.SetDefaultString(Path.Combine(folder, fileName));
                gs.AcceptNothing(true);
                var res = gs.Get();
                if (res == GetResult.Nothing)
                    path = Path.Combine(folder, fileName);
                else if (res == GetResult.String)
                    path = gs.StringResult().Trim().Trim('"');
                else
                    return null;
                if (!Path.IsPathRooted(path))
                    path = Path.Combine(folder, path);
            }
            if (!path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                path += extension;
            return path;
        }

        /// <summary>
        /// Whether captures write a JSON file next to each image (and Capture All its
        /// _passes.json). On by default; saved with Rhino's settings.
        /// </summary>
        public static bool WriteJson
        {
            get => ImgToolsPlugin.Instance?.Settings.GetBool("WriteJson", true) ?? true;
            set => ImgToolsPlugin.Instance?.Settings.SetBool("WriteJson", value);
        }

        /// <summary>Writes json next to the image (same name, .json) when <see cref="WriteJson"/> is on.</summary>
        public static void SaveJson(string imagePath, string json)
        {
            if (!WriteJson)
                return;
            var path = Path.ChangeExtension(imagePath, ".json");
            File.WriteAllText(path, json);
            RhinoApp.WriteLine("Saved {0}", path);
        }

        public static string Num(double v) => double.IsNaN(v) || double.IsInfinity(v) ? "null" : v.ToString("R", CultureInfo.InvariantCulture);

        public static string Vec(Point3d p) => "[" + Num(p.X) + ", " + Num(p.Y) + ", " + Num(p.Z) + "]";

        public static string Vec(Vector3d v) => "[" + Num(v.X) + ", " + Num(v.Y) + ", " + Num(v.Z) + "]";

        public static string Str(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (var ch in s)
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                        else sb.Append(ch);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }
    }
}
