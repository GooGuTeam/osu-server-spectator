using System.Threading.Tasks;

namespace osu.Server.Spectator.Hubs
{
    public interface ISpectatorServer
    {
        Task BeginPlaySession(int beatmapId);
        Task SendFrameData(FrameDataBundle data);
        Task EndPlaySession(int beatmapId);
        
        Task StartWatchingUser(string userId);
        Task EndWatchingUser(string userId);
    }
}