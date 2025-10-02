using System;

// ReSharper disable InconsistentNaming (matches database table)

namespace osu.Server.Spectator.Database.Models
{
    [Serializable]
    public class beatmap_sync
    {
        public int beatmapset_id { get; set; }
        public required DateTimeOffset updated_at { get; set; }

    }
}