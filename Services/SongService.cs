using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RadioStation.Models;
using YoutubeExplode;
using YoutubeExplode.Videos;
using YoutubeExplode.Search;
using Google.Apis.YouTube.v3;
using Google.Apis.Services;

namespace RadioStation.Services
{
    public class SongService : ISongService
    {
        private readonly YoutubeClient _youtubeClient;
        private readonly YouTubeService? _youtubeService;
        private readonly ILogger<SongService> _logger;
        private readonly IConfiguration _configuration;

        public SongService(ILogger<SongService> logger, IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
            _youtubeClient = new YoutubeClient();

            // Initialize official YouTube API service
            var apiKey = _configuration["YouTubeApiKey"];
            if (!string.IsNullOrEmpty(apiKey))
            {
                _youtubeService = new YouTubeService(new BaseClientService.Initializer
                {
                    ApiKey = apiKey,
                    ApplicationName = "RadioHub"
                });
            }
            else
            {
                _logger.LogWarning("YouTube API key not found in configuration. Using fallback search method.");
                _youtubeService = null!;
            }
        }

        public async Task<List<SongSearchResult>> SearchSongsAsync(string query)
        {
            try
            {
                _logger.LogInformation("Searching for songs with query: {Query}", query);

                // Try official YouTube API first for accurate music category filtering
                if (_youtubeService != null)
                {
                    var officialResults = await SearchWithOfficialAPIAsync(query);
                    if (officialResults.Any())
                    {
                        _logger.LogInformation("Found {Count} songs using official YouTube API for query: {Query}", officialResults.Count, query);
                        return officialResults;
                    }
                }

                // Fallback to YoutubeExplode with enhanced filtering
                return await SearchWithYoutubeExplodeAsync(query);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error searching for songs with query: {Query}", query);
                throw;
            }
        }

        private async Task<List<SongSearchResult>> SearchWithOfficialAPIAsync(string query)
        {
            try
            {
                var allResults = new List<SongSearchResult>();

                // Strategy 1: Search without category restriction but with music keywords
                var musicKeywordsQuery = $"{query} song music audio গান গানের";
                var searchRequest1 = _youtubeService.Search.List("snippet");
                searchRequest1.Q = musicKeywordsQuery;
                searchRequest1.Type = "video";
                searchRequest1.VideoDuration = SearchResource.ListRequest.VideoDurationEnum.Medium; // 4-20 minutes
                searchRequest1.MaxResults = 15;

                var searchResponse1 = await searchRequest1.ExecuteAsync();
                await ProcessSearchResults(searchResponse1.Items, allResults);

                // Strategy 2: Try pure search if we don't have enough results
                if (allResults.Count < 10)
                {
                    var searchRequest2 = _youtubeService.Search.List("snippet");
                    searchRequest2.Q = query;
                    searchRequest2.Type = "video";
                    searchRequest2.VideoDuration = SearchResource.ListRequest.VideoDurationEnum.Medium; // 4-20 minutes
                    searchRequest2.MaxResults = 20;

                    var searchResponse2 = await searchRequest2.ExecuteAsync();
                    await ProcessSearchResults(searchResponse2.Items, allResults);
                }

                // Strategy 3: If still not enough, try without duration restriction
                if (allResults.Count < 10)
                {
                    var searchRequest3 = _youtubeService.Search.List("snippet");
                    searchRequest3.Q = $"{query} song music গান";
                    searchRequest3.Type = "video";
                    searchRequest3.MaxResults = 15;

                    var searchResponse3 = await searchRequest3.ExecuteAsync();
                    await ProcessSearchResults(searchResponse3.Items, allResults);
                }

                // Remove duplicates and apply smart filtering
                var uniqueResults = allResults
                    .GroupBy(x => x.VideoId)
                    .Select(g => g.First())
                    .Where(IsLikelySongFromMetadata)
                    .Take(20)
                    .ToList();

                return uniqueResults;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error using official YouTube API for search: {Query}", query);
                return new List<SongSearchResult>(); // Return empty to trigger fallback
            }
        }

        private async Task ProcessSearchResults(IList<Google.Apis.YouTube.v3.Data.SearchResult> searchItems, List<SongSearchResult> results)
        {
            foreach (var searchItem in searchItems.Take(10)) // Process in batches to avoid rate limits
            {
                try
                {
                    var videoRequest = _youtubeService.Videos.List("contentDetails,snippet");
                    videoRequest.Id = searchItem.Id.VideoId;

                    var videoResponse = await videoRequest.ExecuteAsync();
                    var video = videoResponse.Items.FirstOrDefault();

                    if (video != null)
                    {
                        var duration = System.Xml.XmlConvert.ToTimeSpan(video.ContentDetails.Duration);

                        // Accept broader duration range: 30 seconds to 15 minutes
                        if (duration.TotalSeconds >= 30 && duration.TotalSeconds <= 900)
                        {
                            results.Add(new SongSearchResult
                            {
                                VideoId = video.Id,
                                Title = video.Snippet.Title,
                                ChannelTitle = video.Snippet.ChannelTitle,
                                ThumbnailUrl = video.Snippet.Thumbnails?.Medium?.Url ?? video.Snippet.Thumbnails?.Standard?.Url ?? string.Empty,
                                Duration = duration
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error processing video {VideoId}", searchItem.Id?.VideoId);
                }
            }
        }

        private bool IsLikelySongFromMetadata(SongSearchResult result)
        {
            var title = result.Title.ToLower();
            var channelTitle = result.ChannelTitle.ToLower();

            // Duration check - already done in search, but double-check
            var durationSeconds = result.Duration.TotalSeconds;
            if (durationSeconds < 30 || durationSeconds > 900)
                return false;

            // Strong positive indicators
            var strongMusicIndicators = new[]
            {
                "official video", "music video", "lyrics", "lyric", "audio", "song", "গান", "গানের",
                "remix", "cover", "acoustic", "live", "concert", "performance",
                "album", "single", "ep", "track", "ট্র্যাক",
                "instrumental", "karaoke", "ক্যারাওকে", "বাংলা", "বাংলা গান"
            };

            // Medium positive indicators
            var mediumMusicIndicators = new[]
            {
                "mv", "music", "audio", "sound", "সঙ্গীত", "সঙ্গীতের",
                "video song", "song video", "ভিডিও গান", "গান ভিডিও"
            };

            // Negative indicators (exclude these)
            var negativeIndicators = new[]
            {
                "interview", "podcast", "documentary", "tutorial", "review", "trailer",
                "movie", "film", "episode", "show", "news", "vlog", "gameplay",
                "how to", "guide", "explanation", "lecture", "talk", "speech",
                "reaction", "compilation", "top 10", "best of", "moments",
                "behind the scenes", "making of", "bloopers", "commentary",
                "analysis", "breakdown", "explainer", "theory", "টিউটোরিয়াল"
            };

            // Check negative indicators first (immediate disqualification)
            if (negativeIndicators.Any(indicator => title.Contains(indicator) || channelTitle.Contains(indicator)))
                return false;

            // Check strong positive indicators
            if (strongMusicIndicators.Any(indicator => title.Contains(indicator) || channelTitle.Contains(indicator)))
                return true;

            // Check medium positive indicators
            var mediumScore = mediumMusicIndicators.Count(indicator => title.Contains(indicator) || channelTitle.Contains(indicator));
            if (mediumScore >= 2) // Need at least 2 medium indicators
                return true;

            // Check for common song patterns (Artist - Song Title)
            if (title.Contains(" - ") || title.Contains("|") || title.Contains("•"))
            {
                // Exclude patterns that suggest playlists or compilations
                if (!title.Contains("mix") && !title.Contains("playlist") && !title.Contains("collection"))
                    return true;
            }

            // Check for music-related channel patterns
            var musicChannelPatterns = new[]
            {
                "music", "song", "audio", "গান", "সঙ্গীত", "official", "vevo",
                "records", "label", "studio", "production", "media"
            };

            return musicChannelPatterns.Any(pattern => channelTitle.Contains(pattern));
        }

        private async Task<List<SongSearchResult>> SearchWithYoutubeExplodeAsync(string query)
        {
            _logger.LogInformation("Using fallback YoutubeExplode search for query: {Query}", query);

            var searchResults = new List<VideoSearchResult>();

            // Search with music-specific keywords including Bangla
            var musicSearchQuery = $"{query} song music audio গান গানের official";
            await foreach (var video in _youtubeClient.Search.GetVideosAsync(musicSearchQuery))
            {
                searchResults.Add(video);
                if (searchResults.Count >= 30)
                    break;
            }

            // If not enough results, do broader search
            if (searchResults.Count < 15)
            {
                await foreach (var video in _youtubeClient.Search.GetVideosAsync(query))
                {
                    if (!searchResults.Any(v => v.Id == video.Id))
                    {
                        searchResults.Add(video);
                        if (searchResults.Count >= 50)
                            break;
                    }
                }
            }

            // Filter with enhanced logic
            var songResults = searchResults
                .Where(v => IsLikelySong(v))
                .OrderByDescending(v => GetSongRelevanceScore(v, query))
                .Take(20)
                .Select(video => new SongSearchResult
                {
                    VideoId = video.Id.Value,
                    Title = video.Title,
                    ChannelTitle = video.Author.ChannelTitle,
                    ThumbnailUrl = video.Thumbnails?.OrderByDescending(t => t.Resolution.Area).FirstOrDefault()?.Url ?? string.Empty,
                    Duration = video.Duration ?? TimeSpan.Zero
                }).ToList();

            _logger.LogInformation("Found {Count} songs using fallback method for query: {Query}", songResults.Count, query);
            return songResults;
        }

        private bool IsLikelySong(VideoSearchResult video)
        {
            // Check if duration is reasonable for a song (30 seconds to 15 minutes)
            var duration = video.Duration ?? TimeSpan.Zero;
            if (duration.TotalSeconds < 30 || duration.TotalSeconds > 900) // Reduced max duration
                return false;

            var title = video.Title.ToLower();
            var channelTitle = video.Author.ChannelTitle.ToLower();

            // Enhanced keywords that suggest music content (including Bangla)
            var musicKeywords = new[]
            {
                "official video", "music video", "lyrics", "lyric", "audio", "song", "track",
                "remix", "cover", "acoustic", "live", "concert", "performance",
                "album", "single", "ep", "playlist", "radio edit", "extended",
                "instrumental", "karaoke", "backing track", "mix", "dub",
                "mv", "clip", "official", "hd", "4k", "visualizer",
                "গান", "গানের", "সঙ্গীত", "সঙ্গীতের", "গাওয়া", "বাংলা গান", "বাংলা গানের",
                "ভিডিও গান", "গান ভিডিও", "অডিও", "অডিও গান", "ক্যারাওকে"
            };

            // Enhanced keywords that suggest non-music content (including Bangla)
            var nonMusicKeywords = new[]
            {
                "interview", "podcast", "documentary", "tutorial", "review", "trailer",
                "movie", "film", "episode", "show", "news", "vlog", "gameplay",
                "how to", "guide", "explanation", "lecture", "talk", "speech",
                "reaction", "compilation", "top 10", "best of", "moments",
                "highlights", "behind the scenes", "making of", "bloopers",
                "commentary", "analysis", "breakdown", "explainer", "theory",
                "টিউটোরিয়াল", "সাক্ষাৎকার", "ডকুমেন্টারি", "খবর", "গেমপ্লে", "রিভিউ"
            };

            // Check for non-music keywords in title or channel - immediate disqualification
            if (nonMusicKeywords.Any(keyword => title.Contains(keyword) || channelTitle.Contains(keyword)))
                return false;

            // Check for music keywords in title or channel - strong indicator
            if (musicKeywords.Any(keyword => title.Contains(keyword) || channelTitle.Contains(keyword)))
                return true;

            // Enhanced title pattern matching for songs
            if (title.Contains(" - ") && !title.Contains(":") && !title.Contains("|") && !title.Contains("#"))
            {
                // Additional check: make sure it's not a "Topic" channel that might be non-music
                if (!channelTitle.Contains("topic") && !title.Contains("topic"))
                    return true;
            }

            // Enhanced channel name patterns for music content (including Bangla)
            var musicChannelPatterns = new[]
            {
                "vevo", "music", "records", "label", "radio", "band", "artist",
                "official", "channel", "entertainment", "warner", "universal",
                "sony", "atlantic", "capitol", "republic", "interscope",
                "সঙ্গীত", "গান", "অডিও", "মিউজিক", "বাংলা", "বাংলা গান"
            };

            if (musicChannelPatterns.Any(pattern => channelTitle.Contains(pattern)))
                return true;

            // Check for common music video patterns in title
            var titlePatterns = new[]
            {
                @"\(\d{4}\)", // Year in parentheses
                @"\[official\]",
                @"\(official\)",
                @"\[lyrics?\]",
                @"\(lyrics?\)",
                @"\[audio\]",
                @"\(audio\)"
            };

            if (titlePatterns.Any(pattern => System.Text.RegularExpressions.Regex.IsMatch(title, pattern)))
                return true;

            // Default to false if none of the above conditions are met
            return false;
        }

        private double GetSongRelevanceScore(VideoSearchResult video, string searchQuery)
        {
            var score = 0.0;
            var title = video.Title.ToLower();
            var channelTitle = video.Author.ChannelTitle.ToLower();
            var query = searchQuery.ToLower();

            // Score for exact title matches
            if (title.Contains(query))
                score += 10;

            // Score for music keywords
            var musicKeywords = new[] { "official video", "music video", "lyrics", "audio", "song", "track", "official" };
            score += musicKeywords.Count(keyword => title.Contains(keyword)) * 3;

            // Score for music channels
            if (channelTitle.Contains("vevo")) score += 5;
            if (channelTitle.Contains("official")) score += 3;

            // Score for ideal duration (3-5 minutes is optimal for songs)
            var durationSeconds = video.Duration?.TotalSeconds ?? 0;
            if (durationSeconds >= 180 && durationSeconds <= 300)
                score += 3;
            else if (durationSeconds >= 120 && durationSeconds <= 420)
                score += 1;

            // Penalty for very long or very short videos
            if (durationSeconds < 60 || durationSeconds > 600)
                score -= 2;

            return score;
        }

        public async Task<SongSearchResult?> GetSongDetailsAsync(string videoId)
        {
            try
            {
                _logger.LogInformation("Getting details for video ID: {VideoId}", videoId);

                var video = await _youtubeClient.Videos.GetAsync(videoId);

                if (video == null)
                {
                    _logger.LogWarning("Video not found with ID: {VideoId}", videoId);
                    return null;
                }

                var songResult = new SongSearchResult
                {
                    VideoId = video.Id.Value,
                    Title = video.Title,
                    ChannelTitle = video.Author.ChannelTitle,
                    ThumbnailUrl = video.Thumbnails?.OrderByDescending(t => t.Resolution.Area).FirstOrDefault()?.Url ?? string.Empty,
                    Duration = video.Duration ?? TimeSpan.Zero
                };

                _logger.LogInformation("Retrieved details for video: {Title}", video.Title);
                return songResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting details for video ID: {VideoId}", videoId);
                throw;
            }
        }

        public async Task<bool> IsSongAsync(string videoId)
        {
            try
            {
                _logger.LogInformation("Checking if video ID is a song: {VideoId}", videoId);

                // Try official YouTube API first for accurate category detection
                if (_youtubeService != null)
                {
                    var isOfficialSong = await IsSongWithOfficialAPIAsync(videoId);
                    if (isOfficialSong.HasValue)
                    {
                        _logger.LogInformation("Video {VideoId} is {IsSong}a song (official API)", videoId, isOfficialSong.Value ? "" : "not ");
                        return isOfficialSong.Value;
                    }
                }

                // Fallback to pattern-based analysis
                return await IsSongWithYoutubeExplodeAsync(videoId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking if video ID is a song: {VideoId}", videoId);
                return false;
            }
        }

        private async Task<bool?> IsSongWithOfficialAPIAsync(string videoId)
        {
            try
            {
                var videoRequest = _youtubeService.Videos.List("snippet,contentDetails");
                videoRequest.Id = videoId;

                var videoResponse = await videoRequest.ExecuteAsync();
                var video = videoResponse.Items.FirstOrDefault();

                if (video == null)
                    return null;

                // Parse duration
                var duration = System.Xml.XmlConvert.ToTimeSpan(video.ContentDetails.Duration);

                // Check duration first
                if (duration.TotalSeconds < 30 || duration.TotalSeconds > 900)
                    return false;

                // Create song result to use with metadata filtering
                var songResult = new SongSearchResult
                {
                    VideoId = video.Id,
                    Title = video.Snippet.Title,
                    ChannelTitle = video.Snippet.ChannelTitle,
                    Duration = duration
                };

                // Use the same smart filtering logic as search
                return IsLikelySongFromMetadata(songResult);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error using official API to check video: {VideoId}", videoId);
                return null; // Return null to trigger fallback
            }
        }

        private async Task<bool> IsSongWithYoutubeExplodeAsync(string videoId)
        {
            try
            {
                var video = await _youtubeClient.Videos.GetAsync(videoId);

                if (video == null)
                {
                    _logger.LogWarning("Video not found with ID: {VideoId}", videoId);
                    return false;
                }

                // Check duration first
                var duration = video.Duration ?? TimeSpan.Zero;
                if (duration.TotalSeconds < 30 || duration.TotalSeconds > 900)
                    return false;

                // Use the same logic as IsLikelySong but directly on the Video object
                var title = video.Title.ToLower();
                var channelTitle = video.Author.ChannelTitle.ToLower();

                // Enhanced keywords that suggest music content
                var musicKeywords = new[]
                {
                    "official video", "music video", "lyrics", "audio", "song", "track",
                    "remix", "cover", "acoustic", "live", "concert", "performance",
                    "album", "single", "ep", "playlist", "radio edit", "extended",
                    "instrumental", "karaoke", "backing track", "mix", "dub",
                    "mv", "clip", "official", "hd", "4k", "visualizer"
                };

                // Enhanced keywords that suggest non-music content
                var nonMusicKeywords = new[]
                {
                    "interview", "podcast", "documentary", "tutorial", "review", "trailer",
                    "movie", "film", "episode", "show", "news", "vlog", "gameplay",
                    "how to", "guide", "explanation", "lecture", "talk", "speech",
                    "reaction", "compilation", "top 10", "best of", "moments",
                    "highlights", "behind the scenes", "making of", "bloopers",
                    "commentary", "analysis", "breakdown", "explainer", "theory"
                };

                // Check for non-music keywords - immediate disqualification
                if (nonMusicKeywords.Any(keyword => title.Contains(keyword) || channelTitle.Contains(keyword)))
                    return false;

                // Check for music keywords - strong indicator
                if (musicKeywords.Any(keyword => title.Contains(keyword) || channelTitle.Contains(keyword)))
                    return true;

                // Enhanced title pattern matching for songs
                if (title.Contains(" - ") && !title.Contains(":") && !title.Contains("|") && !title.Contains("#"))
                {
                    if (!channelTitle.Contains("topic") && !title.Contains("topic"))
                        return true;
                }

                // Enhanced channel name patterns for music content
                var musicChannelPatterns = new[]
                {
                    "vevo", "music", "records", "label", "radio", "band", "artist",
                    "official", "channel", "entertainment", "warner", "universal",
                    "sony", "atlantic", "capitol", "republic", "interscope"
                };

                if (musicChannelPatterns.Any(pattern => channelTitle.Contains(pattern)))
                    return true;

                // Check for common music video patterns in title
                var titlePatterns = new[]
                {
                    @"\(\d{4}\)", // Year in parentheses
                    @"\[official\]",
                    @"\(official\)",
                    @"\[lyrics?\]",
                    @"\(lyrics?\)",
                    @"\[audio\]",
                    @"\(audio\)"
                };

                if (titlePatterns.Any(pattern => System.Text.RegularExpressions.Regex.IsMatch(title, pattern)))
                    return true;

                _logger.LogInformation("Video {VideoId} is not a song based on pattern analysis", videoId);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking if video ID is a song using YoutubeExplode: {VideoId}", videoId);
                return false;
            }
        }
    }
}