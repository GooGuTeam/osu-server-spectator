// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace osu.Server.Spectator.Services
{
    public class RulesetInitializer(RulesetManager rulesetManager, ILogger<RulesetInitializer> logger)
        : IHostedService
    {
        private readonly RulesetManager _rulesetManager = rulesetManager;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            logger.LogInformation("Initialized all rulesets");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
