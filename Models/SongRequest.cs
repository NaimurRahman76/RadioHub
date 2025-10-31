namespace RadioStation.Models
{
    public class SongRequest
    {
        public string YoutubeVideoId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string RequesterName { get; set; } = string.Empty;
        public string Note { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;

        public SongRequest()
        {
        }

        public SongRequest(string youtubeVideoId, string title, string requesterName)
        {
            YoutubeVideoId = youtubeVideoId;
            Title = title;
            RequesterName = requesterName;
        }
        public bool IsPlaying { get; set; }
        public int Duration { get; set; } // Duration in seconds
        public DateTime? StartedPlayingAt { get; set; }
        public DateTime? AddedToQueueAt { get; set; } = DateTime.UtcNow;
        
        // Calculated property to get remaining duration
        public int? GetRemainingDuration()
        {
            if (!StartedPlayingAt.HasValue || Duration <= 0)
                return null;
                
            var elapsed = (DateTime.UtcNow - StartedPlayingAt.Value).TotalSeconds;
            return Math.Max(0, (int)(Duration - elapsed));
        }
    }
}
