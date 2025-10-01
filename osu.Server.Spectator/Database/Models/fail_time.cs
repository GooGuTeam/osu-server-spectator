using System;

// ReSharper disable InconsistentNaming (matches database table)

namespace osu.Server.Spectator.Database.Models
{
    [Serializable]
    public class fail_time
    {
        public int beatmap_id { get; set; }
        public required byte[] exit { get; set; }
        public required byte[] fail { get; set; }
    }
}