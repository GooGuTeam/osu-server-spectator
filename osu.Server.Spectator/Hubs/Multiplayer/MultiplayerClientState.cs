// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Threading;
using Newtonsoft.Json;
using StatsdClient;
using System.Collections.Generic;

namespace osu.Server.Spectator.Hubs.Multiplayer
{
    [Serializable]
    public class MultiplayerClientState : ClientState
    {
        public long? CurrentRoomID;

        public Dictionary<string, string> RulesetHashes;

        private static int countUsersInRooms;

        [JsonConstructor]
        public MultiplayerClientState(in string connectionId, in int userId, in long? currentRoomID = null, in Dictionary<string, string>? rulesetHashes = null)
            : base(connectionId, userId)
        {
            CurrentRoomID = currentRoomID;
            RulesetHashes = rulesetHashes ?? new Dictionary<string, string>();
        }

        public void SetRoom(long roomId)
        {
            if (CurrentRoomID != null)
                throw new InvalidOperationException("User is already in a room.");

            CurrentRoomID = roomId;
            DogStatsd.Gauge($"{MultiplayerHub.STATSD_PREFIX}.users", Interlocked.Increment(ref countUsersInRooms));
        }

        public void ClearRoom()
        {
            if (CurrentRoomID == null)
                return;

            CurrentRoomID = null;
            DogStatsd.Gauge($"{MultiplayerHub.STATSD_PREFIX}.users", Interlocked.Decrement(ref countUsersInRooms));
        }
    }
}