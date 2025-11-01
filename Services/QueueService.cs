using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RadioStation.Data;
using RadioStation.Models;

namespace RadioStation.Services
{
    public class QueueService : IQueueService
    {
        private readonly ApplicationDbContext _context;
        private readonly ILogger<QueueService> _logger;

        public QueueService(ApplicationDbContext context, ILogger<QueueService> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task AddSongAsync(Song song)
        {
            _logger.LogInformation("Adding song to queue: {Title} by {RequesterName}", song.Title, song.RequesterName);
            
            _context.Songs.Add(song);
            await _context.SaveChangesAsync();
        }

        public async Task<Song?> GetNextSongAsync()
        {
            var nextSong = await _context.Songs
                .Where(s => !s.IsFinished && !s.IsPlaying)
                .OrderBy(s => s.AddedToQueueAt)
                .FirstOrDefaultAsync();
                
            if (nextSong != null)
            {
                _logger.LogInformation("Retrieved next song from queue: {Title}", nextSong.Title);
            }

            return nextSong;
        }

        public async Task<List<Song>> GetQueueAsync()
        {
            return await _context.Songs
                .Where(s => !s.IsFinished)
                .OrderBy(s => s.AddedToQueueAt)
                .ToListAsync();
        }

        public async Task<Song?> GetCurrentlyPlayingAsync()
        {
            return await _context.Songs
                .FirstOrDefaultAsync(s => s.IsPlaying);
        }

        public async Task MarkSongAsPlayingAsync(string videoId)
        {
            var song = await _context.Songs
                .FirstOrDefaultAsync(s => s.YoutubeVideoId == videoId);
                
            if (song != null)
            {
                song.IsPlaying = true;
                song.StartedPlayingAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();
                _logger.LogInformation("Marked song as playing: {Title}", song.Title);
            }
        }

        public async Task MarkSongAsFinishedAsync(string videoId)
        {
            var song = await _context.Songs
                .FirstOrDefaultAsync(s => s.YoutubeVideoId == videoId);
                
            if (song != null)
            {
                song.IsPlaying = false;
                song.IsFinished = true;
                await _context.SaveChangesAsync();
                _logger.LogInformation("Marked song as finished: {Title}", song.Title);
            }
        }

        public async Task<bool> RemoveSongAsync(string videoId)
        {
            var song = await _context.Songs
                .FirstOrDefaultAsync(s => s.YoutubeVideoId == videoId);
                
            if (song != null)
            {
                _context.Songs.Remove(song);
                await _context.SaveChangesAsync();
                _logger.LogInformation("Removed song from queue: {Title}", song.Title);
                return true;
            }

            return false;
        }

        public async Task<bool> IsSongInQueueAsync(string videoId)
        {
            return await _context.Songs
                .AnyAsync(s => s.YoutubeVideoId == videoId && !s.IsFinished);
        }

        public async Task ClearQueueAsync()
        {
            _logger.LogInformation("Clearing song queue");
            
            var songs = await _context.Songs.ToListAsync();
            _context.Songs.RemoveRange(songs);
            await _context.SaveChangesAsync();
        }

        public async Task<QueueViewModel> GetQueueViewModelAsync()
        {
            var queue = await GetQueueAsync();
            var currentlyPlaying = await GetCurrentlyPlayingAsync();
            
            var viewModel = new QueueViewModel
            {
                CurrentlyPlaying = currentlyPlaying,
                Queue = queue
            };
            
            return viewModel;
        }
    }
}