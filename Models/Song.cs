using System;
using System.ComponentModel.DataAnnotations;

namespace RadioStation.Models
{
    public class Song
    {
        public int Id { get; set; }
        
        [Required]
        public string Title { get; set; } = string.Empty;
        
        [Required]
        public string Artist { get; set; } = string.Empty;
        
        [Required]
        public string YoutubeVideoId { get; set; } = string.Empty;
        
        public string? ThumbnailUrl { get; set; }
        
        public string? Note { get; set; }
        
        public string? FilePath { get; set; }
        
        public TimeSpan Duration { get; set; }
        
        public string RequesterName { get; set; } = string.Empty;
        
        public DateTime AddedToQueueAt { get; set; } = DateTime.UtcNow;
        
        public DateTime? StartedPlayingAt { get; set; }
        
        public bool IsPlaying { get; set; } = false;
        
        public bool IsFinished { get; set; } = false;
        
        public TimeSpan? GetRemainingDuration()
        {
            if (!IsPlaying || StartedPlayingAt == null)
                return Duration;
                
            var elapsed = DateTime.UtcNow - StartedPlayingAt.Value;
            var remaining = Duration - elapsed;
            
            return remaining.TotalSeconds > 0 ? remaining : TimeSpan.Zero;
        }
    }
}