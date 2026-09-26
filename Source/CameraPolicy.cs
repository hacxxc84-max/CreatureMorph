using System;
namespace CreatureMorph
{
    internal static class CameraPolicy
    {
        // Keep the camera outside the visual body when a rock or terrain collider is
        // immediately behind the creature. The old .12 factor could place the camera
        // inside small creatures after an obstruction clamp.
        internal static float Minimum(float length) { return Math.Max(.45f, length * .42f); }
        internal static float Maximum(float length) { return Math.Max(Minimum(length), Math.Min(180, Math.Max(8, length * 3))); }
        internal static float DefaultDistance(float length, float fov)
        {
            double halfAngle = Math.Max(15, Math.Min(55, fov * .5)) * Math.PI / 180;
            return Clamp((float)(length * 1.25 / Math.Tan(halfAngle)), Minimum(length), Maximum(length));
        }
        internal static float Zoom(float distance, float wheel, float length)
        { return Clamp(distance * (float)Math.Pow(.85, wheel), Minimum(length), Maximum(length)); }
        private static float Clamp(float value, float min, float max) { return Math.Max(min, Math.Min(max, value)); }
    }
}
