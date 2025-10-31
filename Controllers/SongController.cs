using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using RadioStation.Models;
using RadioStation.Services;
using System.Threading.Tasks;
using Google.Apis.Services;
using Google.Apis.YouTube.v3;
using System.Linq;
using YoutubeExplode;
using Microsoft.AspNetCore.SignalR;

namespace RadioStation.Controllers
{
    public class SongController : Controller
    {
        private readonly SongQueueService _songQueueService;
        private readonly StreamingService _streamingService;
        private readonly IConfiguration _configuration;
        private readonly ILogger<SongController> _logger;
        private readonly IHubContext<RadioHub> _hubContext;
        private static readonly Lazy<YoutubeClient> _youtubeClient = new Lazy<YoutubeClient>(() => new YoutubeClient());

        public SongController(SongQueueService songQueueService,
            StreamingService streamingService,
            ILogger<SongController> logger,
            IConfiguration configuration,
            IHubContext<RadioHub> hubContext)
        {
            _songQueueService = songQueueService;
            _streamingService = streamingService;
            _logger = logger;
            _configuration = configuration;
            _hubContext = hubContext;
            
            // Subscribe to queue changes
            _songQueueService.QueueChanged += async () =>
            {
                await UpdateQueueClients();
            };
        }

        public IActionResult Index()
        {
            var queue = _songQueueService.GetQueue();
            var currentlyPlaying = _songQueueService.GetCurrentlyPlaying();
            var model = new SongQueueViewModel
            {
                Queue = queue,
                CurrentlyPlaying = currentlyPlaying
            };
            return View(model);
        }

        [HttpGet]
        public IActionResult Search()
        {
            return View();
        }

        [HttpPost]
        public async Task<IActionResult> Search(string query)
        {
            try
            {
                if (string.IsNullOrEmpty(query)) 
                    return Json(new { success = false, message = "Search query cannot be empty" });

                var youtubeService = new YouTubeService(new BaseClientService.Initializer()
                {
                    ApiKey = _configuration["YouTube:ApiKey"],
                    ApplicationName = "RadioHub"
                });

                var searchRequest = youtubeService.Search.List("snippet");
                searchRequest.Q = query;
                searchRequest.MaxResults = 10;
                searchRequest.Type = "video";

                var searchResponse = await searchRequest.ExecuteAsync();

                var searchResults = searchResponse.Items.Select(item => new 
                {
                    youtubeVideoId = item.Id.VideoId,
                    title = item.Snippet.Title
                }).ToList();

                return Json(new { success = true, results = searchResults });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Search error: {ex.Message}");
                return Json(new { success = false, message = "Error performing search. Please try again." });
            }
        }

        [HttpPost]
        public async Task<IActionResult> RequestSong(string youtubeVideoId, string title, string requesterName, string note)
        {
            try
            {
                _logger.LogInformation($"RequestSong called with: YouTubeID={youtubeVideoId}, Title={title}, Requester={requesterName}");
                
                // Get video duration from YouTube
                var youtube = _youtubeClient.Value;
                var video = await youtube.Videos.GetAsync(youtubeVideoId);
                
                var songRequest = new SongRequest
                {
                    YoutubeVideoId = youtubeVideoId,
                    Title = title,
                    RequesterName = string.IsNullOrWhiteSpace(requesterName) ? "Anonymous" : requesterName.Trim(),
                    Note = note,
                    Duration = (int)(video.Duration?.TotalSeconds ?? 0),
                    AddedToQueueAt = DateTime.UtcNow
                };

                // Add song to queue
                _songQueueService.AddSong(songRequest);
                
                // Get the current queue position
                var queue = _songQueueService.GetQueue().ToList();
                var position = queue.FindIndex(s => s.YoutubeVideoId == youtubeVideoId) + 1;
                
                // Just update the queue - SongPlayerService will handle starting playback automatically
                await UpdateQueueClients();

                _logger.LogInformation($"Song request processed successfully: {title}");
                return Json(new { 
                    success = true, 
                    message = $"Added to queue (#{position})",
                    position = position
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing song request");
                return Json(new { 
                    success = false, 
                    message = "Failed to add song to queue. Please try again.",
                    error = ex.Message
                });
            }
        }
        
        [HttpPost]
        public async Task<IActionResult> PlayNextSong()
        {
            try
            {
                // Mark current song as finished if there is one
                var current = _songQueueService.GetCurrentlyPlaying();
                if (current != null)
                {
                    _songQueueService.MarkSongAsFinished();
                }

                // Get the next song from the queue
                var nextSong = _songQueueService.GetNextSong();
                if (nextSong != null)
                {
                    // Set as currently playing
                    nextSong.StartedPlayingAt = DateTime.UtcNow;
                    
                    // Start streaming the song (synchronous call)
                    _streamingService.StartStreaming(nextSong.YoutubeVideoId);
                    
                    // Update all clients
                    await UpdateQueueClients();
                    
                    _logger.LogInformation($"Now playing: {nextSong.Title} (ID: {nextSong.YoutubeVideoId})");
                    return Json(new { 
                        success = true, 
                        message = $"Now playing: {nextSong.Title}",
                        song = new {
                            youtubeVideoId = nextSong.YoutubeVideoId,
                            title = nextSong.Title,
                            duration = nextSong.Duration
                        }
                    });
                }
                
                // No more songs in queue
                _logger.LogInformation("No more songs in the queue");
                return Json(new { 
                    success = true, 
                    message = "No more songs in the queue",
                    queueEmpty = true
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in PlayNextSong");
                return Json(new { 
                    success = false, 
                    message = "Error playing next song",
                    error = ex.Message
                });
            }
        }

        [HttpPost]
        public IActionResult StopCurrentSong()
        {
            try
            {
                _streamingService.StopStreaming();
                _songQueueService.MarkSongAsFinished();
                TempData["Message"] = "Playback stopped.";
            }
            catch (Exception ex)
            {
                TempData["Error"] = "Failed to stop playback. Please try again.";
                Console.WriteLine($"Error in StopCurrentSong: {ex.Message}");
            }

            return RedirectToAction("Index");
        }

        [HttpPost]
        public async Task<IActionResult> ClearQueue()
        {
            try
            {
                _songQueueService.ClearQueue();
                await UpdateQueueClients();
                TempData["Message"] = "Queue has been cleared.";
            }
            catch (Exception ex)
            {
                TempData["Error"] = "Failed to clear the queue. Please try again.";
                _logger.LogError(ex, "Error clearing queue");
            }

            return RedirectToAction("Index");
        }

        [HttpPost]
        public async Task<IActionResult> RemoveSong(string youtubeVideoId)
        {
            try
            {
                _songQueueService.RemoveSong(youtubeVideoId);
                await UpdateQueueClients();
                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error removing song");
                return Json(new { success = false, error = ex.Message });
            }
        }
        
        private async Task UpdateQueueClients()
        {
            try
            {
                var queue = _songQueueService.GetQueue().ToList();
                var currentlyPlaying = _songQueueService.GetCurrentlyPlaying();
                
                // Calculate remaining time for currently playing song
                int? remainingTime = null;
                if (currentlyPlaying?.StartedPlayingAt != null && currentlyPlaying.Duration > 0)
                {
                    var elapsed = (DateTime.UtcNow - currentlyPlaying.StartedPlayingAt.Value).TotalSeconds;
                    remainingTime = (int)Math.Max(0, currentlyPlaying.Duration - elapsed);
                    
                    // If song has ended, automatically play next
                    if (remainingTime <= 0)
                    {
                        _logger.LogInformation($"Song {currentlyPlaying.Title} has ended, playing next song");
                        await PlayNextSong();
                        return;
                    }
                }
                
                var queueData = new
                {
                    queue = queue.Select((s, index) => new
                    {
                        position = index + 1,
                        youtubeVideoId = s.YoutubeVideoId,
                        title = s.Title,
                        requesterName = s.RequesterName,
                        note = s.Note,
                        duration = s.Duration,
                        addedAt = s.AddedToQueueAt?.ToString("o"),
                        isPlaying = currentlyPlaying?.YoutubeVideoId == s.YoutubeVideoId
                    }).ToList(),
                    currentlyPlaying = currentlyPlaying != null ? new
                    {
                        youtubeVideoId = currentlyPlaying.YoutubeVideoId,
                        title = currentlyPlaying.Title,
                        requesterName = currentlyPlaying.RequesterName,
                        note = currentlyPlaying.Note,
                        duration = currentlyPlaying.Duration,
                        remainingTime = remainingTime,
                        startedAt = currentlyPlaying.StartedPlayingAt?.ToString("o"),
                        progress = remainingTime.HasValue && currentlyPlaying.Duration > 0 
                            ? 100 - ((double)remainingTime / currentlyPlaying.Duration * 100)
                            : 0
                    } : null,
                    timestamp = DateTime.UtcNow.ToString("o")
                };
                
                _logger.LogDebug($"Updating queue clients. {queue.Count} songs in queue, " +
                               $"currently playing: {currentlyPlaying?.Title ?? "None"}");
                
                // Update all connected clients
                await _hubContext.Clients.All.SendAsync("QueueUpdated", queueData);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating queue clients");
                // Try to recover by refreshing the queue
                try {
                    await Task.Delay(1000);
                    await UpdateQueueClients();
                } catch {
                    // If we fail again, just log and continue
                    _logger.LogError("Failed to recover from queue update error");
                }
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetQueueStatus()
        {
            try
            {
                _logger.LogDebug("GetQueueStatus called");
                
                var queue = _songQueueService.GetQueue().ToList();
                var currentlyPlaying = _songQueueService.GetCurrentlyPlaying();

                _logger.LogDebug($"Queue status: {queue.Count} songs in queue, Currently playing: {currentlyPlaying?.Title ?? "None"}");
                
                // Calculate remaining time for currently playing song
                int? remainingTime = null;
                if (currentlyPlaying?.StartedPlayingAt != null && currentlyPlaying.Duration > 0)
                {
                    var elapsed = (DateTime.UtcNow - currentlyPlaying.StartedPlayingAt.Value).TotalSeconds;
                    remainingTime = (int)Math.Max(0, currentlyPlaying.Duration - elapsed);
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
                        addedAt = s.AddedToQueueAt?.ToString("o")
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
    }
}
