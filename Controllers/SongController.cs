using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RadioStation.Hubs;
using RadioStation.Models;
using RadioStation.Services;
using RadioHubSignalR = RadioStation.Hubs.RadioHub;
using System;
using System.Threading.Tasks;

namespace RadioStation.Controllers
{
    public class SongController : Controller
    {
        private readonly ISongService _songService;
        private readonly IStreamingService _streamingService;
        private readonly IHubContext<RadioHubSignalR> _hubContext;
        private readonly ILogger<SongController> _logger;
        private readonly IConfiguration _configuration;

        public SongController(
            ISongService songService,
            IStreamingService streamingService,
            IHubContext<RadioHubSignalR> hubContext,
            ILogger<SongController> logger,
            IConfiguration configuration)
        {
            _songService = songService;
            _streamingService = streamingService;
            _hubContext = hubContext;
            _logger = logger;
            _configuration = configuration;
        }

        // GET: /Song
        public IActionResult Index()
        {
            return View(); // frontend will connect via SignalR to get updates
        }

        // GET: /Song/Search
        public IActionResult Search()
        {
            return View();
        }

        // POST: /Song/Search
        [HttpPost]
        public async Task<IActionResult> Search(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return Json(new { success = false, message = "Please enter a search term" });

            try
            {
                var results = await _songService.SearchSongsAsync(query);
                return Json(new { success = true, results });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error searching songs: {Query}", query);
                return Json(new { success = false, message = "Search failed. Try again later." });
            }
        }

        // POST: /Song/Request
        [HttpPost]
        public async Task<IActionResult> RequestSong(string videoId, string title, string artist, string requesterName)
        {
            if (string.IsNullOrWhiteSpace(videoId) || string.IsNullOrWhiteSpace(title))
                return Json(new { success = false, message = "Invalid song request" });

            try
            {
                var details = await _songService.GetSongDetailsAsync(videoId);

                var song = new Song
                {
                    YoutubeVideoId = videoId,
                    Title = title,
                    Artist = string.IsNullOrWhiteSpace(artist) ? details?.ChannelTitle ?? "Unknown Artist" : artist,
                    ThumbnailUrl = details?.ThumbnailUrl,
                    Duration = details?.Duration ?? TimeSpan.FromSeconds(12),
                    RequesterName = requesterName,
                    AddedToQueueAt = DateTime.UtcNow
                };

                _streamingService.EnqueueSong(song);

                // Notify all clients that the queue has updated
                await _hubContext.Clients.All.SendAsync("QueueUpdated", song);

                return Json(new { success = true, message = $"'{title}' has been added to the queue" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error requesting song: {VideoId}", videoId);
                return Json(new { success = false, message = "Could not request this song." });
            }
        }

    }
}
