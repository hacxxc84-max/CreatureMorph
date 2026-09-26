using System;
namespace CreatureMorph
{
    internal static class AnimationPolicy
    {
        internal static int Select(string[] names, bool ground, bool idle, bool attack, string direction)
        {
            int best = -1, bestScore = int.MinValue;
            for (int i = 0; i < names.Length; i++)
            {
                string n = names[i].ToLowerInvariant();
                if (n.Contains("death") || n.Contains("dead") || n.Contains("cinematic") || n.Contains("hatch") || n.Contains("birth")
                    || n.Contains("flinch") || n.Contains("seamoth") || n.Contains("exo") || n.Contains("holding") || n.Contains("release")
                    || n.Contains("to_float") || n.Contains("to_stand") || n.Contains("mushroom") || n.Contains("coil")
                    || n.Contains("hold") || n.Contains("putaway") || n.Contains("draw") || n.Contains("headonly")
                    || n.Contains("reaction") || n.Contains("attacked") || n.Contains("attach") || n.Contains("stickto") || n.Contains("suck")) continue;
                bool walking = n.Contains("walk") || n.Contains("crawl") || n.Contains("run") || n.Contains("locomotion") || n.Contains("move");
                bool swimming = n.Contains("swim") || n.Contains("fly") || n.Contains("slow") || n.Contains("fast");
                bool attacking = n.Contains("attack") || n.Contains("bite") || n.Contains("strike") || n.Contains("shove") || n.Contains("swat");
                if (attack ? !attacking : idle ? !n.Contains("idle") : ground ? !walking : !swimming) continue;
                if (!attack && attacking) continue;
                int score = 100;
                bool standing = n.Contains("standing") || n.Contains("ground");
                bool floating = n.Contains("floating") || n.Contains("swim");
                score += ground ? (standing ? 80 : 0) - (floating ? 100 : 0) : (floating ? 80 : 0) - (standing ? 100 : 0);
                if (n.Contains("neutral")) score += 15;
                if (n.Contains("slow")) score += 15;
                if (n.Contains("fast") || n.Contains("aggro")) score -= 10;
                if (attack) { if (n.Contains("bite") || n.Contains("melee")) score += 120; if (n.Contains("blast")) score -= 70; }
                if (!idle && !attack)
                {
                    string actual = n.Contains("left") || n.EndsWith("_l") ? "left" : n.Contains("right") || n.EndsWith("_r") ? "right" : n.Contains("back") ? "back" : n.Contains("down") || n.Contains("up") || n.EndsWith("_u") || n.EndsWith("_d") || n.EndsWith("swimu") || n.EndsWith("swimd") ? "vertical" : "forward";
                    score += actual == direction ? 200 : -200;
                    if (n.Contains("forw") || n.EndsWith("swimf")) score += direction == "forward" ? 20 : 0;
                }
                if (score > bestScore) { bestScore = score; best = i; }
            }
            return best;
        }
    }
}
