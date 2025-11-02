using System.Collections.Generic;
using System.Threading.Tasks;
using RadioStation.Models;

namespace RadioStation.Services
{
    public interface ISongService
    {
        Task<List<SongSearchResult>> SearchSongsAsync(string query);
        Task<SongSearchResult?> GetSongDetailsAsync(string videoId);
        Task<bool> IsSongAsync(string videoId);
    }
}