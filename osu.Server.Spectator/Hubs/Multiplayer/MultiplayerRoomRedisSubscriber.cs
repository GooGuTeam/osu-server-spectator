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
using osu.Game.Online.Rooms;
using osu.Server.Spectator.Database;
using osu.Server.Spectator.Database.Models;
using osu.Server.Spectator.Services;
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

        private readonly IDatabaseFactory databaseFactory;
        private readonly IConnectionMultiplexer redis;
        private readonly IMultiplayerRoomController roomController;
        private readonly RulesetManager rulesetMgr;
        private readonly EntityStore<MultiplayerClientState> players;
        private readonly EntityStore<RefereeClientState> referees;
        private readonly ILogger<MultiplayerRoomRedisSubscriber> logger;

        private ISubscriber? subscriber;

        public MultiplayerRoomRedisSubscriber(
            IDatabaseFactory databaseFactory,
            IConnectionMultiplexer redis,
            IMultiplayerRoomController roomController,
            RulesetManager rulesetMgr,
            EntityStore<MultiplayerClientState> players,
            EntityStore<RefereeClientState> referees,
            ILoggerFactory loggerFactory)
        {
            this.databaseFactory = databaseFactory;
            this.redis = redis;
            this.roomController = roomController;
            this.rulesetMgr = rulesetMgr;
            this.players = players;
            this.referees = referees;
            logger = loggerFactory.CreateLogger<MultiplayerRoomRedisSubscriber>();
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            subscriber = redis.GetSubscriber();
            subscriber.Subscribe(new RedisChannel($"{room_channel_prefix}*", RedisChannel.PatternMode.Pattern), onMessageReceived);
            logger.LogInformation("Started listening to backend messages.");

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            subscriber?.UnsubscribeAll();
            subscriber = null;
            logger.LogInformation("Stopped listening to backend messages.");
            return Task.CompletedTask;
        }

        public void Dispose() => subscriber?.UnsubscribeAll();

        private void onMessageReceived(RedisChannel channel, RedisValue message)
            => _ = Task.Run(() => processMessage(channel, message));

        private async Task processMessage(RedisChannel channel, RedisValue message)
        {
            logger.LogInformation("Received message {Message}", message);

            if (!tryParseRoomId(channel, out long roomId))
                return;

            if (message.IsNullOrEmpty)
                return;

            var envelope = JsonConvert.DeserializeObject<MultiplayerRoomEventEnvelope>(message!);
            logger.LogInformation("Message: {Message}", JsonConvert.SerializeObject(envelope));
            if (envelope == null || string.IsNullOrWhiteSpace(envelope.Type))
                return;

            var callbackMessage = new MultiplayerCallbackMessage
            {
                ID = envelope.ID,
                Type = "TaskResult",
                Details = new
                {
                },
            };

            try
            {
                var room = await ensureStandardRoom(roomId);

                ensurePrivileged(room, envelope.ByUserId, envelope.Type != "AddReferee" && envelope.Type != "RemoveReferee");

                switch (envelope.Type)
                {
                    case "TransferHost":
                        if (envelope.TargetUserID != null)
                            await applyTransferHost(room, envelope.TargetUserID.Value);
                        break;

                    case "SetLockState":
                        if (envelope.RoomState != null)
                            await applySetLockState(room, envelope.ByUserId, envelope.RoomState.Locked);
                        break;

                    case "SetUserSlot":
                        if (envelope.UserState?.SlotID != null)
                            await applySetUserSlot(room, envelope.ByUserId, envelope.UserState.SlotID.Value);
                        break;

                    case "ChangeRoomSettings":
                        if (envelope.RoomSettings != null)
                            await applyChangeRoomSettings(room, envelope.RoomSettings);
                        break;

                    case "ChangeTeam":
                        if (envelope.TargetUserID != null && envelope.UserState?.TeamID != null)
                            await applyChangeUserTeam(room, envelope.TargetUserID.Value, envelope.UserState.TeamID.Value);
                        break;

                    case "KickPlayer":
                        if (envelope.TargetUserID != null)
                            await applyKickUser(roomId, envelope.TargetUserID.Value);
                        break;

                    case "BanUser":
                        if (envelope.TargetUserID != null)
                            await applyBanUser(roomId, envelope.TargetUserID.Value);
                        break;

                    case "AddReferee":
                        if (envelope.TargetUserID != null)
                            await applyAddReferee(roomId, envelope.TargetUserID.Value);
                        break;

                    case "RemoveReferee":
                        if (envelope.TargetUserID != null)
                            await applyRemoveReferee(roomId, envelope.TargetUserID.Value);
                        break;

                    case "ListReferees":
                        callbackMessage.Details = new
                        {
                            referee_ids = referees.GetAllEntities()
                                                  .Select(kv => kv.Value)
                                                  .Where(rs => rs.IsAssociatedWithRoom(roomId))
                                                  .Select(rs => rs.UserId)
                                                  .ToArray(),
                        };
                        break;

                    case "StartMatch":
                        await applyStartMatch(room, envelope);
                        break;

                    case "StartReminderTimer":
                        await applyStartReminderTimer(room, envelope);
                        break;

                    case "StopAllCountdowns":
                        await applyStopAllCountdowns(room);
                        break;

                    case "AbortMatch":
                        await applyAbortMatch(room);
                        break;

                    case "CloseRoom":
                        await room.Disband(envelope.ByUserId);
                        break;

                    case "InviteUser":
                        if (envelope.TargetUserID != null)
                            await room.InvitePlayer(envelope.TargetUserID.Value, envelope.ByUserId);
                        break;

                    case "ChangeBeatmap":
                        if (envelope.MapSettings != null)
                            await applyChangeBeatmap(room, envelope.ByUserId, envelope.MapSettings);
                        break;

                    default:
                        throw new InvalidStateException("Unsupported message.");
                }

                callbackMessage.Success = true;
            }
            // Try our best effort to give the feedback to backend
            catch (Exception ex) when (ex is InvalidStateException or NotHostException or RefereeHubException)
            {
                logger.LogWarning("Exception caught: [{Type}]{Details}", ex, ex.Message);
                callbackMessage.Success = false;
                callbackMessage.Message = ex switch
                {
                    NotHostException => "You don't have the required privilege to perform this action.",

                    // "Error {code}: {message}"
                    RefereeHubException => ex.Message.Split(':', 2)[1].Trim(),
                    _ => ex.Message,
                };
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to process multiplayer room event from {Channel}", channel);
            }

            await redis.GetSubscriber().PublishAsync(
                new RedisChannel($"{callback_channel_prefix}{callbackMessage.ID}", RedisChannel.PatternMode.Literal),
                JsonConvert.SerializeObject(callbackMessage));
            logger.LogInformation("Callback sent: {Message}", JsonConvert.SerializeObject(callbackMessage));
        }

        /// <summary>
        /// Finds a standard multiplay room (i.e. a room with <see cref="StandardMatchController"/>) by ID.
        /// </summary>
        /// <returns>The <see cref="ServerMultiplayerRoom"/> room instance.</returns>
        /// <exception cref="InvalidStateException">Thrown when the room cannot be found, or it's not a standard multiplayer room.</exception>
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

        /// <summary>
        /// Finds a user in a multiplayer room by ID.
        /// </summary>
        /// <returns>The <see cref="MultiplayerRoomUser"/> user instance.</returns>
        /// <exception cref="InvalidStateException">Thrown when the specified user is not in the room.</exception>
        private static MultiplayerRoomUser ensureUser(ServerMultiplayerRoom room, int userId)
        {
            var user = room.Users.FirstOrDefault(u => u.UserID == userId);
            return user ?? throw new InvalidStateException("Cannot find the specified user.");
        }

        private MultiplayerRoomUser ensurePrivileged(ServerMultiplayerRoom room, int userId, bool allowReferee = true)
        {
            var user = ensureUser(room, userId);

            logger.LogInformation("Checking privilege for user '{User}' (#{ID}) in room '{Room}'", user.User?.Username, userId, room.Settings.Name);

            logger.LogInformation("Room host: '{User}' (#{ID})", room.Host?.User?.Username, room.Host?.UserID);

            logger.LogInformation("Current user role: {Role}", user.Role);

            if (room.Host?.UserID == userId || (allowReferee && user.Role == MultiplayerRoomUserRole.Referee))
                return user;

            throw new NotHostException();
        }

        private async Task applyTransferHost(ServerMultiplayerRoom room, int userId)
        {
            if (room.Host?.UserID == userId)
                throw new InvalidStateException("The specified user is already the host.");

            await room.SetHost(userId);
        }

        private async Task applySetLockState(ServerMultiplayerRoom room, int byUserId, bool locked)
        {
            var user = room.Users.FirstOrDefault(u => u.UserID == byUserId);
            if (user == null)
                throw new InvalidStateException("Cannot find the specified user.");

            await room.HandleUserRequest(user, new SetLockStateRequest
            {
                Locked = locked,
            });
        }

        private async Task applySetUserSlot(ServerMultiplayerRoom room, int byUserId, byte slotId)
        {
            var user = room.Users.FirstOrDefault(u => u.UserID == byUserId);
            if (user == null)
                throw new InvalidStateException("Cannot find the specified user.");

            await room.HandleUserRequest(user, new ChangeSlotRequest
            {
                SlotID = slotId,
            });
        }

        private async Task applyChangeRoomSettings(ServerMultiplayerRoom room, MultiplayerRoomSettingsEnvelope settings)
        {
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

        private async Task applyChangeUserTeam(ServerMultiplayerRoom room, int userId, int teamId)
        {
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
            // Get or create referee state and associate with room.
            // createOnMissing is required because the user may not have been tracked in the referee store yet
            // (e.g. BanchoBot or a user that has never joined as referee).
            using ItemUsage<RefereeClientState> refereeUsage = await referees.GetForUse(userId, createOnMissing: true);

            refereeUsage.Item ??= new RefereeClientState(string.Empty, userId);
            refereeUsage.Item.AssociateWithRoom(roomId);
        }

        private async Task applyRemoveReferee(long roomId, int userId)
        {
            // Disassociate referee from room.
            // Use TryGetForUse because the referee might not exist in the store
            // (never added, or already removed).
            using ItemUsage<RefereeClientState>? refereeUsage = await referees.TryGetForUse(userId);

            if (refereeUsage?.Item == null)
                throw new InvalidStateException("The specified user is not a referee.");

            refereeUsage.Item.DisassociateFromRoom(roomId);
        }

        private async Task applyStartMatch(ServerMultiplayerRoom room, MultiplayerRoomEventEnvelope envelope)
        {
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

        private async Task applyStartReminderTimer(ServerMultiplayerRoom room, MultiplayerRoomEventEnvelope envelope)
        {
            // Create and start a ReminderCountdown (reminder-only, does not start match)
            int seconds = envelope.Countdown?.Seconds ?? 30;
            var reminderCountdown = new ReminderCountdown
            {
                TimeRemaining = TimeSpan.FromSeconds(seconds),
            };

            await room.StartCountdown(reminderCountdown, onComplete: null);
        }

        private async Task applyStopAllCountdowns(ServerMultiplayerRoom room)
        {
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

        private async Task applyAbortMatch(ServerMultiplayerRoom room)
        {
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

        private async Task applyChangeBeatmap(ServerMultiplayerRoom room, int byUserId, MultiplayerMapSettingsEnvelope settings)
        {
            if (settings.BeatmapID == null)
                throw new InvalidStateException("Beatmap ID is required.");

            database_beatmap? beatmap;

            using (var db = databaseFactory.GetInstance())
                beatmap = await db.GetBeatmapAsync(settings.BeatmapID.Value);

            if (beatmap == null)
                throw new InvalidStateException("Cannot find the beatmap specified.");

            int rulesetId = settings.RulesetID ?? beatmap.playmode;

            var item = new MultiplayerPlaylistItem
            {
                OwnerID = byUserId,
                BeatmapID = beatmap.beatmap_id,
                BeatmapChecksum = beatmap.checksum ?? string.Empty,
                RulesetID = rulesetId,
                RequiredMods = [],
                AllowedMods = [],
                StarRating = beatmap.difficulty_rating,
                Freestyle = false,
            };

            RefereeHub.EnsurePlaylistItemValid(item, beatmap, rulesetMgr);

            var currentItem = room.CurrentPlaylistItem;

            if (!currentItem.Expired)
            {
                // Edit the current item in-place to avoid disrupting the playlist structure.
                item.ID = currentItem.ID;
                await room.EditPlaylistItem(byUserId, item);
            }
            else
            {
                // No valid current item exists; add a fresh one.
                await room.AddPlaylistItem(byUserId, item);
            }
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
            [JsonProperty("id", Required = Required.Always)]
            public required string ID { get; set; }

            [JsonProperty("type", Required = Required.Always)]
            public required string Type { get; set; }

            [JsonProperty("target")]
            public int? TargetUserID { get; set; }

            [JsonProperty("by", Required = Required.Always)]
            public int ByUserId { get; set; }

            [JsonProperty("countdown")]
            public MultiplayerCountdownEnvelope? Countdown { get; set; }

            [JsonProperty("room_state")]
            public MultiplayerRoomStateEnvelope? RoomState { get; set; }

            [JsonProperty("room_settings")]
            public MultiplayerRoomSettingsEnvelope? RoomSettings { get; set; }

            [JsonProperty("user_state")]
            public MultiplayerUserStateEnvelope? UserState { get; set; }

            [JsonProperty("map_settings")]
            public MultiplayerMapSettingsEnvelope? MapSettings { get; set; }
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
            public int? TeamID { get; set; }

            [JsonProperty("slot_id")]
            public byte? SlotID { get; set; }
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

        private sealed class MultiplayerMapSettingsEnvelope
        {
            [JsonProperty("beatmap_id")]
            public int? BeatmapID { get; set; }

            [JsonProperty("ruleset_id")]
            public int? RulesetID { get; set; }

            [JsonProperty("mods")]
            public string[]? ModAcronyms { get; set; }
        }

        private sealed class MultiplayerCallbackMessage
        {
            [JsonProperty("id", Required = Required.Always)]
            public required string ID { get; set; }

            [JsonProperty("type", Required = Required.Always)]
            public required string Type { get; set; }

            [JsonProperty("success")]
            public bool Success { get; set; }

            [JsonProperty("message")]
            public string? Message { get; set; }

            [JsonProperty("details")]
            public object Details { get; set; } = new
            {
            };
        }
    }
}
