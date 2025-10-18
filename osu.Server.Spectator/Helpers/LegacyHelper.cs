// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;

namespace osu.Server.Spectator.Helpers
{
    public static class LegacyHelper
    {
        public static Ruleset GetRulesetFromLegacyID(int id)
        {
            switch (id)
            {
                default:
                    throw new ArgumentException("Invalid ruleset ID provided.");

                case 0:
                    return new OsuRuleset();

                case 1:
                    return new TaikoRuleset();

                case 2:
                    return new CatchRuleset();

                case 3:
                    return new ManiaRuleset();
            }
        }
    }
}
