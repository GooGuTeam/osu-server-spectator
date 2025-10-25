using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;

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