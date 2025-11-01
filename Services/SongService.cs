using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RadioStation.Models;
using YoutubeExplode;
using YoutubeExplode.Videos;
using YoutubeExplode.Search;

namespace RadioStation.Services
{
    public class SongService : ISongService
    {
        private readonly YoutubeClient _youtubeClient;
        private readonly ILogger<SongService> _logger;
        private readonly IConfiguration _configuration;

        public SongService(ILogger<SongService> logger, IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
            _youtubeClient = new YoutubeClient();
        }

        public async Task<List<SongSearchResult>> SearchSongsAsync(string query)
        {
            try
            {
                _logger.LogInformation("Searching for songs with query: {Query}", query);

                var searchResults = new List<VideoSearchResult>();
                await foreach (var video in _youtubeClient.Search.GetVideosAsync(query))
                {
                    searchResults.Add(video);
                    if (searchResults.Count >= 20)
                        break;
                }

                var songResults = searchResults.Select(video => new SongSearchResult
                {
                    VideoId = video.Id.Value,
                    Title = video.Title,
                    ChannelTitle = video.Author.ChannelTitle,
                    ThumbnailUrl = video.Thumbnails?.OrderByDescending(t => t.Resolution.Area).FirstOrDefault()?.Url ?? string.Empty,
                    Duration = video.Duration ?? TimeSpan.Zero
                }).ToList();

                _logger.LogInformation("Found {Count} songs for query: {Query}", songResults.Count, query);
                return songResults;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error searching for songs with query: {Query}", query);
                throw;
            }
        }

        public async Task<SongSearchResult?> GetSongDetailsAsync(string videoId)
        {
            try
            {
                _logger.LogInformation("Getting details for video ID: {VideoId}", videoId);

                var video = await _youtubeClient.Videos.GetAsync(videoId);

                if (video == null)
                {
                    _logger.LogWarning("Video not found with ID: {VideoId}", videoId);
                    return null;
                }

                var songResult = new SongSearchResult
                {
                    VideoId = video.Id.Value,
                    Title = video.Title,
                    ChannelTitle = video.Author.ChannelTitle,
                    ThumbnailUrl = video.Thumbnails?.OrderByDescending(t => t.Resolution.Area).FirstOrDefault()?.Url ?? string.Empty,
                    Duration = video.Duration ?? TimeSpan.Zero
                };

                _logger.LogInformation("Retrieved details for video: {Title}", video.Title);
                return songResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting details for video ID: {VideoId}", videoId);
                throw;
            }
        }
    }
}