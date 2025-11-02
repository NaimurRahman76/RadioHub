using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
    public class StreamingService : IStreamingService, IDisposable
    {
        private readonly ILogger<StreamingService> _logger;
        private readonly IConfiguration _config;
        private readonly YoutubeClient _youtube;
        private readonly IHubContext<RadioHub> _hub;

        private readonly string _audioFilesPath;
        private readonly string _icecastUrl;
        private readonly string _icecastPassword;

        private readonly ConcurrentQueue<Song> _queue = new();
        private readonly ConcurrentDictionary<string, string> _prefetchedFiles = new();
        private readonly SemaphoreSlim _queueSignal = new(0);
        private readonly SemaphoreSlim _prefetchLock = new(1);

        private CancellationTokenSource? _mainCts;
        private Task? _workerTask;
        private Task? _prefetchTask;
        private bool _disposed = false;
        private Song? _currentSong;

        public StreamingService(ILogger<StreamingService> logger, IConfiguration config, IHubContext<RadioHub> hub)
        {
            _logger = logger;
            _config = config;
            _hub = hub;
            _youtube = new YoutubeClient();

            _audioFilesPath = config["AudioFiles:Path"] ?? Path.Combine(Directory.GetCurrentDirectory(), "AudioFiles");
            _icecastUrl = config["Icecast:Url"] ?? "http://localhost:8000/stream";
            _icecastPassword = config["Icecast:Password"] ?? "hackme";

            Directory.CreateDirectory(_audioFilesPath);

            _logger.LogInformation("=== StreamingService Initialized ===");
            _logger.LogInformation("Audio files path: {Path}", _audioFilesPath);
            _logger.LogInformation("Icecast URL: {Url}", _icecastUrl);

            StartMainLoop();
        }

        #region Utility Methods

        private void StartMainLoop()
        {
            _logger.LogInformation("=== Starting Main Loop ===");
            _mainCts = new CancellationTokenSource();
            _workerTask = Task.Run(() => MainLoopAsync(_mainCts.Token));
        }

        private async Task MainLoopAsync(CancellationToken token)
        {
            _logger.LogInformation("=== Main Loop Running ===");

            while (!token.IsCancellationRequested)
            {
                try
                {
                    _logger.LogInformation("--- Waiting for next song in queue...");
                    await _queueSignal.WaitAsync(token);

                    if (_queue.TryDequeue(out var song))
                    {
                        _logger.LogInformation("=== Playing Next: {Title} ===", song.Title);
                        _currentSong = song;

                        try
                        {
                            await PlaySongAsync(song, token);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error playing song {Title}", song.Title);
                            await _hub.Clients.All.SendAsync("SongFailed", new { song.Title, song.YoutubeVideoId });
                        }
                        finally
                        {
                            _currentSong = null;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Main loop cancelled");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unexpected error in main loop");
                }
            }

            _logger.LogInformation("=== Main Loop Ended ===");
        }

        private async Task PlaySongAsync(Song song, CancellationToken token)
        {
            _logger.LogInformation("====================================");
            _logger.LogInformation("NOW PLAYING: {Title}", song.Title);
            _logger.LogInformation("====================================");

            // Step 1: Get file (from prefetch or download)
            string? filePath = null;

            await _prefetchLock.WaitAsync(token);
            try
            {
                // Check if already prefetched
                if (_prefetchedFiles.TryRemove(song.YoutubeVideoId, out var prefetchedPath))
                {
                    if (File.Exists(prefetchedPath))
                    {
                        _logger.LogInformation("✓ Using prefetched file: {Path}", prefetchedPath);
                        filePath = prefetchedPath;
                    }
                    else
                    {
                        _logger.LogWarning("Prefetched file missing, will re-download");
                    }
                }
            }
            finally
            {
                _prefetchLock.Release();
            }

            // Download if not prefetched
            if (filePath == null)
            {
                _logger.LogInformation("Downloading audio...");
                filePath = await DownloadAudioAsync(song.YoutubeVideoId, song.Title);
            }

            if (filePath == null || !File.Exists(filePath))
            {
                _logger.LogError("Failed to get audio file for {Title}", song.Title);
                return;
            }

            var fileInfo = new FileInfo(filePath);
            _logger.LogInformation("File ready: {Size} bytes", fileInfo.Length);

            // Notify clients
            await _hub.Clients.All.SendAsync("SongStarted", new
            {
                song.Title,
                song.YoutubeVideoId,
                queueSize = _queue.Count
            });

            // Trigger prefetch for next songs while this one plays
            _ = Task.Run(() => PrefetchNextSongsAsync());

            // Step 2: Stream to Icecast
            var success = await StreamToIcecastAsync(filePath, song, token);

            if (success)
            {
                _logger.LogInformation("✓ Successfully played: {Title}", song.Title);
            }
            else
            {
                _logger.LogError("✗ Failed to play: {Title}", song.Title);
            }

            // Step 3: Cleanup - delete file after playing
            await Task.Delay(1000, token); // Wait a moment to ensure FFmpeg released the file
            TryDeleteFile(filePath);

            // Notify clients
            await _hub.Clients.All.SendAsync("SongEnded", new
            {
                song.Title,
                song.YoutubeVideoId,
                queueSize = _queue.Count,
                nextSong = _queue.TryPeek(out var next) ? next.Title : null
            });

            _logger.LogInformation("====================================");
        }

        private async Task<bool> StreamToIcecastAsync(string filePath, Song song, CancellationToken token)
        {
            var ffmpegPath = FindFFmpeg();
            if (ffmpegPath == null)
            {
                _logger.LogError("FFmpeg not found");
                return false;
            }

            // Parse Icecast URL
            var icecastUri = new Uri(_icecastUrl);
            var host = icecastUri.Host;
            var port = icecastUri.Port;
            var mountPoint = icecastUri.AbsolutePath.TrimStart('/');

            if (string.IsNullOrEmpty(mountPoint))
            {
                mountPoint = "live";
            }

            var target = $"{host}:{port}/{mountPoint}";
            var safeTitle = SanitizeMetadata(song.Title);

            // Create FFmpeg process
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    StandardErrorEncoding = Encoding.UTF8
                },
                EnableRaisingEvents = true
            };

            // Build arguments
            process.StartInfo.ArgumentList.Add("-re");
            process.StartInfo.ArgumentList.Add("-i");
            process.StartInfo.ArgumentList.Add(filePath);
            process.StartInfo.ArgumentList.Add("-c:a");
            process.StartInfo.ArgumentList.Add("libmp3lame");
            process.StartInfo.ArgumentList.Add("-b:a");
            process.StartInfo.ArgumentList.Add("128k");
            process.StartInfo.ArgumentList.Add("-f");
            process.StartInfo.ArgumentList.Add("mp3");
            process.StartInfo.ArgumentList.Add("-content_type");
            process.StartInfo.ArgumentList.Add("audio/mpeg");
            process.StartInfo.ArgumentList.Add("-ice_name");
            process.StartInfo.ArgumentList.Add(safeTitle);
            process.StartInfo.ArgumentList.Add("-ice_genre");
            process.StartInfo.ArgumentList.Add("Music");
            process.StartInfo.ArgumentList.Add($"icecast://source:{_icecastPassword}@{target}");

            var errorOutput = new List<string>();
            process.ErrorDataReceived += (s, e) => {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    errorOutput.Add(e.Data);
                    if (e.Data.Contains("time=") || e.Data.Contains("size="))
                    {
                        _logger.LogDebug("FFmpeg: {Line}", e.Data);
                    }
                }
            };

            try
            {
                process.Start();
                process.BeginErrorReadLine();

                _logger.LogInformation("FFmpeg started (PID: {Pid})", process.Id);

                // Check for immediate failure
                await Task.Delay(1000, token);
                if (process.HasExited)
                {
                    _logger.LogError("FFmpeg exited immediately (code: {Code})", process.ExitCode);
                    foreach (var line in errorOutput.TakeLast(5))
                    {
                        _logger.LogError("  {Line}", line);
                    }
                    return false;
                }

                await _hub.Clients.All.SendAsync("SongStarted", new
                {
                    song.Title,
                    song.YoutubeVideoId,
                    queueSize = _queue.Count,
                    nextSong = _queue.TryPeek(out var next) ? next.Title : null
                });
                // Wait for completion
                await process.WaitForExitAsync(token);

                if (process.ExitCode == 0)
                {
                    return true;
                }
                else
                {
                    _logger.LogWarning("FFmpeg exit code: {Code}", process.ExitCode);
                    return false;
                }
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch { }
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FFmpeg execution error");
                return false;
            }
            finally
            {
                process.Dispose();
            }
        }

        private async Task PrefetchNextSongsAsync()
        {
            if (_prefetchTask != null && !_prefetchTask.IsCompleted)
            {
                return; // Already prefetching
            }

            _prefetchTask = Task.Run(async () =>
            {
                try
                {
                    // Get next 2 songs from queue (without removing them)
                    var songsToFetch = _queue.Take(2).ToList();

                    if (songsToFetch.Count == 0)
                    {
                        _logger.LogDebug("No songs to prefetch");
                        return;
                    }

                    _logger.LogInformation(">>> Prefetching {Count} songs...", songsToFetch.Count);

                    foreach (var song in songsToFetch)
                    {
                        await _prefetchLock.WaitAsync();
                        try
                        {
                            // Skip if already prefetched
                            if (_prefetchedFiles.ContainsKey(song.YoutubeVideoId))
                            {
                                _logger.LogDebug("Already prefetched: {Title}", song.Title);
                                continue;
                            }

                            // Check if file already exists on disk
                            var safeTitle = SanitizeFileName(song.Title);
                            var fileName = $"{song.YoutubeVideoId}_{safeTitle}.mp3";
                            var filePath = Path.Combine(_audioFilesPath, fileName);

                            if (File.Exists(filePath))
                            {
                                var size = new FileInfo(filePath).Length;
                                if (size > 1024)
                                {
                                    _logger.LogInformation("✓ Prefetch: {Title} (already on disk)", song.Title);
                                    _prefetchedFiles[song.YoutubeVideoId] = filePath;
                                    continue;
                                }
                            }

                            // Download
                            _logger.LogInformation("⬇ Prefetching: {Title}...", song.Title);
                            var downloadedPath = await DownloadAudioAsync(song.YoutubeVideoId, song.Title);

                            if (downloadedPath != null)
                            {
                                _prefetchedFiles[song.YoutubeVideoId] = downloadedPath;
                                _logger.LogInformation("✓ Prefetch complete: {Title}", song.Title);
                            }
                            else
                            {
                                _logger.LogWarning("✗ Prefetch failed: {Title}", song.Title);
                            }
                        }
                        finally
                        {
                            _prefetchLock.Release();
                        }
                    }

                    _logger.LogInformation(">>> Prefetch complete. Cached: {Count} songs", _prefetchedFiles.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Prefetch error");
                }
            });

            await _prefetchTask;
        }

        private string SanitizeMetadata(string input)
        {
            if (string.IsNullOrEmpty(input))
                return "Unknown";

            var sanitized = Regex.Replace(input, @"[^\u0020-\u007E\u0980-\u09FF]+", " ");
            sanitized = sanitized
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("'", "\\'")
                .Replace(";", "\\;")
                .Replace("=", "\\=")
                .Trim();

            return string.IsNullOrWhiteSpace(sanitized) ? "Unknown" : sanitized;
        }

        private string SanitizeFileName(string fileName)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sanitized = string.Join("_", fileName.Split(invalid, StringSplitOptions.RemoveEmptyEntries));

            if (sanitized.Length > 100)
                sanitized = sanitized.Substring(0, 100);

            return sanitized;
        }

        private void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    _logger.LogInformation("🗑 Deleted: {Path}", Path.GetFileName(path));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete file {Path}", path);
            }
        }

        private async Task<string?> DownloadAudioAsync(string videoId, string title)
        {
            try
            {
                var streamManifest = await _youtube.Videos.Streams.GetManifestAsync(videoId);
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

        private string? FindFFmpeg()
        {
            try
            {
                var command = OperatingSystem.IsWindows() ? "where" : "which";
                var p = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = command,
                        Arguments = "ffmpeg",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    }
                };

                p.Start();
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit();

                if (p.ExitCode == 0 && !string.IsNullOrEmpty(output))
                {
                    var path = output.Split(new[] { Environment.NewLine, "\n" }, StringSplitOptions.RemoveEmptyEntries)[0].Trim();
                    return path;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error finding FFmpeg");
            }

            return null;
        }

        #endregion

        #region Methods 

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _logger.LogInformation("Disposing StreamingService...");

            try { _mainCts?.Cancel(); } catch { }

            _queueSignal.Dispose();
            _prefetchLock.Dispose();

            _logger.LogInformation("StreamingService disposed");
        }

        public Task<bool> IsStreamingAsync() => Task.FromResult(_currentSong != null);

        public void EnqueueSong(Song song)
        {
            _logger.LogInformation(">>> Enqueuing song: {Title} ({VideoId})", song.Title, song.YoutubeVideoId);

            _queue.Enqueue(song);
            _logger.LogInformation("Queue size: {Count}", _queue.Count);

            _queueSignal.Release();

            // Notify clients about queue update
            _ = _hub.Clients.All.SendAsync("PlaylistUpdated", new
            {
                queueSize = _queue.Count,
                currentSong = _currentSong?.Title
            });

            // Trigger prefetch for next songs
            _ = Task.Run(() => PrefetchNextSongsAsync());
        }

        public async Task StopAsync()
        {
            _logger.LogInformation("Stopping streaming service...");
            _mainCts?.Cancel();
            if (_workerTask != null)
            {
                try { await _workerTask; } catch { }
            }
        }

        public List<Song> GetQueue()
        {
            return _queue.ToList();
        }

        public Song GetCurrentlyPlaying()
        {
            return _currentSong;
                
        }

        #endregion
    }
}