using System;
using System.Collections.Generic;
using Rhino;
using Rhino.Geometry;

namespace ImgTools
{
    /// <summary>A light that casts shadows: either a direction (sun, directional) or a position.</summary>
    internal sealed class ShadowLight
    {
        public string Name;
        public bool Directional;
        public Vector3d ToLight;      // directional: unit vector towards the light
        public Point3d Position;      // positional
        public Vector3d SpotAxis;     // spot lights: unit direction the light points, else zero
        public double SpotAngle;      // spot lights: half angle of the cone, radians

        /// <summary>Unit vector from the point towards the light and the distance to it (∞ for directional).</summary>
        public void Towards(Point3d p, out Vector3d l, out double distance)
        {
            if (Directional)
            {
                l = ToLight;
                distance = double.PositiveInfinity;
                return;
            }
            l = Position - p;
            distance = l.Length;
            if (distance > 0)
                l /= distance;
        }

        /// <summary>False outside a spot light's cone.</summary>
        public bool Reaches(Vector3d toLight)
        {
            if (SpotAxis.IsZero)
                return true;
            return Vector3d.VectorAngle(-toLight, SpotAxis) <= SpotAngle;
        }
    }

    internal static class ShadowLights
    {
        public static readonly string[] Sources = { "Sun", "Lights", "SunAndLights" };
        public const int Sun = 0, Lights = 1, Both = 2;

        public static List<ShadowLight> Collect(RhinoDoc doc, int source, out string problem)
        {
            problem = null;
            var lights = new List<ShadowLight>();
            if (source == Sun || source == Both)
            {
                var sun = SunLight(doc);
                if (sun != null)
                    lights.Add(sun);
                else if (source == Sun)
                    problem = "The sun is off or below the horizon. Turn it on in the Sun panel, or use Source=Lights.";
            }
            if (source == Lights || source == Both)
            {
                for (int i = 0; i < doc.Lights.Count; i++)
                {
                    var obj = doc.Lights[i];
                    if (obj == null || obj.IsDeleted)
                        continue;
                    var light = FromLight(obj.LightGeometry, obj.Attributes.Name);
                    if (light != null)
                        lights.Add(light);
                }
                if (lights.Count == 0)
                    problem = source == Lights
                        ? "No enabled lights in the document. Add a light, or use Source=Sun."
                        : "Neither the sun nor any light is on.";
            }
            return lights;
        }

        static ShadowLight SunLight(RhinoDoc doc)
        {
            var sun = doc.Lights.Sun;
            if (sun == null || !sun.Enabled)
                return null;
            var v = sun.Vector;
            if (!v.Unitize())
                return null;
            // Make the vector point towards the sun: up when the sun is above the horizon.
            double altitude = sun.Altitude;
            var toSun = Math.Sign(v.Z) == Math.Sign(altitude) || altitude == 0 ? v : -v;
            if (altitude <= 0 && toSun.Z <= 0)
                return null;
            return new ShadowLight { Name = "Sun", Directional = true, ToLight = toSun };
        }

        static ShadowLight FromLight(Light light, string name)
        {
            if (light == null || !light.IsEnabled)
                return null;
            name = string.IsNullOrEmpty(name) ? light.Name : name;
            if (light.IsDirectionalLight)
            {
                var d = -light.Direction;
                if (!d.Unitize())
                    return null;
                return new ShadowLight { Name = name, Directional = true, ToLight = d };
            }
            var result = new ShadowLight { Name = name, Position = light.Location };
            if (light.IsRectangularLight)
                result.Position = light.Location + 0.5 * light.Length + 0.5 * light.Width;
            else if (light.IsLinearLight)
                result.Position = light.Location + 0.5 * light.Length;
            else if (light.IsSpotLight)
            {
                var axis = light.Direction;
                if (axis.Unitize())
                {
                    result.SpotAxis = axis;
                    result.SpotAngle = light.SpotAngleRadians;
                }
            }
            return result;
        }
    }
}
