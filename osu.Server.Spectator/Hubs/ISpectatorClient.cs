using System.Threading.Tasks;

namespace osu.Server.Spectator.Hubs
{
    public interface ISpectatorClient
    {
        Task UserBeganPlaying(string userId, int beatmapId);

        Task UserFinishedPlaying(string userId, int beatmapId);

        Task UserSentFrames(string userId, FrameDataBundle data);
    }
}