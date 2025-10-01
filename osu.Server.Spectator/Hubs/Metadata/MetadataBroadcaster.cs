// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using osu.Game.Online.Metadata;
using osu.Server.QueueProcessor;
using osu.Server.Spectator.Database;
using ServerBeatmapUpdates = osu.Server.QueueProcessor.BeatmapUpdates;
using ClientBeatmapUpdates = osu.Game.Online.Metadata.BeatmapUpdates;
using System.Threading.Tasks;
using System.Threading;
using osu.Server.Spectator.Database.Models;
using System.Collections.Generic;

namespace osu.Server.Spectator.Hubs.Metadata
{
    /// <summary>
    /// A service which broadcasts any new metadata changes to <see cref="MetadataHub"/>.
    /// </summary>
    public class MetadataBroadcaster : IDisposable
    {
        private readonly IDatabaseFactory databaseFactory;
        private readonly IHubContext<MetadataHub> metadataHubContext;

        private readonly ILogger logger;

        private readonly CancellationTokenSource cts;

        private readonly int pollMilliseconds = 10000;

        private DateTimeOffset after;

        public MetadataBroadcaster(
            ILoggerFactory loggerFactory,
            IDatabaseFactory databaseFactory,
            IHubContext<MetadataHub> metadataHubContext)
        {
            this.databaseFactory = databaseFactory;
            this.metadataHubContext = metadataHubContext;
            after = DateTimeOffset.UtcNow;
            cts = new CancellationTokenSource();

            logger = loggerFactory.CreateLogger(nameof(MetadataBroadcaster));
        }

        private async Task poll()
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    using var db = databaseFactory.GetInstance();

                    var beatmapSets = await db.GetChangedBeatmapSetsAsync(after);
                }
                catch (Exception value)
                {
                    logger.LogWarning(value, "Poll failed");
                    await Task.Delay(1000, cts.Token);
                }
                await Task.Delay(pollMilliseconds, cts.Token);
            }
        }

        // ReSharper disable once AsyncVoidMethod
        private async void handleUpdates(IEnumerable<beatmap_sync> updates)
        {
            logger.LogInformation("Polled beatmap changes up to {datetime}", after);

            if (updates.Any())
            {
                List<int> beatmapIds = new List<int>();
                foreach (var update in updates)
                {
                    beatmapIds.Add(update.beatmapset_id);
                    if (update.updated_at > after)
                        after = update.updated_at;
                }

                logger.LogInformation("Broadcasting new beatmaps to client: {beatmapIds}", string.Join(',', beatmapIds.Select(i => i.ToString())));
                await metadataHubContext.Clients.All.SendAsync(nameof(IMetadataClient.BeatmapSetsUpdated), new ClientBeatmapUpdates(beatmapIds.ToArray(), TimeHelper.ToMappedInt(after)));
            }
        }

        public void Dispose()
        {
            cts.Cancel();
        }
    }

    /// <summary>
    /// A dummy disposable class for when BeatmapStatusWatcher is disabled.
    /// </summary>
    internal class DummyDisposable : IDisposable
    {
        public void Dispose()
        {
            // No-op
        }
    }
}
