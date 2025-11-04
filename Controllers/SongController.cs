using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using RadioStation.Models;
using RadioStation.Services;
using RadioHubSignalR = RadioStation.Hubs.RadioHub;

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
                return Json(new { success = true, songs = results });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error searching songs: {Query}", query);
                return Json(new { success = false, message = "Search failed. Try again later." });
            }
        }

        // POST: /Song/Request
        [HttpPost]
        public async Task<IActionResult> RequestSong(string videoId, string title, string artist, string requesterName,string note)
        {
            if (string.IsNullOrWhiteSpace(videoId) || string.IsNullOrWhiteSpace(title))
                return Json(new { success = false, message = "Invalid song request" });

            try
            {
                // Validate that the video is a song
                var isSong = await _songService.IsSongAsync(videoId);
                if (!isSong)
                {
                    return Json(new { success = false, message = "Only songs can be added to the queue. This video appears to be non-music content." });
                }

                var details = await _songService.GetSongDetailsAsync(videoId);

                var song = new Song
                {
                    YoutubeVideoId = videoId,
                    Title = title,
                    Artist = string.IsNullOrWhiteSpace(artist) ? details?.ChannelTitle ?? "Unknown Artist" : artist,
                    ThumbnailUrl = details?.ThumbnailUrl,
                    Duration = details?.Duration ?? TimeSpan.FromSeconds(12),
                    RequesterName = requesterName,
                    Note = note,
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

        [HttpGet]
        public async Task<IActionResult> GetQueueStatus()
        {
            try
            {
                _logger.LogDebug("GetQueueStatus called");

                var queue = _streamingService.GetQueue();
                var currentlyPlaying = _streamingService.GetCurrentlyPlaying();

                _logger.LogDebug("Queue status: {Count} songs in queue, Currently playing: {Title}", queue.Count, currentlyPlaying?.Title ?? "None");

                // Calculate remaining time for currently playing song
                int? remainingTime = null;
                if (currentlyPlaying?.StartedPlayingAt != null && currentlyPlaying?.Duration.TotalSeconds > 0)
                {
                    var elapsed = (DateTime.UtcNow - currentlyPlaying.StartedPlayingAt.Value).TotalSeconds;
                    remainingTime = (int)Math.Max(0, currentlyPlaying.Duration.TotalSeconds - elapsed);
                }

                return Json(new
                {
                    queue = queue.Select(s => new
                    {
                        youtubeVideoId = s.YoutubeVideoId,
                        title = s.Title,
                        requesterName = s.RequesterName,
                        note = s.Note,
                        duration = s.Duration,
                        addedAt = s.AddedToQueueAt.ToString("o")
                    }),
                    currentlyPlaying = currentlyPlaying != null ? new
                    {
                        youtubeVideoId = currentlyPlaying.YoutubeVideoId,
                        title = currentlyPlaying.Title,
                        requesterName = currentlyPlaying.RequesterName,
                        note = currentlyPlaying.Note,
                        duration = currentlyPlaying.Duration,
                        remainingTime = remainingTime,
                        startedAt = currentlyPlaying.StartedPlayingAt?.ToString("o")
                    } : null
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting queue status");
                return Json(new { error = "Failed to get queue status" });
            }
        }

        [HttpGet]
        public IActionResult GetRadioStationStatus()
        {
            try
            {
                // Check if the streaming service supports enhanced features
                if (_streamingService is LiquidSoapStreamingService liquidSoapService)
                {
                    var status = liquidSoapService.GetRadioStationStatus();
                    return Json(new { success = true, status });
                }
                else
                {
                    // Fallback for other streaming services
                    var queue = _streamingService.GetQueue();
                    var currentlyPlaying = _streamingService.GetCurrentlyPlaying();

                    return Json(new {
                        success = true,
                        status = new
                        {
                            IsStreaming = true,
                            CurrentSong = currentlyPlaying,
                            UserRequestsCount = queue.Count,
                            DefaultPlaylistCount = 0,
                            TotalSongsCount = queue.Count,
                            HasUserRequests = queue.Count > 0,
                            IsPlayingDefaultPlaylist = queue.Count == 0,
                            ServiceType = "Basic"
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting radio station status");
                return Json(new { success = false, error = "Failed to get radio station status" });
            }
        }

        [HttpGet]
        public IActionResult GetDefaultPlaylist()
        {
            try
            {
                // Check if the streaming service supports enhanced features
                if (_streamingService is LiquidSoapStreamingService liquidSoapService)
                {
                    var defaultPlaylist = liquidSoapService.GetDefaultPlaylist();

                    return Json(new {
                        success = true,
                        songs = defaultPlaylist.Select(s => new
                        {
                            youtubeVideoId = s.YoutubeVideoId,
                            title = s.Title,
                            duration = s.Duration
                        }).ToList(),
                        count = defaultPlaylist.Count
                    });
                }
                else
                {
                    return Json(new { success = true, songs = new List<object>(), count = 0 });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting default playlist");
                return Json(new { success = false, error = "Failed to get default playlist" });
            }
        }

        [HttpGet]
        public IActionResult GetUserRequests()
        {
            try
            {
                // Check if the streaming service supports enhanced features
                if (_streamingService is LiquidSoapStreamingService liquidSoapService)
                {
                    var userRequests = liquidSoapService.GetUserRequests();

                    return Json(new {
                        success = true,
                        requests = userRequests.Select(s => new
                        {
                            youtubeVideoId = s.YoutubeVideoId,
                            title = s.Title,
                            requesterName = s.RequesterName,
                            note = s.Note,
                            duration = s.Duration,
                            addedAt = s.AddedToQueueAt.ToString("o")
                        }).ToList(),
                        count = userRequests.Count
                    });
                }
                else
                {
                    // Fallback to queue for basic service
                    var queue = _streamingService.GetQueue();

                    return Json(new {
                        success = true,
                        requests = queue.Select(s => new
                        {
                            youtubeVideoId = s.YoutubeVideoId,
                            title = s.Title,
                            requesterName = s.RequesterName,
                            note = s.Note,
                            duration = s.Duration,
                            addedAt = s.AddedToQueueAt.ToString("o")
                        }).ToList(),
                        count = queue.Count
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting user requests");
                return Json(new { success = false, error = "Failed to get user requests" });
            }
        }

    }
}
