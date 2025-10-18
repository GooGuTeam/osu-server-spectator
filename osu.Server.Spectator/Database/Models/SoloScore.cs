// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;
using Newtonsoft.Json;
using osu.Game.Online.API;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;


namespace osu.Server.Spectator.Database.Models
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [Serializable]
    [Table("scores")]
    public class SoloScore
    {
        public ulong id { get; set; }

        public uint user_id { get; set; }

        public uint beatmap_id { get; set; }

        public ushort ruleset_id { get; set; }

        public bool has_replay { get; set; }
        public bool preserve { get; set; }
        public bool ranked { get; set; } = true;

        public ScoreRank rank { get; set; }

        public bool passed { get; set; } = true;

        public float accuracy { get; set; }

        public uint max_combo { get; set; }

        public uint total_score { get; set; }

        public uint n300 { get; set; }
        public uint n100 { get; set; }
        public uint n50 { get; set; }
        public uint nmiss { get; set; }
        public uint ngeki { get; set; }
        public uint nkatu { get; set; }
        public uint? nlarge_tick_miss { get; set; }
        public uint? nlarge_tick_hit { get; set; }
        public uint? nslider_tail_hit { get; set; }
        public uint? nsmall_tick_hit { get; set; }
        public string? mods { get; set; }
        public string? maximum_statistics { get; set; }

        public double? pp { get; set; }

        public ulong? legacy_score_id { get; set; }
        public uint? legacy_total_score { get; set; }

        public DateTimeOffset? started_at { get; set; }
        public DateTimeOffset ended_at { get; set; }

        public override string ToString() => $"score_id: {id} user_id: {user_id}";

        public ushort? build_id { get; set; }

        public SoloScoreInfo ToScoreInfo() => new SoloScoreInfo
        {
            BeatmapID = (int)beatmap_id,
            RulesetID = ruleset_id,
            BuildID = build_id,
            Passed = passed,
            TotalScore = total_score,
            Accuracy = accuracy,
            UserID = (int)user_id,
            MaxCombo = (int)max_combo,
            Rank = rank,
            StartedAt = started_at,
            EndedAt = ended_at,
            Mods = JsonConvert.DeserializeObject<APIMod[]>(mods ?? "[]") ?? Array.Empty<APIMod>(),
            Statistics = new Dictionary<HitResult, int>
                {
                    { HitResult.Great, (int)n300 },
                    { HitResult.Good, (int)n100 },
                    { HitResult.Meh, (int)n50 },
                    { HitResult.Miss, (int)nmiss },
                    { HitResult.Perfect, (int)ngeki },
                    { HitResult.Ok, (int)nkatu },
                    { HitResult.LargeTickMiss, (int?)(nlarge_tick_miss ?? 0) ?? 0 },
                    { HitResult.LargeTickHit, (int?)(nlarge_tick_hit ?? 0) ?? 0 },
                    { HitResult.SliderTailHit, (int?)(nslider_tail_hit ?? 0) ?? 0 },
                    { HitResult.SmallTickHit, (int?)(nsmall_tick_hit ?? 0) ?? 0 },
            },
            MaximumStatistics = JsonConvert.DeserializeObject<Dictionary<HitResult, int>>(maximum_statistics ?? "{}") ?? new Dictionary<HitResult, int>(),
            LegacyTotalScore = (int?)legacy_total_score,
            LegacyScoreId = legacy_score_id,
            ID = id,
            PP = pp,
            HasReplay = has_replay
        };
    }
}
