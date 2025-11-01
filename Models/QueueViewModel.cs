using System;
using System.Collections.Generic;
using System.Linq;

namespace RadioStation.Models
{
    public class QueueViewModel
    {
        public Song? CurrentlyPlaying { get; set; }
        
        public List<Song> Queue { get; set; } = new List<Song>();
        
        public int TotalSongs => Queue.Count + (CurrentlyPlaying != null ? 1 : 0);
        
        public TimeSpan? RemainingTime => CurrentlyPlaying?.GetRemainingDuration();
        
        public bool IsPlaying => CurrentlyPlaying != null && CurrentlyPlaying.IsPlaying;
        
        public string CurrentTimeDisplay => CurrentlyPlaying != null ? 
            $"{FormatTime(CurrentlyPlaying.Duration - (CurrentlyPlaying.GetRemainingDuration() ?? TimeSpan.Zero))} / {FormatTime(CurrentlyPlaying.Duration)}" : 
            "00:00 / 00:00";
        
        private static string FormatTime(TimeSpan time)
        {
            if (time.TotalHours >= 1)
                return $"{time.Hours:D2}:{time.Minutes:D2}:{time.Seconds:D2}";
            else
                return $"{time.Minutes:D2}:{time.Seconds:D2}";
        }
    }
}