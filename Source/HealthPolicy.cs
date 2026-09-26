using System;
namespace CreatureMorph
{
    internal static class HealthPolicy
    {
        internal static float RestoredHealth(float original, float humanMax, float current, float creatureMax)
        {
            if (current <= 0) return 0;
            float fraction = Math.Max(0, Math.Min(1, current / Math.Max(1, creatureMax)));
            return Math.Min(original, humanMax * fraction);
        }
    }
}
