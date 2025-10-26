using osu.Game.Rulesets;

namespace osu.Server.Spectator.Extensions
{
    public static class RulesetExtensions
    {
        public static bool IsOfficial(this Ruleset ruleset)
        {
            switch (ruleset.ShortName)
            {
                case "osu":
                case "taiko":
                case "fruits":
                case "catch":
                case "mania":
                    return true;

                default:
                    return false;
            }
        }

        public static bool IsOfficial(this RulesetInfo ruleset)
        {
            switch (ruleset.ShortName)
            {
                case "osu":
                case "taiko":
                case "fruits":
                case "catch":
                case "mania":
                    return true;

                default:
                    return false;
            }
        }
    }
}