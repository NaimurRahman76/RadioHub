using System.Collections.Generic;
using System.Threading.Tasks;
using RadioStation.Models;

namespace RadioStation.Services
{
    public interface IQueueService
    {
        Task AddSongAsync(Song song);
        Task<Song?> GetNextSongAsync();
        Task<List<Song>> GetQueueAsync();
        Task<Song?> GetCurrentlyPlayingAsync();
        Task MarkSongAsPlayingAsync(string videoId);
        Task MarkSongAsFinishedAsync(string videoId);
        Task<bool> RemoveSongAsync(string videoId);
        Task<bool> IsSongInQueueAsync(string videoId);
        Task ClearQueueAsync();
        Task<QueueViewModel> GetQueueViewModelAsync();
    }
}