// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using osu.Server.Spectator.Entities;
using osu.Server.Spectator.Hubs.Multiplayer.Standard;
using osu.Server.Spectator.Hubs.Referee;
using osu.Game.Online.Multiplayer;
using osu.Game.Online.Multiplayer.Countdown;
using StackExchange.Redis;

namespace osu.Server.Spectator.Hubs.Multiplayer
{
    /// <summary>
    /// Subscribes to external multiplayer room events and applies them to the local room state.
    /// </summary>
    sealed public class MultiplayerRoomRedisSubscriber : IHostedService, IDisposable
    {
        private const string room_channel_prefix = "osu-channel:room:";

        private readonly IConnectionMultiplexer redis;
        private readonly IMultiplayerRoomController roomController;
        private readonly EntityStore<MultiplayerClientState> players;
        private readonly EntityStore<RefereeClientState> referees;
        private readonly ILogger<MultiplayerRoomRedisSubscriber> logger;

        private ISubscriber? subscriber;

        public MultiplayerRoomRedisSubscriber(
            IConnectionMultiplexer redis,
            IMultiplayerRoomController roomController,
            EntityStore<MultiplayerClientState> players,
            EntityStore<RefereeClientState> referees,
            ILoggerFactory loggerFactory)
        {
            this.redis = redis;
            this.roomController = roomController;
            this.players = players;
            this.referees = referees;
            logger = loggerFactory.CreateLogger<MultiplayerRoomRedisSubscriber>();
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            subscriber = redis.GetSubscriber();
            subscriber.Subscribe(new RedisChannel($"{room_channel_prefix}*", RedisChannel.PatternMode.Pattern), onMessageReceived);

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            subscriber?.UnsubscribeAll();
            subscriber = null;
            return Task.CompletedTask;
        }

        public void Dispose() => subscriber?.UnsubscribeAll();

        private void onMessageReceived(RedisChannel channel, RedisValue message)
            => _ = Task.Run(() => processMessage(channel, message));

        private async Task processMessage(RedisChannel channel, RedisValue message)
        {
            try
            {
                if (!tryParseRoomId(channel, out long roomId))
                    return;

                if (message.IsNullOrEmpty)
                    return;

                var envelope = JsonConvert.DeserializeObject<MultiplayerRoomEventEnvelope>(message!);
                if (envelope == null || string.IsNullOrWhiteSpace(envelope.Type))
                    return;

                switch (envelope.Type)
                {
                    case "HostChanged":
                        if (envelope.UserId != null)
                            await applyHostChanged(roomId, envelope.UserId.Value);

                        break;

                    case "MatchRoomStateChanged":
                        if (envelope.State != null)
                            await applyMatchRoomStateChanged(roomId, envelope.State.Locked);

                        break;

                    case "SetLockState":
                        if (envelope.Locked != null)
                            await applySetLockState(roomId, envelope.Locked.Value);

                        break;

                    case "MatchUserStateChanged":
                        if (envelope.UserId != null && envelope.MatchState != null)
                            await applyMatchUserStateChanged(roomId, envelope.UserId.Value, envelope.MatchState.TeamID);

                        break;

                    case "KickPlayer":
                        if (envelope.UserId != null)
                            await applyUserKicked(roomId, envelope.UserId.Value);

                        break;

                    case "BanUser":
                        if (envelope.BannedUserId != null)
                            await applyUserBanned(roomId, envelope.BannedUserId.Value);

                        break;

                    case "AddReferee":
                        if (envelope.TargetUserId != null)
                            await applyRefereeAdded(roomId, envelope.TargetUserId.Value);

                        break;

                    case "RemoveReferee":
                        if (envelope.TargetUserId != null)
                            await applyRefereeRemoved(roomId, envelope.TargetUserId.Value);

                        break;

                    case "StartMatch":
                        await applyStartMatch(roomId, envelope);
                        break;

                    case "StartReminderTimer":
                        await applyStartReminderTimer(roomId, envelope);
                        break;

                    case "StopMatchCountdown":
                        await applyStopMatchCountdown(roomId);
                        break;

                    case "StopAllCountdowns":
                        await applyStopAllCountdowns(roomId);
                        break;

                    case "AbortMatch":
                        await applyAbortMatch(roomId);
                        break;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to process multiplayer room event from {Channel}", channel);
            }
        }

        private async Task applyHostChanged(long roomId, int userId)
        {
            using var roomUsage = await roomController.TryGetRoom(roomId);

            if (roomUsage?.Item == null)
                return;

            if (roomUsage.Item.Host?.UserID == userId)
                return;

            if (roomUsage.Item.Users.All(u => u.UserID != userId))
                return;

            await roomUsage.Item.SetHost(userId);
        }

        private async Task applyMatchRoomStateChanged(long roomId, bool locked)
        {
            using var roomUsage = await roomController.TryGetRoom(roomId);

            if (roomUsage?.Item?.MatchController is not TeamVersusMatchController teamVersus)
                return;

            await teamVersus.SetLockState(locked);
        }

        private async Task applySetLockState(long roomId, bool locked)
        {
            using var roomUsage = await roomController.TryGetRoom(roomId);

            if (roomUsage?.Item?.MatchController is not TeamVersusMatchController teamVersus)
                return;

            await teamVersus.SetLockState(locked);
        }

        private async Task applyMatchUserStateChanged(long roomId, int userId, int teamId)
        {
            using var roomUsage = await roomController.TryGetRoom(roomId);

            if (roomUsage?.Item?.MatchController is not TeamVersusMatchController teamVersus)
                return;

            var user = roomUsage.Item.Users.FirstOrDefault(u => u.UserID == userId);
            if (user == null)
                return;

            await teamVersus.ChangeUserTeam(user, teamId);
        }

        private async Task applyUserKicked(long roomId, int userId)
        {
            using var roomUsage = await roomController.TryGetRoom(roomId);

            var user = roomUsage?.Item?.Users.FirstOrDefault(u => u.UserID == userId);
            if (roomUsage == null || user == null)
                return;

            // Determine if user is a player or referee and get the appropriate state
            if (user.Role == MultiplayerRoomUserRole.Player)
            {
                using (var playerUsage = await players.GetForUse(userId))
                {
                    if (playerUsage.Item != null)
                        await roomController.KickUserFromRoom(playerUsage.Item, roomUsage, userId);
                }
            }
            else if (user.Role == MultiplayerRoomUserRole.Referee)
            {
                using (var refereeUsage = await referees.GetForUse(userId))
                {
                    if (refereeUsage.Item != null)
                        await roomController.KickUserFromRoom(refereeUsage.Item, roomUsage, userId);
                }
            }
        }

        private async Task applyUserBanned(long roomId, int bannedUserId)
        {
            using var roomUsage = await roomController.TryGetRoom(roomId);

            if (roomUsage?.Item == null)
                return;

            await roomController.BanUserFromRoom(bannedUserId, roomUsage, bannedUserId);
        }

        private async Task applyRefereeAdded(long roomId, int userId)
        {
            using var roomUsage = await roomController.TryGetRoom(roomId);

            if (roomUsage?.Item == null)
                return;

            // Get or create referee state and associate with room
            using (var refereeUsage = await referees.GetForUse(userId))
            {
                refereeUsage.Item ??= new RefereeClientState(string.Empty, userId);
                refereeUsage.Item.AssociateWithRoom(roomId);
            }
        }

        private async Task applyRefereeRemoved(long roomId, int userId)
        {
            using var roomUsage = await roomController.TryGetRoom(roomId);

            if (roomUsage?.Item == null)
                return;

            var user = roomUsage.Item.Users.FirstOrDefault(u => u.UserID == userId);

            // If referee is in room, kick them
            if (user?.Role == MultiplayerRoomUserRole.Referee)
            {
                using (var refereeUsage = await referees.GetForUse(userId))
                {
                    Debug.Assert(refereeUsage.Item != null);
                    await roomController.KickUserFromRoom(refereeUsage.Item, roomUsage, userId);
                }
            }

            // Disassociate referee from room
            using (var refereeUsage = await referees.GetForUse(userId))
            {
                refereeUsage.Item?.DisassociateFromRoom(roomId);
            }
        }

        private async Task applyStartMatch(long roomId, MultiplayerRoomEventEnvelope envelope)
        {
            using var roomUsage = await roomController.TryGetRoom(roomId);

            if (roomUsage?.Item == null)
                return;

            // Stop any active reminder timer (mutual exclusion: match countdown takes priority)
            var reminderCountdown = roomUsage.Item.FindCountdownOfType<ReminderCountdown>();
            if (reminderCountdown != null)
                await roomUsage.Item.StopCountdown(reminderCountdown.ID);

            // If countdownSeconds provided, request spectator to start countdown; otherwise start immediately
            if (envelope.CountdownSeconds != null)
            {
                int seconds = envelope.CountdownSeconds.Value;
                await roomUsage.Item.StartMatchCountdown(TimeSpan.FromSeconds(seconds));
            }
            else
            {
                await ServerMultiplayerRoom.StartMatch(roomUsage.Item);
            }
        }

        private async Task applyStartReminderTimer(long roomId, MultiplayerRoomEventEnvelope envelope)
        {
            using var roomUsage = await roomController.TryGetRoom(roomId);

            if (roomUsage?.Item == null || envelope.CountdownSeconds == null)
                return;

            // Create and start a ReminderCountdown (reminder-only, does not start match)
            int seconds = envelope.CountdownSeconds.Value;
            var reminderCountdown = new ReminderCountdown
            {
                TimeRemaining = TimeSpan.FromSeconds(seconds)
            };

            await roomUsage.Item.StartCountdown(reminderCountdown, onComplete: null);
        }

        private async Task applyStopMatchCountdown(long roomId)
        {
            using var roomUsage = await roomController.TryGetRoom(roomId);

            var countdown = roomUsage?.Item?.FindCountdownOfType<MatchStartCountdown>();
            if (roomUsage?.Item != null && countdown != null)
                await roomUsage.Item.StopCountdown(countdown.ID);
        }

        private async Task applyStopAllCountdowns(long roomId)
        {
            using var roomUsage = await roomController.TryGetRoom(roomId);

            if (roomUsage?.Item == null)
                return;

            // Stop MatchStartCountdown if exists
            var matchCountdown = roomUsage.Item.FindCountdownOfType<MatchStartCountdown>();
            if (matchCountdown != null)
                await roomUsage.Item.StopCountdown(matchCountdown.ID);

            // Stop ReminderCountdown if exists
            var reminderCountdown = roomUsage.Item.FindCountdownOfType<ReminderCountdown>();
            if (reminderCountdown != null)
                await roomUsage.Item.StopCountdown(reminderCountdown.ID);
        }

        private async Task applyAbortMatch(long roomId)
        {
            using var roomUsage = await roomController.TryGetRoom(roomId);

            if (roomUsage?.Item == null)
                return;

            // Only abort if match is in a running state
            try
            {
                await roomUsage.Item.AbortMatch();
            }
            catch (InvalidOperationException)
            {
                // ignore invalid abort requests
            }
        }

        private static bool tryParseRoomId(RedisChannel channel, out long roomId)
        {
            roomId = 0;

            string channelName = channel.ToString();
            if (!channelName.StartsWith(room_channel_prefix, StringComparison.Ordinal))
                return false;

            return long.TryParse(channelName.Substring(room_channel_prefix.Length), out roomId);
        }

        private sealed class MultiplayerRoomEventEnvelope
        {
            [JsonProperty("type")]
            public string? Type { get; set; }

            [JsonProperty("userId")]
            public int? UserId { get; set; }

            [JsonProperty("bannedUserId")]
            public int? BannedUserId { get; set; }

            [JsonProperty("targetUserId")]
            public int? TargetUserId { get; set; }

            [JsonProperty("countdownSeconds")]
            public int? CountdownSeconds { get; set; }

            [JsonProperty("locked")]
            public bool? Locked { get; set; }

            [JsonProperty("byUserId")]
            public int? ByUserId { get; set; }

            [JsonProperty("state")]
            public MultiplayerRoomStateEnvelope? State { get; set; }

            [JsonProperty("matchState")]
            public MultiplayerUserStateEnvelope? MatchState { get; set; }
        }

        private sealed class MultiplayerRoomStateEnvelope
        {
            [JsonProperty("locked")]
            public bool Locked { get; set; }
        }

        private sealed class MultiplayerUserStateEnvelope
        {
            [JsonProperty("teamID")]
            public int TeamID { get; set; }
        }
    }
}