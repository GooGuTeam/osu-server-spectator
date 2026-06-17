// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
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
using MatchType = osu.Game.Online.Rooms.MatchType;

namespace osu.Server.Spectator.Hubs.Multiplayer
{
    /// <summary>
    /// Subscribes to external multiplayer room events and applies them to the local room state.
    /// </summary>
    sealed public class MultiplayerRoomRedisSubscriber : IHostedService, IDisposable
    {
        private const string room_channel_prefix = "osu-channel:room:";
        private const string callback_channel_prefix = "osu-channel:callback:";

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
            if (!tryParseRoomId(channel, out long roomId))
                return;

            if (message.IsNullOrEmpty)
                return;

            string? id = null;
            object details = new
            {
            };

            try
            {
                var envelope = JsonConvert.DeserializeObject<MultiplayerRoomEventEnvelope>(message!);
                if (envelope == null || string.IsNullOrWhiteSpace(envelope.Type))
                    return;

                id = envelope.Id;

                switch (envelope.Type)
                {
                    case "TransferHost":
                        if (envelope.TargetUserId != null)
                            await applyTransferHost(roomId, envelope.TargetUserId.Value);
                        break;

                    case "SetLockState":
                        if (envelope.RoomState != null && envelope.ByUserId != null)
                            await applySetLockState(roomId, envelope.ByUserId.Value, envelope.RoomState.Locked);
                        break;

                    case "SetUserSlot":
                        if (envelope.ByUserId != null && envelope.UserState?.SlotId != null)
                            await applySetUserSlot(roomId, envelope.ByUserId.Value, envelope.UserState.SlotId.Value);
                        break;

                    case "ChangeRoomSettings":
                        if (envelope.RoomSettings != null)
                            await applyChangeRoomSettings(roomId, envelope.RoomSettings);
                        break;

                    case "ChangeTeam":
                        if (envelope.TargetUserId != null && envelope.UserState?.TeamId != null)
                            await applyChangeUserTeam(roomId, envelope.TargetUserId.Value, envelope.UserState.TeamId.Value);
                        break;

                    case "KickPlayer":
                        if (envelope.TargetUserId != null)
                            await applyKickUser(roomId, envelope.TargetUserId.Value);
                        break;

                    case "BanUser":
                        if (envelope.TargetUserId != null)
                            await applyBanUser(roomId, envelope.TargetUserId.Value);
                        break;

                    case "AddReferee":
                        if (envelope.TargetUserId != null)
                            await applyAddReferee(roomId, envelope.TargetUserId.Value);
                        break;

                    case "RemoveReferee":
                        if (envelope.TargetUserId != null)
                            await applyRemoveReferee(roomId, envelope.TargetUserId.Value);
                        break;

                    case "ListReferees":
                        details = new
                        {
                            referee_ids = await getRefereeIds(roomId),
                        };
                        break;

                    case "StartMatch":
                        await applyStartMatch(roomId, envelope);
                        break;

                    case "StartReminderTimer":
                        await applyStartReminderTimer(roomId, envelope);
                        break;

                    case "StopAllCountdowns":
                        await applyStopAllCountdowns(roomId);
                        break;

                    case "AbortMatch":
                        await applyAbortMatch(roomId);
                        break;

                    case "CloseRoom":
                        if (envelope.ByUserId != null)
                            await applyDisbandRoom(roomId, envelope.ByUserId.Value);
                        break;

                    case "InviteUser":
                        if (envelope.TargetUserId != null && envelope.ByUserId != null)
                            await applyInviteUser(roomId, envelope.TargetUserId.Value, envelope.ByUserId.Value);
                        break;

                    default:
                        return;
                }

                string callbackMessage = JsonConvert.SerializeObject(new
                {
                    type = "TaskResult",
                    success = true,
                    details,
                });

                await redis.GetSubscriber().PublishAsync(
                    new RedisChannel($"{callback_channel_prefix}{id}", RedisChannel.PatternMode.Literal),
                    callbackMessage);
            }
            // Try our best effort to give the feedback to backend
            catch (Exception ex) when (ex is InvalidStateException or NotHostException)
            {
                // Would this really happen?
                if (id == null)
                {
                    logger.LogWarning(ex, "A task (from {Channel}) without an ID returned with exception.", channel);
                    return;
                }

                string exceptionMessage = ex is NotHostException ? "You don't have the required privilege to perform this action." : ex.Message;

                string callbackMessage = JsonConvert.SerializeObject(new
                {
                    type = "TaskResult",
                    success = false,
                    message = exceptionMessage,
                });

                await redis.GetSubscriber().PublishAsync(
                    new RedisChannel($"{callback_channel_prefix}{id}", RedisChannel.PatternMode.Literal),
                    callbackMessage);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to process multiplayer room event from {Channel}", channel);
            }
        }

        private async Task<ServerMultiplayerRoom> ensureStandardRoom(long roomId)
        {
            using ItemUsage<ServerMultiplayerRoom>? roomUsage = await roomController.TryGetRoom(roomId);
            return ensureStandardRoomUsage(roomUsage).Item ?? throw new InvalidStateException("Cannot find the room specified.");
        }

        private static ItemUsage<ServerMultiplayerRoom> ensureStandardRoomUsage(ItemUsage<ServerMultiplayerRoom>? roomUsage)
        {
            if (roomUsage?.Item == null)
                throw new InvalidStateException("Cannot find the room specified.");

            if (roomUsage.Item.MatchController is not StandardMatchController)
                throw new InvalidStateException("This function is only supported for multiplayer rooms.");

            return roomUsage;
        }

        private async Task<int[]> getRefereeIds(long roomId)
        {
            await ensureStandardRoom(roomId);

            return referees.GetAllEntities()
                           .Select(kv => kv.Value)
                           .Where(rs => rs.IsAssociatedWithRoom(roomId))
                           .Select(rs => rs.UserId)
                           .ToArray();
        }

        private async Task applyTransferHost(long roomId, int userId)
        {
            var room = await ensureStandardRoom(roomId);

            if (room.Host?.UserID == userId)
                throw new InvalidStateException("The specified user is already the host.");

            await room.SetHost(userId);
        }

        private async Task applySetLockState(long roomId, int byUserId, bool locked)
        {
            var room = await ensureStandardRoom(roomId);

            var user = room.Users.FirstOrDefault(u => u.UserID == byUserId);
            if (user == null)
                throw new InvalidStateException("Cannot find the specified user.");

            await room.HandleUserRequest(user, new SetLockStateRequest
            {
                Locked = locked,
            });
        }

        private async Task applySetUserSlot(long roomId, int byUserId, byte slotId)
        {
            var room = await ensureStandardRoom(roomId);

            var user = room.Users.FirstOrDefault(u => u.UserID == byUserId);
            if (user == null)
                throw new InvalidStateException("Cannot find the specified user.");

            await room.HandleUserRequest(user, new ChangeSlotRequest
            {
                SlotID = slotId,
            });
        }

        private async Task applyChangeRoomSettings(long roomId, MultiplayerRoomSettingsEnvelope settings)
        {
            var room = await ensureStandardRoom(roomId);

            var oldSettings = room.Settings;

            byte? maxParticipants = oldSettings.MaxParticipants;
            if (settings.MaxParticipants.HasValue)
                maxParticipants = settings.MaxParticipants.Value == 0 ? null : settings.MaxParticipants.Value;

            var newSettings = new MultiplayerRoomSettings
            {
                Name = settings.Name ?? oldSettings.Name,
                PlaylistItemId = oldSettings.PlaylistItemId,
                Password = settings.Password ?? oldSettings.Password,
                MatchType = settings.MatchType != null ? (MatchType)settings.MatchType.Value : oldSettings.MatchType,
                QueueMode = oldSettings.QueueMode,
                AutoStartDuration = oldSettings.AutoStartDuration,
                AutoSkip = oldSettings.AutoSkip,
                MaxParticipants = maxParticipants,
            };

            await room.ChangeRoomSettings(newSettings);
        }

        private async Task applyChangeUserTeam(long roomId, int userId, int teamId)
        {
            var room = await ensureStandardRoom(roomId);

            if (room.MatchController is not TeamVersusMatchController teamVersus)
                throw new InvalidStateException("Team changing is only supported in Team VS mode.");

            var user = room.Users.FirstOrDefault(u => u.UserID == userId);
            if (user == null)
                throw new InvalidStateException("User is not in the room.");

            await teamVersus.ChangeUserTeam(user, teamId);
        }

        private async Task applyKickUser(long roomId, int userId)
        {
            using ItemUsage<ServerMultiplayerRoom> roomUsage = ensureStandardRoomUsage(await roomController.TryGetRoom(roomId));

            var user = roomUsage.Item?.Users.FirstOrDefault(u => u.UserID == userId);
            if (user == null)
                throw new InvalidStateException("User is not in the room.");

            // Not handling the null usage items below
            // since the user's role is bound to be either a player or a referee for now.
            switch (user.Role)
            {
                // Determine if user is a player or referee and get the appropriate state
                case MultiplayerRoomUserRole.Player:
                {
                    using ItemUsage<MultiplayerClientState> playerUsage = await players.GetForUse(userId);

                    if (playerUsage.Item != null)
                        await roomController.KickUserFromRoom(playerUsage.Item, roomUsage, userId);
                    break;
                }

                case MultiplayerRoomUserRole.Referee:
                {
                    using ItemUsage<RefereeClientState> refereeUsage = await referees.GetForUse(userId);

                    if (refereeUsage.Item != null)
                        await roomController.KickUserFromRoom(refereeUsage.Item, roomUsage, userId);
                    break;
                }
            }
        }

        private async Task applyBanUser(long roomId, int bannedUserId)
        {
            using ItemUsage<ServerMultiplayerRoom> roomUsage = ensureStandardRoomUsage(await roomController.TryGetRoom(roomId));
            await roomController.BanUserFromRoom(bannedUserId, roomUsage, bannedUserId);
        }

        private async Task applyAddReferee(long roomId, int userId)
        {
            await ensureStandardRoom(roomId);

            // Get or create referee state and associate with room
            using ItemUsage<RefereeClientState> refereeUsage = await referees.GetForUse(userId);

            refereeUsage.Item ??= new RefereeClientState(string.Empty, userId);
            refereeUsage.Item.AssociateWithRoom(roomId);
        }

        private async Task applyRemoveReferee(long roomId, int userId)
        {
            using ItemUsage<ServerMultiplayerRoom> roomUsage = ensureStandardRoomUsage(await roomController.TryGetRoom(roomId));

            // Disassociate referee from room
            using ItemUsage<RefereeClientState> refereeUsage = await referees.GetForUse(userId);
            refereeUsage.Item?.DisassociateFromRoom(roomId);
        }

        private async Task applyStartMatch(long roomId, MultiplayerRoomEventEnvelope envelope)
        {
            var room = await ensureStandardRoom(roomId);

            // Stop any active reminder timer (mutual exclusion: match countdown takes priority)
            var reminderCountdown = room.FindCountdownOfType<ReminderCountdown>();
            if (reminderCountdown != null)
                await room.StopCountdown(reminderCountdown.ID);

            // If countdownSeconds provided, request spectator to start countdown; otherwise start immediately
            if (envelope.Countdown != null)
            {
                int seconds = envelope.Countdown.Seconds;
                await room.StartMatchCountdown(TimeSpan.FromSeconds(seconds));
            }
            else
            {
                await ServerMultiplayerRoom.StartMatch(room);
            }
        }

        private async Task applyStartReminderTimer(long roomId, MultiplayerRoomEventEnvelope envelope)
        {
            var room = await ensureStandardRoom(roomId);

            // Create and start a ReminderCountdown (reminder-only, does not start match)
            int seconds = envelope.Countdown?.Seconds ?? 30;
            var reminderCountdown = new ReminderCountdown
            {
                TimeRemaining = TimeSpan.FromSeconds(seconds),
            };

            await room.StartCountdown(reminderCountdown, onComplete: null);
        }

        private async Task applyStopAllCountdowns(long roomId)
        {
            var room = await ensureStandardRoom(roomId);
            bool result = false;

            // Stop MatchStartCountdown if exists
            var matchCountdown = room.FindCountdownOfType<MatchStartCountdown>();

            if (matchCountdown != null)
            {
                await room.StopCountdown(matchCountdown.ID);
                result = true;
            }

            // Stop ReminderCountdown if exists
            var reminderCountdown = room.FindCountdownOfType<ReminderCountdown>();

            if (reminderCountdown != null)
            {
                await room.StopCountdown(reminderCountdown.ID);
                result = true;
            }

            if (!result)
            {
                if (room.FindCountdownOfType<MultiplayerCountdown>() != null)
                    throw new InvalidStateException("Active countdowns exist, but you are unable to abort them.");

                throw new InvalidStateException("There are no active countdowns.");
            }
        }

        private async Task applyAbortMatch(long roomId)
        {
            var room = await ensureStandardRoom(roomId);

            try
            {
                // If the match is running, abort it
                await room.AbortMatch();
            }
            catch (InvalidStateException)
            {
                // Then try to stop ongoing match timers
                var countdown = room.FindCountdownOfType<MatchStartCountdown>();

                if (countdown == null)
                    throw new InvalidStateException("There is not a running match or an ongoing match countdown.");

                await room.StopCountdown(countdown.ID);
            }
        }

        private async Task applyDisbandRoom(long roomId, int userId)
        {
            var room = await ensureStandardRoom(roomId);
            await room.Disband(userId);
        }

        private async Task applyInviteUser(long roomId, int invitedUserId, int invitedBy)
        {
            var room = await ensureStandardRoom(roomId);
            await room.InvitePlayer(invitedUserId, invitedBy);
        }

        private static bool tryParseRoomId(RedisChannel channel, out long roomId)
        {
            roomId = 0;

            string channelName = channel.ToString();
            if (!channelName.StartsWith(room_channel_prefix, StringComparison.Ordinal))
                return false;

            return long.TryParse(channelName[room_channel_prefix.Length..], out roomId);
        }

        private sealed class MultiplayerRoomEventEnvelope
        {
            [JsonProperty("id")]
            public string? Id { get; set; }

            [JsonProperty("type")]
            public string? Type { get; set; }

            [JsonProperty("target")]
            public int? TargetUserId { get; set; }

            [JsonProperty("by")]
            public int? ByUserId { get; set; }

            [JsonProperty("countdown")]
            public MultiplayerCountdownEnvelope? Countdown { get; set; }

            [JsonProperty("room_state")]
            public MultiplayerRoomStateEnvelope? RoomState { get; set; }

            [JsonProperty("room_settings")]
            public MultiplayerRoomSettingsEnvelope? RoomSettings { get; set; }

            [JsonProperty("user_state")]
            public MultiplayerUserStateEnvelope? UserState { get; set; }
        }

        private sealed class MultiplayerCountdownEnvelope
        {
            [JsonProperty("seconds")]
            public int Seconds { get; set; }
        }

        private sealed class MultiplayerRoomStateEnvelope
        {
            [JsonProperty("locked")]
            public bool Locked { get; set; }
        }

        private sealed class MultiplayerUserStateEnvelope
        {
            [JsonProperty("team_id")]
            public int? TeamId { get; set; }

            [JsonProperty("slot_id")]
            public byte? SlotId { get; set; }
        }

        private sealed class MultiplayerRoomSettingsEnvelope
        {
            [JsonProperty("name")]
            public string? Name { get; set; }

            [JsonProperty("password")]
            public string? Password { get; set; }

            [JsonProperty("type")]
            public int? MatchType { get; set; }

            [JsonProperty("max_participants")]
            public byte? MaxParticipants { get; set; }
        }
    }
}
