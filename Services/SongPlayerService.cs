using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RadioStation.Models;

namespace RadioStation.Services
{
    public class SongPlayerService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<SongPlayerService> _logger;
        private readonly object _stateLock = new object();

        public SongPlayerService(IServiceProvider serviceProvider, ILogger<SongPlayerService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("SongPlayerService started");
            
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var songQueueService = scope.ServiceProvider.GetRequiredService<SongQueueService>();
                    var streamingService = scope.ServiceProvider.GetRequiredService<StreamingService>();

                    // Check if current song is finished
                    var currentlyPlaying = songQueueService.GetCurrentlyPlaying();
                    if (currentlyPlaying != null)
                    {
                        _logger.LogDebug($"Currently playing: {currentlyPlaying.Title}");
                        if (IsSongFinished(currentlyPlaying))
                        {
                            _logger.LogInformation($"Song finished: {currentlyPlaying.Title}");
                            songQueueService.MarkSongAsFinished();
                        }
                    }

                    // Check if we need to play the next song
                    currentlyPlaying = songQueueService.GetCurrentlyPlaying();
                    if (currentlyPlaying == null)
                    {
                        _logger.LogDebug("No song currently playing, checking for next song");
                        var nextSong = songQueueService.GetNextSong();
                        if (nextSong != null)
                        {
                            _logger.LogInformation($"Starting playback: {nextSong.Title}");
                            await PlaySongAsync(nextSong, streamingService, songQueueService, stoppingToken);
                        }
                        else
                        {
                            _logger.LogDebug("No next song available");
                        }
                    }

                    await Task.Delay(1000, stoppingToken); // Check every second
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in SongPlayerService");
                    await Task.Delay(5000, stoppingToken); // Wait longer on error
                }
            }
            
            _logger.LogInformation("SongPlayerService stopped");
        }

        private async Task PlaySongAsync(SongRequest song, StreamingService streamingService, SongQueueService songQueueService, CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation($"Starting to play song: {song.Title} (ID: {song.YoutubeVideoId})");
                
                // Download the audio
                _logger.LogInformation($"Downloading audio for: {song.Title}");
                var filePath = await streamingService.DownloadAudioAsync(song.YoutubeVideoId);
                
                if (string.IsNullOrEmpty(filePath))
                {
                    _logger.LogError($"Failed to download audio for: {song.Title}");
                    songQueueService.MarkSongAsFinished();
                    return;
                }

                song.FilePath = filePath;
                _logger.LogInformation($"Audio downloaded for: {song.Title} - {filePath}");
                
                // Start streaming the audio
                streamingService.StartStreaming(filePath);
                
                // Wait for the song to finish streaming
                while (streamingService.IsStreaming() && !cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(1000, cancellationToken);
                }
                
                _logger.LogInformation($"Song completed: {song.Title}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error playing song: {song.Title}");
                songQueueService.MarkSongAsFinished();
            }
        }

        private bool IsSongFinished(SongRequest song)
        {
            if (song == null || !song.StartedPlayingAt.HasValue)
                return false;

            var duration = song.Duration * 1000; // Convert seconds to milliseconds
            var elapsed = DateTime.UtcNow - song.StartedPlayingAt.Value;
            return elapsed.TotalMilliseconds >= duration;
        }

        private int ParseDuration(string duration)
        {
            try
            {
                // Parse duration format like "3:45" or "1:23:45"
                var parts = duration.Split(':');
                if (parts.Length == 2)
                {
                    var minutes = int.Parse(parts[0]);
                    var seconds = int.Parse(parts[1]);
                    return (minutes * 60 + seconds) * 1000; // Convert to milliseconds
                }
                else if (parts.Length == 3)
                {
                    var hours = int.Parse(parts[0]);
                    var minutes = int.Parse(parts[1]);
                    var seconds = int.Parse(parts[2]);
                    return (hours * 3600 + minutes * 60 + seconds) * 1000; // Convert to milliseconds
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error parsing duration: {duration}");
            }
            
            return 180000; // Default to 3 minutes if parsing fails
        }
    }
}
