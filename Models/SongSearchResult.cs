using System;

namespace RadioStation.Models
{
    public class SongSearchResult
    {
        public string VideoId { get; set; } = string.Empty;
        
        public string Title { get; set; } = string.Empty;
        
        public string ChannelTitle { get; set; } = string.Empty;
        
        public string Artist => ChannelTitle; // Alias for ChannelTitle for consistency in views
        
        public string ThumbnailUrl { get; set; } = string.Empty;
        
        public TimeSpan Duration { get; set; }
        
        public string DurationDisplay => FormatDuration(Duration);
        
        private static string FormatDuration(TimeSpan duration)
        {
            if (duration.TotalHours >= 1)
                return $"{duration.Hours:D2}:{duration.Minutes:D2}:{duration.Seconds:D2}";
            else
                return $"{duration.Minutes:D2}:{duration.Seconds:D2}";
        }
    }
}