using Microsoft.Extensions.Logging;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Catch;
using osu.Game.Rulesets.Mania;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Taiko;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace osu.Server.Spectator.Services
{
    public class RulesetManager
    {
        private readonly ILogger<RulesetManager> _logger;

        private const string ruleset_library_prefix = "osu.Game.Rulesets";

        private readonly Dictionary<string, Ruleset> _rulesets = new Dictionary<string, Ruleset>();
        private readonly Dictionary<int, Ruleset> _rulesetsById = new Dictionary<int, Ruleset>();

        public RulesetManager(ILogger<RulesetManager> logger)
        {
            _logger = logger;
            loadOfficialRulesets();
            loadFromDisk();
        }

        private void addRuleset(Ruleset ruleset)
        {
            if (!_rulesets.TryAdd(ruleset.ShortName, ruleset))
            {
                _logger.LogWarning("Ruleset with short name {shortName} already exists, skipping.", ruleset.ShortName);
                return;
            }

            if (ruleset is not ILegacyRuleset legacyRuleset)
            {
                return;
            }

            if (!_rulesetsById.TryAdd(legacyRuleset.LegacyID, ruleset))
            {
                _logger.LogWarning("Ruleset with ID {id} already exists, skipping.", legacyRuleset.LegacyID);
            }
        }

        private void loadOfficialRulesets()
        {
            foreach (Ruleset ruleset in (List<Ruleset>)
                     [new OsuRuleset(), new TaikoRuleset(), new CatchRuleset(), new ManiaRuleset()])
            {
                addRuleset(ruleset);
            }

            _rulesets["catch"] = _rulesets["fruits"];
        }

        private void loadFromDisk()
        {
            if (!Directory.Exists(AppSettings.RulesetsPath))
            {
                return;
            }

            string[] rulesets = Directory.GetFiles(AppSettings.RulesetsPath, $"{ruleset_library_prefix}.*.dll");

            foreach (string ruleset in rulesets.Where(f => !f.Contains(@"Tests")))
            {
                try
                {
                    Assembly assembly = Assembly.LoadFrom(ruleset);
                    Type? rulesetType = assembly.GetTypes()
                                                .FirstOrDefault(t => t.IsSubclassOf(typeof(Ruleset)) && !t.IsAbstract);

                    if (rulesetType == null)
                    {
                        continue;
                    }

                    Ruleset instance = (Ruleset)Activator.CreateInstance(rulesetType)!;
                    _logger.LogInformation("Loading ruleset {ruleset}", ruleset);
                    addRuleset(instance);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Failed to load ruleset from {ruleset}: {ex}", ruleset, ex);
                }
            }
        }

        public Ruleset GetRuleset(int rulesetId)
        {
            return _rulesetsById.TryGetValue(rulesetId, out Ruleset? ruleset)
                ? ruleset
                : throw new ArgumentException("Invalid ruleset ID provided.");
        }

        public Ruleset GetRuleset(string shortName)
        {
            return _rulesets.TryGetValue(shortName, out Ruleset? ruleset)
                ? ruleset
                : throw new ArgumentException("Invalid ruleset name provided.");
        }
    }
}