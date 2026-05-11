// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Text.Json.Serialization;
using JetBrains.Annotations;

namespace osu.Server.Spectator.Hubs.Referee.Models.Events
{
    [PublicAPI]
    public class CountdownTickEvent
    {
        [JsonPropertyName("room_id")]
        public long RoomId { get; set; }

        [JsonPropertyName("countdown_id")]
        public int CountdownId { get; set; }

        [JsonPropertyName("seconds")]
        public double Seconds { get; set; }

        public CountdownTickEvent()
        {
        }
    }
}
