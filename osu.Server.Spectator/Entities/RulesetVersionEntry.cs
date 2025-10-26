using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace osu.Server.Spectator.Entities
{
    // https://github.com/GooGuTeam/custom-rulesets/blob/main/CustomRulesetMetadataGenerator/SubCommands/GenerateVersionCommand.cs#L24-L29
    public class RulesetVersionEntry
    {
        [JsonPropertyName("latest-version")]
        public string LatestVersion { get; set; } = "";

        [JsonPropertyName("versions")]
        public Dictionary<string, string> Versions { get; set; } = new Dictionary<string, string>();
    }
}