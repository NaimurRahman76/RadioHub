using System.Collections.Generic;
using System.Linq;

namespace RadioStation.Models
{
    public class SongQueueViewModel
    {
        public IEnumerable<SongRequest> Queue { get; set; } = Enumerable.Empty<SongRequest>();
        public SongRequest? CurrentlyPlaying { get; set; }
    }
}
