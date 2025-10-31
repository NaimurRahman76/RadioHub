using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using RadioStation.Models;

namespace RadioStation.Services
{
    public class SongQueueService
    {
        private ConcurrentQueue<SongRequest> _songQueue = new ConcurrentQueue<SongRequest>();
        private SongRequest? _currentlyPlaying;
        public event Action? QueueChanged;
        private readonly ILogger<SongQueueService> _logger;

        public SongQueueService(ILogger<SongQueueService> logger)
        {
            _logger = logger;
        }

        public void AddSong(SongRequest song)
        {
            _logger.LogInformation($"Adding song to queue: {song.Title} (ID: {song.YoutubeVideoId})");
            _songQueue.Enqueue(song);
            _logger.LogInformation($"Song added successfully. Queue count: {_songQueue.Count}");
            QueueChanged?.Invoke();
        }

        public SongRequest? GetNextSong()
        {
            _logger.LogInformation($"Getting next song. Current queue count: {_songQueue.Count}");
            
            // Mark current song as finished if there is one
            MarkSongAsFinished();

            if (_songQueue.TryDequeue(out var song))
            {
                _logger.LogInformation($"Retrieved next song: {song.Title} (ID: {song.YoutubeVideoId})");
                _currentlyPlaying = song;
                _currentlyPlaying.IsPlaying = true;
                _currentlyPlaying.StartedPlayingAt = DateTime.UtcNow;
                return _currentlyPlaying;
            }
            
            _logger.LogInformation("No more songs in queue");
            _currentlyPlaying = default;
            return default;
        }

        public IEnumerable<SongRequest> GetQueue()
        {
            var queue = _songQueue.ToList();
            _logger.LogDebug($"Retrieved queue with {queue.Count} songs");
            return queue;
        }

        public SongRequest? GetCurrentlyPlaying()
        {
            _logger.LogDebug($"Currently playing: {_currentlyPlaying?.Title ?? "None"}");
            return _currentlyPlaying;
        }

        public void MarkSongAsFinished()
        {
            if (_currentlyPlaying != null)
            {
                _logger.LogInformation($"Marking song as finished: {_currentlyPlaying.Title} (ID: {_currentlyPlaying.YoutubeVideoId})");
                _currentlyPlaying.IsPlaying = false;
                _currentlyPlaying.StartedPlayingAt = null;
                _currentlyPlaying = null;
            }
        }
        public void ClearQueue()
        {
            _logger.LogInformation($"Clearing queue. Current count: {_songQueue.Count}");
            
            // Clear the queue
            while (_songQueue.TryDequeue(out _)) { }
            
            // Mark current song as finished
            MarkSongAsFinished();
            QueueChanged?.Invoke();
            
            _logger.LogInformation("Queue cleared successfully");
        }
        
        public void RemoveSong(string youtubeVideoId)
        {
            if (string.IsNullOrEmpty(youtubeVideoId))
            {
                _logger.LogWarning("Attempted to remove song with empty YouTube video ID");
                return;
            }
            
            _logger.LogInformation($"Attempting to remove song with YouTube video ID: {youtubeVideoId}");
            
            bool wasChanged = false;
            // Create a new queue without the song to remove
            var newQueue = new ConcurrentQueue<SongRequest>();
            
            // Copy all songs except the one to remove
            foreach (var song in _songQueue)
            {
                if (song.YoutubeVideoId != youtubeVideoId)
                {
                    newQueue.Enqueue(song);
                }
                else
                {
                    wasChanged = true;
                    _logger.LogInformation($"Removed song: {song.Title} (ID: {song.YoutubeVideoId})");
                }
            }
            
            // Only update if we actually removed something
            if (wasChanged)
            {
                _songQueue = newQueue;
                QueueChanged?.Invoke();
                _logger.LogInformation($"Song removed successfully. New queue count: {_songQueue.Count}");
            }
            else
            {
                _logger.LogWarning($"No song found with YouTube video ID: {youtubeVideoId}");
            }
        }
    }
}
