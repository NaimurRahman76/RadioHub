using System.Threading.Tasks;
using RadioStation.Models;

namespace RadioStation.Services
{
    public interface IStreamingService
    {
        void EnqueueSong(Song song);
        Task<bool> IsStreamingAsync();
        Task StopAsync();
    }
}