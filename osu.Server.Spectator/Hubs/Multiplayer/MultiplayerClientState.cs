// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using Newtonsoft.Json;
using System.Collections.Generic;

namespace osu.Server.Spectator.Hubs.Multiplayer
{
    [Serializable]
    public class MultiplayerClientState : ClientState
    {
        public long CurrentRoomID;

        public Dictionary<string, string> RulesetHashes;

        public void MakeUserLeaveRoom()
        {
            CurrentRoomID = -1;
        }

        public bool IsUserInRoom(long? roomId = null)
        {
            if (roomId.HasValue)
                return CurrentRoomID == roomId.Value;

            return CurrentRoomID != -1;
        }

        [JsonConstructor]
        public MultiplayerClientState(in string connectionId, in int userId, in long currentRoomID = -1, in Dictionary<string, string>? rulesetHashes = null)
            : base(connectionId, userId)
        {
            CurrentRoomID = currentRoomID;
            RulesetHashes = rulesetHashes ?? new Dictionary<string, string>();
        }
    }
}