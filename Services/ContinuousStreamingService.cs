using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RadioStation.Hubs;
using RadioStation.Models;
using YoutubeExplode;
using YoutubeExplode.Videos.Streams;

namespace RadioStation.Services
{
    /// <summary>
    /// Continuous streaming service using single FFmpeg process
    /// Seamlessly plays songs from queue without interruptions
    /// </summary>
    public class ContinuousStreamingService : IStreamingService, IDisposable
    {
        private readonly ILogger<ContinuousStreamingService> _logger;
        private readonly IConfiguration _config;
        private readonly YoutubeClient _youtube;
        private readonly IHubContext<RadioHub> _hub;

        private readonly string _audioFilesPath;
        private readonly string _icecastUrl;
        private readonly string _icecastPassword;

        private readonly ConcurrentQueue<Song> _queue = new();
        private readonly ConcurrentDictionary<string, string> _downloadedFiles = new();
        private readonly object _preparedSongsLock = new();
        private readonly List<Song> _preparedSongs = new(); // Maintain order of prepared songs
        private readonly SemaphoreSlim _queueSignal = new(0);
        private readonly SemaphoreSlim _ffmpegLock = new(1);

        private CancellationTokenSource? _mainCts;
        private Task? _workerTask;
                private bool _disposed = false;
        private Song? _currentSong;
        private Process? _ffmpegProcess;
        private bool _isStreaming = false;

        // Stream status tracking
        private readonly ConcurrentDictionary<string, SongStatus> _songStatus = new();

        public ContinuousStreamingService(ILogger<ContinuousStreamingService> logger, IConfiguration config, IHubContext<RadioHub> hub)
        {
            _logger = logger;
            _config = config;
            _hub = hub;
            _youtube = new YoutubeClient();

            _audioFilesPath = config["AudioFiles:Path"] ?? Path.Combine(Directory.GetCurrentDirectory(), "AudioFiles");
            var configuredUrl = config["Icecast:Url"] ?? "localhost:8000/stream";
            // Remove protocol prefix if present to avoid malformed URLs
            _icecastUrl = configuredUrl.Replace("http://", "").Replace("https://", "");
            _icecastPassword = config["Icecast:Password"] ?? "hackme";

            Directory.CreateDirectory(_audioFilesPath);

            // Pre-create silent audio file to ensure we always have content
            _ = Task.Run(() => CreateSilentAudioFileAsync(new CancellationToken()));

            _logger.LogInformation("=== ContinuousStreamingService Initialized ===");
            _logger.LogInformation("Audio files path: {Path}", _audioFilesPath);
            _logger.LogInformation("Icecast URL: {Url}", _icecastUrl);

            // Log current state of AudioFiles directory
            LogAudioFilesDirectoryState();

            StartMainLoop();
        }

        private void LogAudioFilesDirectoryState()
        {
            try
            {
                if (Directory.Exists(_audioFilesPath))
                {
                    var files = Directory.GetFiles(_audioFilesPath, "*.mp3");
                    _logger.LogInformation("AudioFiles directory contains {Count} MP3 files:", files.Length);
                    foreach (var file in files)
                    {
                        var fileInfo = new FileInfo(file);
                        _logger.LogInformation("  - {FileName} ({Size} bytes)", fileInfo.Name, fileInfo.Length);
                    }
                }
                else
                {
                    _logger.LogWarning("AudioFiles directory does not exist: {Path}", _audioFilesPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking AudioFiles directory state");
            }
        }

        private async Task<bool> IsIcecastServerRunning(string host, int port)
        {
            try
            {
                using var httpClient = new HttpClient();
                httpClient.Timeout = TimeSpan.FromSeconds(5);

                var response = await httpClient.GetAsync($"http://{host}:{port}/status-json.xsl");
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Cannot reach Icecast server at {Host}:{Port}", host, port);
                return false;
            }
        }

        public void EnqueueSong(Song song)
        {
            _queue.Enqueue(song);
            _songStatus.TryAdd(song.YoutubeVideoId, SongStatus.Queued);
            _queueSignal.Release();
            _logger.LogInformation("Song added to queue: {Title} (Queue size: {Size})", song.Title, _queue.Count);
            _logger.LogInformation("Total downloaded files: {Count}", _downloadedFiles.Count);

            // Notify clients that queue has been updated
            _hub.Clients.All.SendAsync("QueueUpdated", new { song.Title, song.YoutubeVideoId });
        }

        public List<Song> GetQueue() => _queue.ToList();

        public Song GetCurrentlyPlaying() => _currentSong ?? new Song();

        public Task<bool> IsStreamingAsync() => Task.FromResult(_isStreaming);

        public async Task StopAsync()
        {
            _logger.LogInformation("Stopping continuous streaming...");
            _mainCts?.Cancel();

            // Stop FFmpeg process
            await StopFfmpegProcess();

            // Clear queue and status
            _queue.Clear();
            _songStatus.Clear();
            _isStreaming = false;
            _currentSong = null;

            // Notify clients
            await _hub.Clients.All.SendAsync("StreamStopped");
        }

        #region Main Loop

        private void StartMainLoop()
        {
            _logger.LogInformation("=== Starting Continuous Streaming Loop ===");
            _mainCts = new CancellationTokenSource();
            _workerTask = Task.Run(() => MainLoopAsync(_mainCts.Token));
        }

        private async Task MainLoopAsync(CancellationToken token)
        {
            _logger.LogInformation("=== Continuous Streaming Loop Running ===");

            try
            {
                // Process queue continuously
                while (!token.IsCancellationRequested)
                {
                    _logger.LogInformation("--- Waiting for next song in queue...");
                    await _queueSignal.WaitAsync(token);

                    if (_queue.TryDequeue(out var song))
                    {
                        _logger.LogInformation("=== Preparing Song: {Title} ===", song.Title);
                        _currentSong = song;

                        try
                        {
                            await ProcessSongAsync(song, token);

                            // Song is now in prepared queue - pipe writer will pick it up automatically
                            _logger.LogInformation("Song prepared for continuous streaming: {Title}", song.Title);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error processing song {Title}", song.Title);
                            await _hub.Clients.All.SendAsync("SongFailed", new { song.Title, song.YoutubeVideoId });
                        }
                    }

                    // If we have prepared songs and FFmpeg is not running, start it
                    if (!_isStreaming && _downloadedFiles.Count > 0 && !token.IsCancellationRequested)
                    {
                        _logger.LogInformation("Starting FFmpeg for {Count} prepared songs...", _downloadedFiles.Count);
                        await StartContinuousStreamAsync(token);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Continuous streaming loop cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in continuous streaming loop");
            }
            finally
            {
                await StopFfmpegProcess();
            }
        }

        private async Task StartContinuousStreamAsync(CancellationToken token)
        {
            await _ffmpegLock.WaitAsync(token);
            try
            {
                var ffmpegPath = FindFFmpeg();
                if (ffmpegPath == null)
                {
                    _logger.LogError("FFmpeg not found. Cannot start continuous streaming.");
                    return;
                }

                // Start the FFmpeg process
                await StartNewFfmpegProcessAsync(token);

                _logger.LogInformation("=== Continuous FFmpeg Stream Started ===");
                await _hub.Clients.All.SendAsync("StreamStarted");

                // Start background task to monitor FFmpeg and restart if needed
                _ = Task.Run(() => MonitorAndRestartFfmpegAsync(token));
            }
            finally
            {
                _ffmpegLock.Release();
            }
        }

        private async Task StartNewFfmpegProcessAsync(CancellationToken token)
        {
            try
            {
                var ffmpegPath = FindFFmpeg();
                if (ffmpegPath == null) return;

                // Start FFmpeg to read from continuous playlist file
                await StartFfmpegWithContinuousPlaylistAsync(ffmpegPath, token);

                // Start background task to continuously update the playlist
                _ = Task.Run(() => MaintainContinuousPlaylistAsync(token));

                _isStreaming = true;
                _logger.LogInformation("FFmpeg continuous streaming started successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start FFmpeg continuous streaming");
                _isStreaming = false;
            }
        }

        private async Task StartFfmpegWithContinuousPlaylistAsync(string ffmpegPath, CancellationToken token)
        {
            // Parse Icecast URL the same way as original streaming service
            var icecastUri = new Uri($"http://{_icecastUrl}");
            var host = icecastUri.Host;
            var port = icecastUri.Port;
            var mountPoint = icecastUri.AbsolutePath.TrimStart('/');

            if (string.IsNullOrEmpty(mountPoint))
            {
                mountPoint = "live";
            }

            var target = $"{host}:{port}/{mountPoint}";
            var playlistPath = Path.Combine(_audioFilesPath, "continuous_playlist.txt");

            _logger.LogInformation("Starting FFmpeg with smart looping");
            _logger.LogInformation("Icecast Host: {Host}", host);
            _logger.LogInformation("Icecast Port: {Port}", port);
            _logger.LogInformation("Icecast Mount: {Mount}", mountPoint);
            _logger.LogInformation("Icecast Full Target: {Target}", target);

            // Check if Icecast server is reachable
            if (!await IsIcecastServerRunning(host, port))
            {
                _logger.LogWarning("Icecast server may not be running at {Host}:{Port}", host, port);
            }
            else
            {
                _logger.LogInformation("Icecast server is running at {Host}:{Port}", host, port);
            }

            // Create initial playlist file
            await CreateSmartPlaylistFileAsync(playlistPath, token);

            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                StandardErrorEncoding = Encoding.UTF8
            };

            // Build arguments for smart looping
            startInfo.ArgumentList.Add("-re");
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add("concat");
            startInfo.ArgumentList.Add("-safe");
            startInfo.ArgumentList.Add("0");
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(playlistPath);
            startInfo.ArgumentList.Add("-vn"); // No video
            startInfo.ArgumentList.Add("-ignore_unknown"); // Skip unknown streams
            startInfo.ArgumentList.Add("-c:a");
            startInfo.ArgumentList.Add("libmp3lame");
            startInfo.ArgumentList.Add("-b:a");
            startInfo.ArgumentList.Add("128k");
            startInfo.ArgumentList.Add("-ar");
            startInfo.ArgumentList.Add("44100"); // Standard sample rate
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add("mp3");
            startInfo.ArgumentList.Add("-content_type");
            startInfo.ArgumentList.Add("audio/mpeg");
            startInfo.ArgumentList.Add("-ice_name");
            startInfo.ArgumentList.Add("RadioHub Continuous Stream");
            startInfo.ArgumentList.Add("-ice_genre");
            startInfo.ArgumentList.Add("Music");
            startInfo.ArgumentList.Add($"icecast://source:{_icecastPassword}@{target}");

            _ffmpegProcess = new Process { StartInfo = startInfo };

            _ffmpegProcess.ErrorDataReceived += (s, e) => {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    _logger.LogInformation("FFmpeg: {Data}", e.Data);
                }
            };

            _ffmpegProcess.Exited += (s, e) => {
                _logger.LogWarning("FFmpeg process ended - restarting with updated playlist");
                _isStreaming = false;
                // Auto-restart with new songs
                _ = Task.Run(async () => {
                    await Task.Delay(2000, token);
                    await StartNewFfmpegProcessAsync(token);
                });
            };

            _ffmpegProcess.Start();
            _ffmpegProcess.BeginErrorReadLine();
        }

        private async Task CreateSmartPlaylistFileAsync(string playlistPath, CancellationToken token)
        {
            try
            {
                var playlistContent = new StringBuilder();

                // Always ensure we have content
                bool hasSongs = false;

                // Add prepared songs to playlist
                lock (_preparedSongsLock)
                {
                    var preparedSongsCopy = _preparedSongs.ToList();
                    foreach (var song in preparedSongsCopy)
                    {
                        if (_downloadedFiles.TryGetValue(song.YoutubeVideoId, out var filePath) && File.Exists(filePath))
                        {
                            var absolutePath = Path.GetFullPath(filePath);
                            playlistContent.AppendLine($"file '{absolutePath}'");
                            hasSongs = true;
                        }
                    }
                }

                // ALWAYS add silent audio file as backup
                var silentFilePath = await CreateSilentAudioFileAsync(token);
                if (silentFilePath != null)
                {
                    playlistContent.AppendLine($"file '{silentFilePath}'");
                }

                // Use a timestamp to avoid file conflicts
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var newPlaylistPath = Path.Combine(Path.GetDirectoryName(playlistPath)!, $"playlist_{timestamp}.txt");

                await File.WriteAllTextAsync(newPlaylistPath, playlistContent.ToString(), token);

                // Only replace if FFmpeg is not currently reading it
                try
                {
                    if (File.Exists(playlistPath))
                    {
                        File.Delete(playlistPath);
                    }
                    File.Move(newPlaylistPath, playlistPath);
                }
                catch (IOException)
                {
                    // If file is locked, use the new file directly
                    _logger.LogWarning("Playlist file locked, using new file: {NewPath}", newPlaylistPath);
                    // FFmpeg will pick up the new file when it finishes current songs
                }

                _logger.LogInformation("Created smart playlist: {HasSongs} songs + silent backup", hasSongs);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating smart playlist file");
            }
        }

        private async Task MaintainContinuousPlaylistAsync(CancellationToken token)
        {
            _logger.LogInformation("Smart playlist maintenance task - monitoring FFmpeg health");
            var playlistPath = Path.Combine(_audioFilesPath, "continuous_playlist.txt");
            var lastSongCount = 0;

            while (!token.IsCancellationRequested && !_disposed)
            {
                try
                {
                    // Check if FFmpeg process is still running
                    if (_ffmpegProcess != null && _ffmpegProcess.HasExited)
                    {
                        _logger.LogWarning("FFmpeg process ended - restarting with new songs");
                        _isStreaming = false;
                        await Task.Delay(2000, token);
                        await StartNewFfmpegProcessAsync(token);
                        await Task.Delay(3000, token); // Wait before checking again
                        continue;
                    }

                    // Update playlist when songs change
                    int currentSongCount;
                    lock (_preparedSongsLock)
                    {
                        currentSongCount = _preparedSongs.Count;
                    }

                    bool shouldUpdate = (currentSongCount != lastSongCount);

                    if (shouldUpdate)
                    {
                        await CreateSmartPlaylistFileAsync(playlistPath, token);
                        lastSongCount = currentSongCount;
                        _logger.LogInformation("Updated playlist with {Count} songs", currentSongCount);
                    }

                    await Task.Delay(2000, token); // Check every 2 seconds
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in smart playlist maintenance task");
                    await Task.Delay(2000, token);
                }
            }
        }

        
        private async Task<string?> CreateSilentAudioFileAsync(CancellationToken token)
        {
            try
            {
                var silentFilePath = Path.Combine(_audioFilesPath, "silence.mp3");

                if (!File.Exists(silentFilePath))
                {
                    var ffmpegPath = FindFFmpeg();
                    if (ffmpegPath == null) return null;

                    _logger.LogInformation("Creating silent audio file: {FilePath}", silentFilePath);

                    var startInfo = new ProcessStartInfo
                    {
                        FileName = ffmpegPath,
                        Arguments = "-f lavfi -i anullsrc=channel_layout=mono:sample_rate=44100 -t 30 -q:a 9 -y \"" + silentFilePath + "\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true
                    };

                    using var process = new Process { StartInfo = startInfo };
                    process.Start();
                    await process.WaitForExitAsync(token);

                    if (process.ExitCode == 0 && File.Exists(silentFilePath))
                    {
                        _logger.LogInformation("Silent audio file created successfully");
                        return silentFilePath;
                    }
                }
                else if (File.Exists(silentFilePath))
                {
                    return silentFilePath;
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating silent audio file");
                return null;
            }
        }

        private async Task MonitorAndRestartFfmpegAsync(CancellationToken token)
        {
            if (_ffmpegProcess == null) return;

            try
            {
                // Monitor FFmpeg process
                await _ffmpegProcess.WaitForExitAsync(token);
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation is requested
                return;
            }

            if (_disposed || token.IsCancellationRequested)
                return;

            _logger.LogWarning("FFmpeg process ended unexpectedly");
            _isStreaming = false;

            // Check if there are songs to play and restart if needed
            if (_queue.TryPeek(out var nextSong) && !token.IsCancellationRequested)
            {
                _logger.LogInformation("Restarting FFmpeg for next songs...");
                await Task.Delay(1000, token); // Brief delay
                await StartNewFfmpegProcessAsync(token);
            }
        }

        private async Task CreatePlaylistFileAsync(string playlistPath, CancellationToken token)
        {
            var playlistContent = new StringBuilder();
            var validSongs = 0;

            // Add currently prepared songs to playlist in order
            lock (_preparedSongsLock)
            {
                var preparedSongsCopy = _preparedSongs.ToList();
                foreach (var song in preparedSongsCopy)
                {
                    if (_downloadedFiles.TryGetValue(song.YoutubeVideoId, out var filePath) && File.Exists(filePath))
                    {
                        // Convert to absolute path to avoid any path confusion
                        var absolutePath = Path.GetFullPath(filePath);
                        playlistContent.AppendLine($"file '{absolutePath}'");
                        validSongs++;
                        _logger.LogInformation("Added to playlist: {Title} ({Path})", song.Title, absolutePath);
                    }
                    else
                    {
                        _logger.LogWarning("Prepared song file not found: {Title}", song.Title);
                        // Remove from prepared songs list if file doesn't exist
                        _preparedSongs.Remove(song);
                    }
                }
            }

            // Ensure playlist is not empty
            if (validSongs == 0)
            {
                _logger.LogWarning("No prepared songs available for playlist");

                // Create a temporary silent file or skip starting
                // For now, create an empty playlist which will cause FFmpeg to exit
                await File.WriteAllTextAsync(playlistPath, "", token);
                return;
            }

            await File.WriteAllTextAsync(playlistPath, playlistContent.ToString(), token);
            _logger.LogInformation("Created playlist with {Count} prepared songs", validSongs);
        }

        private async Task ProcessSongAsync(Song song, CancellationToken token)
        {
            try
            {
                // Update status to preparing
                _songStatus.AddOrUpdate(song.YoutubeVideoId, SongStatus.Preparing, (k, v) => SongStatus.Preparing);
                await _hub.Clients.All.SendAsync("SongPreparing", new { song.Title, song.YoutubeVideoId });

                _logger.LogInformation("Preparing song: {Title}", song.Title);

                // Check if already downloaded
                _logger.LogInformation("Getting/Downloading song: {Title} (VideoId: {VideoId})", song.Title, song.YoutubeVideoId);
                var filePath = await GetOrDownloadSongAsync(song, token);
                if (filePath == null)
                {
                    _logger.LogError("Failed to prepare song: {Title}", song.Title);
                    _songStatus.AddOrUpdate(song.YoutubeVideoId, SongStatus.Failed, (k, v) => SongStatus.Failed);
                    return;
                }

                // Verify the file actually exists and has content
                if (!File.Exists(filePath))
                {
                    _logger.LogError("Downloaded file not found: {FilePath}", filePath);
                    _songStatus.AddOrUpdate(song.YoutubeVideoId, SongStatus.Failed, (k, v) => SongStatus.Failed);
                    return;
                }

                var fileInfo = new FileInfo(filePath);
                _logger.LogInformation("Song file ready: {Title} - Size: {Size} bytes - Path: {FilePath}",
                    song.Title, fileInfo.Length, filePath);

                // Add to prepared songs list
                lock (_preparedSongsLock)
                {
                    if (!_preparedSongs.Any(s => s.YoutubeVideoId == song.YoutubeVideoId))
                    {
                        _preparedSongs.Add(song);
                        _logger.LogInformation("Added song to prepared queue: {Title} (Queue size: {Size})", song.Title, _preparedSongs.Count);
                    }
                }

                // Update status to ready
                _songStatus.AddOrUpdate(song.YoutubeVideoId, SongStatus.Ready, (k, v) => SongStatus.Ready);
                await _hub.Clients.All.SendAsync("SongReady", new { song.Title, song.YoutubeVideoId });

                _logger.LogInformation("Song ready: {Title}", song.Title);

                // Notify that song started playing (will play when FFmpeg reads from playlist)
                await _hub.Clients.All.SendAsync("SongStarted", new
                {
                    song.Title,
                    song.YoutubeVideoId,
                    queueSize = _queue.Count,
                    preparedCount = _preparedSongs.Count
                });

                // Trigger prefetch for next songs
                _ = Task.Run(() => PrefetchNextSongsAsync(token));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing song {Title}", song.Title);
                _songStatus.AddOrUpdate(song.YoutubeVideoId, SongStatus.Failed, (k, v) => SongStatus.Failed);
                await _hub.Clients.All.SendAsync("SongFailed", new { song.Title, song.YoutubeVideoId });
            }
        }

        #endregion

        #region Song Management

        private async Task<string?> GetOrDownloadSongAsync(Song song, CancellationToken token)
        {
            var videoId = song.YoutubeVideoId;

            // Check if already downloaded
            if (_downloadedFiles.TryGetValue(videoId, out var existingPath) && File.Exists(existingPath))
            {
                _logger.LogInformation("Song already downloaded: {Title}", song.Title);
                return existingPath;
            }

            // Download the song
            _logger.LogInformation("Downloading song: {Title}", song.Title);
            var filePath = await DownloadAudioAsync(videoId, song.Title, token);

            if (filePath != null)
            {
                _downloadedFiles.TryAdd(videoId, filePath);
                _logger.LogInformation("Song downloaded successfully: {Title}", song.Title);
            }

            return filePath;
        }

        private async Task<string?> DownloadAudioAsync(string videoId, string title, CancellationToken token)
        {
            try
            {
                var streamManifest = await _youtube.Videos.Streams.GetManifestAsync(videoId, token);
                var audioStreamInfo = streamManifest.GetAudioOnlyStreams().GetWithHighestBitrate();

                if (audioStreamInfo == null)
                {
                    _logger.LogError("No audio stream found for {VideoId}", videoId);
                    return null;
                }

                var safeTitle = SanitizeFileName(title);
                var fileName = $"{videoId}_{safeTitle}.mp3";
                var filePath = Path.Combine(_audioFilesPath, fileName);

                if (File.Exists(filePath))
                {
                    var existingSize = new FileInfo(filePath).Length;
                    if (existingSize > 1024)
                    {
                        return filePath;
                    }
                    File.Delete(filePath);
                }

                await _youtube.Videos.Streams.DownloadAsync(audioStreamInfo, filePath);

                var downloadedSize = new FileInfo(filePath).Length;
                if (downloadedSize < 1024)
                {
                    _logger.LogError("Downloaded file too small: {Size} bytes", downloadedSize);
                    return null;
                }

                return filePath;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Download failed for {VideoId}", videoId);
                return null;
            }
        }

        
        
        private async Task PrefetchNextSongsAsync(CancellationToken token)
        {
            try
            {
                var nextSongs = _queue.Take(3).ToArray(); // Prefetch next 3 songs
                foreach (var song in nextSongs)
                {
                    if (_songStatus.ContainsKey(song.YoutubeVideoId))
                        continue;

                    _logger.LogInformation("Prefetching song: {Title}", song.Title);
                    await GetOrDownloadSongAsync(song, token);

                    // Small delay to avoid overwhelming the API
                    await Task.Delay(1000, token);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error prefetching songs");
            }
        }

        #endregion

        #region FFmpeg Management

        private async Task MonitorFfmpegProcessAsync(CancellationToken token)
        {
            if (_ffmpegProcess == null) return;

            try
            {
                await _ffmpegProcess.WaitForExitAsync(token);
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation is requested
            }

            if (!_disposed)
            {
                _logger.LogWarning("FFmpeg process ended unexpectedly");
                _isStreaming = false;
                await _hub.Clients.All.SendAsync("StreamStopped");
            }
        }

        private async Task StopFfmpegProcess()
        {
            if (_ffmpegProcess != null && !_ffmpegProcess.HasExited)
            {
                try
                {
                    _logger.LogInformation("Stopping FFmpeg process...");
                    _ffmpegProcess.Kill();
                    await Task.Run(() => _ffmpegProcess.WaitForExit());
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error stopping FFmpeg process");
                }
                finally
                {
                    _ffmpegProcess = null;
                    _isStreaming = false;
                }
            }
        }

        private string? FindFFmpeg()
        {
            try
            {
                var command = OperatingSystem.IsWindows() ? "where" : "which";
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo(command, "ffmpeg")
                    {
                        UseShellExecute = false,
                        RedirectStandardOutput = true
                    }
                };

                process.Start();
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                if (process.ExitCode == 0 && !string.IsNullOrEmpty(output))
                {
                    var path = output.Split('\n')[0].Trim();
                    _logger.LogInformation("Found FFmpeg at: {Path}", path);
                    return path;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "FFmpeg not found in PATH");
            }

            // Try common locations
            var commonPaths = OperatingSystem.IsWindows()
                ? new[] { @"C:\ffmpeg\bin\ffmpeg.exe", @"C:\Program Files\ffmpeg\bin\ffmpeg.exe" }
                : new[] { "/usr/bin/ffmpeg", "/usr/local/bin/ffmpeg" };

            foreach (var path in commonPaths)
            {
                if (File.Exists(path))
                {
                    _logger.LogInformation("Found FFmpeg at: {Path}", path);
                    return path;
                }
            }

            return null;
        }

        
        private string SanitizeFileName(string fileName)
        {
            // Remove invalid file system characters but keep Unicode letters
            var invalidChars = new string(Path.GetInvalidFileNameChars());
            var pattern = $"[{Regex.Escape(invalidChars)}]";
            var sanitized = Regex.Replace(fileName, pattern, "");

            // Replace multiple spaces with single space and trim
            sanitized = Regex.Replace(sanitized, @"\s+", " ").Trim();

            // Ensure filename is not empty
            if (string.IsNullOrWhiteSpace(sanitized))
            {
                sanitized = "Untitled";
            }

            return sanitized;
        }

        #endregion

        #region Public Interface

        public SongStatus GetSongStatus(string videoId)
        {
            return _songStatus.TryGetValue(videoId, out var status) ? status : SongStatus.Unknown;
        }

        public Dictionary<string, SongStatus> GetAllSongStatus()
        {
            return _songStatus.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }

        #endregion

        #region Cleanup

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _logger.LogInformation("=== Disposing ContinuousStreamingService ===");

            _mainCts?.Cancel();
            _mainCts?.Dispose();

            try
            {
                _workerTask?.Wait(TimeSpan.FromSeconds(10));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error waiting for tasks to complete");
            }

            StopFfmpegProcess().GetAwaiter().GetResult();

            _queueSignal?.Dispose();
            _ffmpegLock?.Dispose();
            _queue.Clear();
            _songStatus.Clear();
            _downloadedFiles.Clear();

            _logger.LogInformation("=== ContinuousStreamingService Disposed ===");
        }

        #endregion
    }

    public enum SongStatus
    {
        Unknown,
        Queued,
        Preparing,
        Ready,
        Playing,
        Completed,
        Failed
    }
}