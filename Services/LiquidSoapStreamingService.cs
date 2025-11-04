using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
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
    /// Robust LiquidSoap-based streaming service for continuous radio station.
    /// Features: Default playlist + user requests + FFmpeg conversion + never-ending stream
    /// </summary>
    public class LiquidSoapStreamingService : IStreamingService, IDisposable
    {
        private readonly ILogger<LiquidSoapStreamingService> _logger;
        private readonly IConfiguration _config;
        private readonly YoutubeClient _youtube;
        private readonly IHubContext<RadioHub> _hub;

        private readonly string _audioFilesPath;
        private readonly string _liquidSoapPath;
        private readonly string _icecastUrl;
        private readonly string _icecastPassword;

        // Robust queue system
        private readonly ConcurrentQueue<Song> _userRequests = new();
        private readonly ConcurrentQueue<Song> _defaultPlaylist = new();
        private readonly ConcurrentDictionary<string, string> _downloadedFiles = new();
        private readonly ConcurrentDictionary<string, SongStatus> _songStatus = new();
        private readonly SemaphoreSlim _queueSignal = new(0);
        private readonly object _currentlyPlayingLock = new();

        // Enhanced state management
        private Song? _currentSong;
        private Song? _nextSong;
        private CancellationTokenSource? _mainCts;
        private Task? _workerTask;
        private Process? _liquidSoapProcess;
        private bool _isStreaming = false;
        private bool _disposed = false;

        // Playlist files
        private readonly string _liquidSoapConfigPath;
        private readonly string _mainPlaylistPath;
        private readonly string _defaultPlaylistPath;

        // Stream monitoring
        private Task? _monitorTask;
        private Task? _playlistManagerTask;
        private DateTime _lastPlaylistUpdate = DateTime.MinValue;
        private readonly TimeSpan _minPlaylistUpdateInterval = TimeSpan.FromSeconds(5);

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

        public LiquidSoapStreamingService(ILogger<LiquidSoapStreamingService> logger, IConfiguration config, IHubContext<RadioHub> hub)
        {
            _logger = logger;
            _config = config;
            _hub = hub;
            _youtube = new YoutubeClient();

            _audioFilesPath = config["AudioFiles:Path"] ?? Path.Combine(Directory.GetCurrentDirectory(), "AudioFiles");
            _liquidSoapPath = config["LiquidSoap:Path"] ?? "C:\\liquidsoap-2.4.0-win64\\liquidsoap.exe";

            var icecastUrl = config["Icecast:Url"] ?? "http://localhost:8000/stream";
            if (!icecastUrl.StartsWith("http://") && !icecastUrl.StartsWith("https://"))
                icecastUrl = $"http://{icecastUrl}";

            var uri = new Uri(icecastUrl);
            _icecastUrl = $"icecast://source:{config["Icecast:Password"] ?? "hackme"}@{uri.Host}:{uri.Port}{uri.AbsolutePath}";
            _icecastPassword = config["Icecast:Password"] ?? "hackme";

            _liquidSoapConfigPath = Path.Combine(_audioFilesPath, "radio.liq");
            _mainPlaylistPath = Path.Combine(_audioFilesPath, "main_playlist.txt");
            _defaultPlaylistPath = Path.Combine(_audioFilesPath, "default_playlist.txt");

            Directory.CreateDirectory(_audioFilesPath);

            // Initialize default playlist
            InitializeDefaultPlaylist();

            _logger.LogInformation("=== LiquidSoap Streaming Service Initialized ===");
            _logger.LogInformation("Audio path: {Path}", _audioFilesPath);
            _logger.LogInformation("LiquidSoap path: {Path}", _liquidSoapPath);
            _logger.LogInformation("Icecast URL: {Url}", _icecastUrl);

            InitializeLiquidSoapConfig();
            StartMainLoop();
        }

        private void InitializeDefaultPlaylist()
        {
            try
            {
                _logger.LogInformation("🎵 Initializing default playlist from AudioFiles folder...");

                // First, load existing MP3 files from AudioFiles folder
                LoadExistingSongsIntoDefaultPlaylist();

                // Then add any online songs if needed
                if (_defaultPlaylist.Count == 0)
                {
                    _logger.LogInformation("No existing songs found, adding sample songs...");
                    AddOnlineDefaultSongs();
                }

                _logger.LogInformation("✅ Default playlist initialized with {Count} songs", _defaultPlaylist.Count);

                // Save default playlist to file for persistence
                SaveDefaultPlaylistToFile();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error initializing default playlist");
            }
        }

        private void LoadExistingSongsIntoDefaultPlaylist()
        {
            try
            {
                var mp3Files = Directory.GetFiles(_audioFilesPath, "*.mp3", SearchOption.TopDirectoryOnly);

                foreach (var filePath in mp3Files)
                {
                    var fileName = Path.GetFileNameWithoutExtension(filePath);

                    // Skip the silence file
                    if (fileName.Equals("silence", StringComparison.OrdinalIgnoreCase) ||
                        fileName.Contains("test_"))
                    {
                        continue;
                    }

                    try
                    {
                        // Extract video ID from filename if it follows YouTube naming convention
                        var videoId = ExtractVideoIdFromFileName(fileName);

                        var song = new Song
                        {
                            YoutubeVideoId = videoId ?? Guid.NewGuid().ToString(), // Generate ID if not found
                            Title = CleanSongTitle(fileName),
                            FilePath = filePath // Store the actual file path
                        };

                        _defaultPlaylist.Enqueue(song);
                        _downloadedFiles[song.YoutubeVideoId] = filePath; // Mark as already downloaded

                        _logger.LogInformation("📻 Added to default playlist: {Title}", song.Title);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to process song file: {FileName}", fileName);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading existing songs");
            }
        }

        private string? ExtractVideoIdFromFileName(string fileName)
        {
            // Extract YouTube video ID from filename (assumes format: ID_Title.mp3)
            var parts = fileName.Split('_');
            if (parts.Length > 0)
            {
                var potentialId = parts[0];
                // Check if it looks like a YouTube video ID (11 characters)
                if (potentialId.Length == 11 && IsValidYouTubeId(potentialId))
                {
                    return potentialId;
                }
            }
            return null;
        }

        private bool IsValidYouTubeId(string id)
        {
            // Basic validation for YouTube video ID format
            return id.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_');
        }

        private string CleanSongTitle(string fileName)
        {
            try
            {
                // Remove video ID from the beginning if present
                var parts = fileName.Split('_', 2);
                if (parts.Length > 1)
                {
                    return parts[1]; // Return the part after the video ID
                }

                // If no video ID found, return the cleaned filename
                return fileName.Replace("_", " ").Trim();
            }
            catch
            {
                return fileName;
            }
        }

        private void AddOnlineDefaultSongs()
        {
            var onlineSongs = new[]
            {
                "https://www.youtube.com/watch?v=0dGS7dNwlrU", // Ki Jala - Hridoy Khan
                "https://www.youtube.com/watch?v=1f18irP--O8", // Tomake Chai
            };

            foreach (var songUrl in onlineSongs)
            {
                try
                {
                    var videoId = YoutubeExplode.Videos.VideoId.TryParse(songUrl);
                    if (videoId != null)
                    {
                        var song = new Song
                        {
                            YoutubeVideoId = videoId.Value,
                            Title = "Online Song " + videoId.Value.ToString().Substring(0, 8)
                        };
                        _defaultPlaylist.Enqueue(song);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to process online song URL: {Url}", songUrl);
                }
            }
        }

        private void SaveDefaultPlaylistToFile()
        {
            try
            {
                var playlistContent = new StringBuilder();
                var defaultSongs = _defaultPlaylist.ToList();

                foreach (var song in defaultSongs)
                {
                    if (!string.IsNullOrEmpty(song.FilePath) && File.Exists(song.FilePath))
                    {
                        playlistContent.AppendLine(Path.GetFullPath(song.FilePath));
                    }
                }

                // Ensure we always have silence as fallback
                var silencePath = Path.Combine(_audioFilesPath, "silence.mp3");
                if (File.Exists(silencePath))
                {
                    playlistContent.AppendLine(Path.GetFullPath(silencePath));
                }

                File.WriteAllText(_defaultPlaylistPath, playlistContent.ToString());
                _logger.LogInformation("📝 Default playlist saved to {Path}", _defaultPlaylistPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving default playlist to file");
            }
        }

        private void InitializeLiquidSoapConfig()
        {
            try
            {
                var icecastUrl = _config["Icecast:Url"] ?? "http://localhost:8087/live";
                if (!icecastUrl.StartsWith("http://") && !icecastUrl.StartsWith("https://"))
                    icecastUrl = $"http://{icecastUrl}";

                var uri = new Uri(icecastUrl);
                var host = uri.Host;
                var port = uri.Port;
                var mountPoint = uri.AbsolutePath.TrimStart('/');
                if (string.IsNullOrEmpty(mountPoint))
                    mountPoint = "live";

                var normalizedPlaylistPath = _mainPlaylistPath.Replace('\\', '/');
                var normalizedAudioPath = _audioFilesPath.Replace('\\', '/');

                var config = $@"# RadioHub LiquidSoap Configuration

set(""log.stdout"", true)
set(""log.level"", 4)
set(""server.telnet"", true)
set(""server.telnet.port"", 1234)
set(""server.telnet.password"", ""radiohub"")

# Create reliable silence source
silence = noise()
silence = amplify(0.0, silence)

# Main continuous playlist source
playlist_source = playlist.reloadable(
  ""main_playlist.txt"",
  mode=""normal"",
  reload_mode=""watch""
)

# Single file fallback (FFmpeg-generated silence)
single_fallback = single(""silence.mp3"")

# Robust fallback chain: playlist -> silence file -> generated silence
radio = fallback(track_sensitive=false, [playlist_source, single_fallback, silence])

# Icecast output (WAV format - Icecast will handle encoding)
output.icecast(
  %wav,
  host=""{host}"",
  port={port},
  password=""{_icecastPassword}"",
  mount=""{mountPoint}"",
  name=""RadioHub"",
  description=""Robust Continuous Radio Streaming"",
  genre=""Various"",
  url=""http://{host}:{port}/{mountPoint}"",
  public=true,
  radio
)
";

                File.WriteAllText(_liquidSoapConfigPath, config.Replace("\r\n", "\n"));
                _logger.LogInformation("LiquidSoap config created at {Path}", _liquidSoapConfigPath);

                // Pre-create robust silence file using FFmpeg
                _ = Task.Run(async () => await CreateRobustSilenceFileAsync(new CancellationToken()));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize LiquidSoap config");
                throw;
            }
        }

        private async Task CreateRobustSilenceFileAsync(CancellationToken token)
        {
            try
            {
                var silenceFilePath = Path.Combine(_audioFilesPath, "silence.mp3");

                if (!File.Exists(silenceFilePath))
                {
                    var ffmpegPath = FindFFmpeg();
                    if (ffmpegPath == null)
                    {
                        _logger.LogWarning("FFmpeg not found, skipping silence file creation");
                        return;
                    }

                    _logger.LogInformation("Creating robust silence file with FFmpeg: {FilePath}", silenceFilePath);

                    var startInfo = new ProcessStartInfo
                    {
                        FileName = ffmpegPath,
                        Arguments = "-f lavfi -i anullsrc=channel_layout=stereo:sample_rate=44100 -t 300 -q:a 2 -y \"" + silenceFilePath + "\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true
                    };

                    using var process = new Process { StartInfo = startInfo };
                    process.Start();

                    var output = await process.StandardOutput.ReadToEndAsync();
                    var error = await process.StandardError.ReadToEndAsync();

                    await process.WaitForExitAsync(token);

                    if (process.ExitCode == 0 && File.Exists(silenceFilePath))
                    {
                        var fileInfo = new FileInfo(silenceFilePath);
                        _logger.LogInformation("Robust silence file created: {Size} bytes", fileInfo.Length);
                    }
                    else
                    {
                        _logger.LogError("Failed to create silence file: {Error}", error);
                    }
                }
                else
                {
                    var fileInfo = new FileInfo(silenceFilePath);
                    _logger.LogInformation("Silence file already exists: {Size} bytes", fileInfo.Length);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating robust silence file");
            }
        }

        private void StartMainLoop()
        {
            _mainCts = new CancellationTokenSource();
            _workerTask = Task.Run(MainLoopAsync, _mainCts.Token);
        }

        private async Task MainLoopAsync()
        {
            _logger.LogInformation("=== Starting Robust Radio Station Loop ===");

            try
            {
                // Pre-populate with default playlist songs FIRST (before LiquidSoap starts)
                await InitializeDefaultSongsAsync(_mainCts.Token);

                // Give a brief moment to ensure files are ready
                await Task.Delay(1000, _mainCts.Token);

                // Start LiquidSoap with songs ready
                await StartLiquidSoapAsync();

                // Start monitoring tasks
                _monitorTask = Task.Run(MonitorLiquidSoapAsync, _mainCts.Token);
                _playlistManagerTask = Task.Run(ManagePlaylistLoopAsync, _mainCts.Token);

                _logger.LogInformation("✅ Radio station started successfully with {DefaultSongs} default songs!", _defaultPlaylist.Count);

                // Process user requests continuously
                while (!_mainCts.Token.IsCancellationRequested && !_disposed)
                {
                    await _queueSignal.WaitAsync(_mainCts.Token);

                    if (_userRequests.TryDequeue(out var userRequest))
                    {
                        _logger.LogInformation("🎯 Processing user request: {Title}", userRequest.Title);
                        await ProcessUserRequestAsync(userRequest, _mainCts.Token);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Radio station loop cancelled gracefully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fatal error in streaming loop");
            }
            finally
            {
                await StopLiquidSoapAsync();
            }
        }

        private async Task StartLiquidSoapAsync()
        {
            if (_isStreaming)
                return;

            _logger.LogInformation("🎵 Starting LiquidSoap with comprehensive logging...");

            try
            {
                // Log detailed startup information
                _logger.LogInformation("=== LIQUIDSOAP STARTUP DEBUG ===");
                _logger.LogInformation("LiquidSoap Path: {Path}", _liquidSoapPath);
                _logger.LogInformation("Config Path: {Config}", _liquidSoapConfigPath);
                _logger.LogInformation("Working Directory: {WorkDir}", _audioFilesPath);
                _logger.LogInformation("Current Directory: {CurrentDir}", Directory.GetCurrentDirectory());

                // Check if files exist
                var liquidSoapExists = File.Exists(_liquidSoapPath);
                var configExists = File.Exists(_liquidSoapConfigPath);
                var workingDirExists = Directory.Exists(_audioFilesPath);

                _logger.LogInformation("LiquidSoap executable exists: {Exists}", liquidSoapExists);
                _logger.LogInformation("Config file exists: {Exists}", configExists);
                _logger.LogInformation("Working directory exists: {Exists}", workingDirExists);

                if (!liquidSoapExists)
                {
                    _logger.LogError("❌ LiquidSoap executable not found at: {Path}", _liquidSoapPath);
                    throw new Exception($"LiquidSoap executable not found: {_liquidSoapPath}");
                }

                if (!configExists)
                {
                    _logger.LogError("❌ Config file not found at: {Path}", _liquidSoapConfigPath);
                    throw new Exception($"Config file not found: {_liquidSoapConfigPath}");
                }

                // Log config file contents
                var configContent = await File.ReadAllTextAsync(_liquidSoapConfigPath);
                _logger.LogInformation("Config file content preview:\n{Config}", configContent);

                // Log working directory contents
                var workingFiles = Directory.GetFiles(_audioFilesPath, "*.mp3").Take(5);
                _logger.LogInformation("Working directory contains MP3 files: {Files}",
                    string.Join(", ", workingFiles.Select(f => Path.GetFileName(f))));

                var startInfo = new ProcessStartInfo
                {
                    FileName = _liquidSoapPath,
                    Arguments = $"\"{Path.GetFileName(_liquidSoapConfigPath)}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = _audioFilesPath  // Set working directory to AudioFiles
                };

                _logger.LogInformation("Starting process with command: {FileName} {Arguments}", startInfo.FileName, startInfo.Arguments);
                _logger.LogInformation("Working directory set to: {WorkDir}", startInfo.WorkingDirectory);

                _liquidSoapProcess = new Process { StartInfo = startInfo };

                // Enhanced output capture
                var stdoutBuilder = new StringBuilder();
                var stderrBuilder = new StringBuilder();

                _liquidSoapProcess.OutputDataReceived += (_, e) => {
                    if (e.Data != null)
                    {
                        stdoutBuilder.AppendLine(e.Data);
                        _logger.LogInformation("LIQUIDSOAP STDOUT: {Message}", e.Data);
                    }
                };

                _liquidSoapProcess.ErrorDataReceived += (_, e) => {
                    if (e.Data != null)
                    {
                        stderrBuilder.AppendLine(e.Data);
                        _logger.LogError("LIQUIDSOAP STDERR: {Message}", e.Data);
                    }
                };

                _logger.LogInformation("🚀 Starting LiquidSoap process...");
                _liquidSoapProcess.Start();
                _liquidSoapProcess.BeginOutputReadLine();
                _liquidSoapProcess.BeginErrorReadLine();

                _logger.LogInformation("✅ Process started with PID: {PID}", _liquidSoapProcess.Id);

                // Wait for process to initialize
                _logger.LogInformation("⏳ Waiting for LiquidSoap to initialize...");
                await Task.Delay(2000);

                // Check process status after 2 seconds
                if (_liquidSoapProcess.HasExited)
                {
                    _logger.LogError("❌ LiquidSoap exited early. Exit Code: {ExitCode}", _liquidSoapProcess.ExitCode);
                    _logger.LogError("Full STDOUT:\n{Stdout}", stdoutBuilder.ToString());
                    _logger.LogError("Full STDERR:\n{Stderr}", stderrBuilder.ToString());
                    throw new Exception($"LiquidSoap failed during startup. Exit code: {_liquidSoapProcess.ExitCode}");
                }

                // Wait additional time for Icecast connection
                _logger.LogInformation("⏳ Waiting for Icecast connection...");
                await Task.Delay(3000);

                if (_liquidSoapProcess.HasExited)
                {
                    _logger.LogError("❌ LiquidSoap failed to connect to Icecast. Exit Code: {ExitCode}", _liquidSoapProcess.ExitCode);
                    _logger.LogError("Full STDOUT:\n{Stdout}", stdoutBuilder.ToString());
                    _logger.LogError("Full STDERR:\n{Stderr}", stderrBuilder.ToString());
                    throw new Exception($"LiquidSoap failed to connect to Icecast. Exit code: {_liquidSoapProcess.ExitCode}");
                }

                _isStreaming = true;
                _logger.LogInformation("🎉 LiquidSoap started successfully and is running!");
                _logger.LogInformation("Process PID: {PID}, HasExited: {HasExited}", _liquidSoapProcess.Id, _liquidSoapProcess.HasExited);

                await _hub.Clients.All.SendAsync("StreamStarted");
                _logger.LogInformation("=== LIQUIDSOAP STARTUP COMPLETED SUCCESSFULLY ===");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error starting LiquidSoap");
                _logger.LogError("Exception details: {Exception}", ex.ToString());
                throw;
            }
        }

        private async Task StopLiquidSoapAsync()
        {
            try
            {
                if (_liquidSoapProcess is { HasExited: false })
                {
                    _logger.LogInformation("Stopping LiquidSoap...");
                    _liquidSoapProcess.Kill(true);
                    await _liquidSoapProcess.WaitForExitAsync();
                }

                _isStreaming = false;
                await _hub.Clients.All.SendAsync("StreamStopped");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error stopping LiquidSoap");
            }
        }

        private async Task MonitorLiquidSoapAsync()
        {
            while (!_mainCts.Token.IsCancellationRequested && !_disposed)
            {
                try
                {
                    if (_liquidSoapProcess == null || _liquidSoapProcess.HasExited)
                    {
                        _logger.LogWarning("LiquidSoap stopped. Restarting...");
                        _isStreaming = false;
                        await StartLiquidSoapAsync();
                    }

                    await Task.Delay(5000, _mainCts.Token);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error monitoring LiquidSoap");
                }
            }
        }

        private async Task ProcessSongAsync(Song song, CancellationToken token)
        {
            try
            {
                lock (_currentlyPlayingLock) _currentSong = song;

                _songStatus[song.YoutubeVideoId] = SongStatus.Preparing;
                await _hub.Clients.All.SendAsync("SongPreparing", new { song.Title });

                var filePath = await GetOrDownloadSongAsync(song, token);
                if (filePath == null) return;

                // Song is automatically added to playlist by UpdateMainPlaylistAsync
                _songStatus[song.YoutubeVideoId] = SongStatus.Ready;
                await _hub.Clients.All.SendAsync("SongReady", new { song.Title });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing song {Title}", song.Title);
            }
        }

              #region Enhanced Playlist Management

        private async Task InitializeDefaultSongsAsync(CancellationToken token)
        {
            _logger.LogInformation("📻 Initializing default playlist songs...");

            // Add songs from default playlist to download queue
            var defaultSongs = new List<Song>();
            while (_defaultPlaylist.TryDequeue(out var song))
            {
                defaultSongs.Add(song);
            }

            // Re-add them to the default playlist (to maintain the queue)
            foreach (var song in defaultSongs)
            {
                _defaultPlaylist.Enqueue(song);
                await ProcessDefaultSongAsync(song, token);
            }

            _logger.LogInformation("✅ Default playlist initialized with {Count} songs", defaultSongs.Count);

            // IMMEDIATELY create main playlist with available songs
            await CreateImmediateMainPlaylist(token);
        }

        private async Task CreateImmediateMainPlaylist(CancellationToken token)
        {
            try
            {
                _logger.LogInformation("🚀 Creating immediate main playlist for startup...");

                var playlistContent = new StringBuilder();
                var songCount = 0;

                // Add default songs that have file paths (using relative paths for LiquidSoap)
                var defaultSongs = _defaultPlaylist.ToList();
                foreach (var song in defaultSongs)
                {
                    var filePath = GetSongFilePath(song);
                    if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                    {
                        // Use relative path from AudioFiles directory for LiquidSoap
                        var relativePath = Path.GetFileName(filePath);
                        playlistContent.AppendLine(relativePath);
                        songCount++;
                    }
                }

                // If no default songs found, add at least one existing song (relative paths)
                if (songCount == 0)
                {
                    var existingSongs = Directory.GetFiles(_audioFilesPath, "*.mp3", SearchOption.TopDirectoryOnly)
                        .Where(f => !Path.GetFileName(f).Equals("silence.mp3", StringComparison.OrdinalIgnoreCase))
                        .Take(3);

                    foreach (var songFile in existingSongs)
                    {
                        var relativePath = Path.GetFileName(songFile);
                        playlistContent.AppendLine(relativePath);
                        songCount++;
                    }
                }

                // Always add silence as fallback (relative path)
                playlistContent.AppendLine("silence.mp3");

                await File.WriteAllTextAsync(_mainPlaylistPath, playlistContent.ToString(), token);

                _logger.LogInformation("✅ Immediate main playlist created with {Count} songs using relative paths", songCount);

                // Set the first default song as currently playing
                if (defaultSongs.Count > 0)
                {
                    lock (_currentlyPlayingLock)
                    {
                        _currentSong = defaultSongs.First();
                    }

                    // Notify frontend about the current song
                    _ = _hub.Clients.All.SendAsync("SongStarted", new {
                        title = _currentSong.Title,
                        youtubeVideoId = _currentSong.YoutubeVideoId,
                        isUserRequest = false,
                        isDefaultPlaylist = true
                    });
                }

                // Force trigger playlist update for SignalR
                _ = _hub.Clients.All.SendAsync("PlaylistUpdated", new {
                    UserRequestCount = 0,
                    DefaultPlaylistCount = defaultSongs.Count,
                    TotalSongsCount = songCount,
                    HasUserRequests = false,
                    IsPlayingDefaultPlaylist = true
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating immediate main playlist");
            }
        }

        private async Task ManagePlaylistLoopAsync()
        {
            _logger.LogInformation("🔄 Starting playlist manager loop...");

            while (!_mainCts!.Token.IsCancellationRequested && !_disposed)
            {
                try
                {
                    // Update main playlist periodically and when needed
                    var timeSinceLastUpdate = DateTime.Now - _lastPlaylistUpdate;
                    if (timeSinceLastUpdate >= _minPlaylistUpdateInterval)
                    {
                        await UpdateMainPlaylistAsync(_mainCts.Token);
                        _lastPlaylistUpdate = DateTime.Now;
                    }

                    await Task.Delay(3000, _mainCts.Token); // Check every 3 seconds
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in playlist manager loop");
                    await Task.Delay(5000, _mainCts.Token);
                }
            }
        }

        private async Task UpdateMainPlaylistAsync(CancellationToken token)
        {
            try
            {
                var playlistContent = new StringBuilder();
                var totalSongs = 0;

                // Priority 1: Add all user requests first (highest priority)
                var userRequestList = _userRequests.ToList();
                foreach (var song in userRequestList)
                {
                    var filePath = GetSongFilePath(song);
                    if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                    {
                        // Use relative path for LiquidSoap
                        var relativePath = Path.GetFileName(filePath);
                        playlistContent.AppendLine(relativePath);
                        totalSongs++;
                    }
                }

                // Priority 2: Add default playlist songs if no user requests or to fill gaps
                var defaultSongs = _defaultPlaylist.ToList();
                foreach (var song in defaultSongs)
                {
                    // Skip if this song is already in user requests to avoid duplication
                    if (userRequestList.Any(ur => ur.YoutubeVideoId == song.YoutubeVideoId))
                        continue;

                    var filePath = GetSongFilePath(song);
                    if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                    {
                        // Use relative path for LiquidSoap
                        var relativePath = Path.GetFileName(filePath);
                        playlistContent.AppendLine(relativePath);
                        totalSongs++;
                    }
                }

                // Priority 3: Always ensure we have content - add silence as last resort
                playlistContent.AppendLine("silence.mp3");
                totalSongs++;

                await File.WriteAllTextAsync(_mainPlaylistPath, playlistContent.ToString(), token);

                _logger.LogInformation("📝 Playlist updated: {UserRequests} user requests + {DefaultSongs} default songs",
                    userRequestList.Count, defaultSongs.Count - userRequestList.Count(ur => defaultSongs.Any(ds => ds.YoutubeVideoId == ur.YoutubeVideoId)));

                // Notify clients about the current playlist status
                await _hub.Clients.All.SendAsync("PlaylistUpdated", new {
                    UserRequestCount = userRequestList.Count,
                    DefaultPlaylistCount = defaultSongs.Count,
                    TotalSongsCount = totalSongs,
                    HasUserRequests = userRequestList.Count > 0,
                    IsPlayingDefaultPlaylist = userRequestList.Count == 0
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating main playlist");
            }
        }

        private string? GetSongFilePath(Song song)
        {
            // Check if the song has a specific file path (for existing AudioFiles)
            if (!string.IsNullOrEmpty(song.FilePath) && File.Exists(song.FilePath))
            {
                return song.FilePath;
            }

            // Check if the song has been downloaded via YouTube
            if (_downloadedFiles.TryGetValue(song.YoutubeVideoId, out var downloadedPath) && File.Exists(downloadedPath))
            {
                return downloadedPath;
            }

            return null;
        }

        private async Task ProcessUserRequestAsync(Song song, CancellationToken token)
        {
            try
            {
                _logger.LogInformation("🎯 Processing user request: {Title} (Queue position: {Position})", song.Title, _userRequests.Count);

                // Download and convert the song if not already available
                var filePath = GetSongFilePath(song);
                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                {
                    _songStatus[song.YoutubeVideoId] = SongStatus.Preparing;
                    await _hub.Clients.All.SendAsync("SongPreparing", new { song.Title, song.YoutubeVideoId, isUserRequest = true });

                    filePath = await GetOrDownloadSongAsync(song, token);
                    if (filePath == null)
                    {
                        _logger.LogError("❌ Failed to prepare user request: {Title}", song.Title);
                        _songStatus[song.YoutubeVideoId] = SongStatus.Failed;
                        await _hub.Clients.All.SendAsync("SongFailed", new { song.Title, song.YoutubeVideoId, isUserRequest = true });
                        return;
                    }
                }

                _songStatus[song.YoutubeVideoId] = SongStatus.Ready;
                await _hub.Clients.All.SendAsync("SongReady", new { song.Title, song.YoutubeVideoId, isUserRequest = true });

                // Update the main playlist to include this song
                await UpdateMainPlaylistAsync(token);

                _logger.LogInformation("✅ User request ready and queued: {Title}", song.Title);
                await _hub.Clients.All.SendAsync("UserRequestQueued", new {
                    song.Title,
                    song.YoutubeVideoId,
                    queuePosition = _userRequests.Count,
                    totalUserRequests = _userRequests.Count,
                    hasDefaultPlaylist = _defaultPlaylist.Count > 0
                });

                // Set as current song if this is the first/only user request
                if (_userRequests.Count == 1)
                {
                    lock (_currentlyPlayingLock)
                    {
                        _currentSong = song;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing user request: {Title}", song.Title);
                _songStatus[song.YoutubeVideoId] = SongStatus.Failed;
                await _hub.Clients.All.SendAsync("SongFailed", new { song.Title, song.YoutubeVideoId, isUserRequest = true });
            }
        }

        private async Task ProcessDefaultSongAsync(Song song, CancellationToken token)
        {
            try
            {
                if (!_downloadedFiles.ContainsKey(song.YoutubeVideoId))
                {
                    _songStatus[song.YoutubeVideoId] = SongStatus.Preparing;
                    _logger.LogInformation("📻 Preparing default playlist song: {Title}", song.Title);

                    var filePath = await GetOrDownloadSongAsync(song, token);
                    if (filePath != null)
                    {
                        _songStatus[song.YoutubeVideoId] = SongStatus.Ready;
                        _logger.LogDebug("✅ Default song ready: {Title}", song.Title);
                    }
                    else
                    {
                        _songStatus[song.YoutubeVideoId] = SongStatus.Failed;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing default song: {Title}", song.Title);
                _songStatus[song.YoutubeVideoId] = SongStatus.Failed;
            }
        }

        #endregion

        private async Task<string?> GetOrDownloadSongAsync(Song song, CancellationToken token)
        {
            if (_downloadedFiles.TryGetValue(song.YoutubeVideoId, out var path) && File.Exists(path))
                return path;

            var manifest = await _youtube.Videos.Streams.GetManifestAsync(song.YoutubeVideoId, token);
            var audioStream = manifest.GetAudioOnlyStreams().GetWithHighestBitrate();
            if (audioStream == null)
                return null;

            var safeTitle = SanitizeFileName(song.Title);
            var tempFilePath = Path.Combine(_audioFilesPath, $"{song.YoutubeVideoId}_{safeTitle}_temp.{audioStream.Container.Name}");
            var finalFilePath = Path.Combine(_audioFilesPath, $"{song.YoutubeVideoId}_{safeTitle}.mp3");

            // Download original format first
            await _youtube.Videos.Streams.DownloadAsync(audioStream, tempFilePath);

            // Convert to standard MP3 using FFmpeg for better compatibility with LiquidSoap
            var success = await ConvertToStandardMp3Async(tempFilePath, finalFilePath, token);

            if (success)
            {
                // Clean up temp file
                try { File.Delete(tempFilePath); } catch { }
                _downloadedFiles[song.YoutubeVideoId] = finalFilePath;
                _logger.LogInformation("FFmpeg converted: {Title} -> MP3", song.Title);
                return finalFilePath;
            }
            else
            {
                // Fallback to original file if conversion fails
                _downloadedFiles[song.YoutubeVideoId] = tempFilePath;
                _logger.LogWarning("FFmpeg conversion failed, using original: {Title}", song.Title);
                return tempFilePath;
            }
        }

        private async Task<bool> ConvertToStandardMp3Async(string inputPath, string outputPath, CancellationToken token)
        {
            try
            {
                var ffmpegPath = FindFFmpeg();
                if (ffmpegPath == null)
                {
                    _logger.LogWarning("FFmpeg not found, skipping conversion");
                    return false;
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = $"-i \"{inputPath}\" -vn -acodec libmp3lame -ab 128k -ar 44100 -ac 2 -y \"{outputPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };

                using var process = new Process { StartInfo = startInfo };
                process.Start();

                var errorOutput = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync(token);

                if (process.ExitCode == 0 && File.Exists(outputPath))
                {
                    var outputSize = new FileInfo(outputPath).Length;
                    _logger.LogDebug("FFmpeg conversion successful: {Size} bytes", outputSize);
                    return true;
                }
                else
                {
                    _logger.LogError("FFmpeg conversion failed: {Error}", errorOutput);
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during FFmpeg conversion");
                return false;
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

        private string SanitizeFileName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }

        public void EnqueueSong(Song song)
        {
            // User requests get priority over default playlist
            _userRequests.Enqueue(song);
            _songStatus[song.YoutubeVideoId] = SongStatus.Queued;
            _queueSignal.Release();
            _logger.LogInformation("🎵 User request enqueued: {Title} (Priority queue size: {Size})", song.Title, _userRequests.Count);

            // Notify clients about the new request
            _hub.Clients.All.SendAsync("UserRequestAdded", new {
                song.Title,
                song.YoutubeVideoId,
                queueSize = _userRequests.Count,
                isPriorityRequest = true
            });

            // Trigger playlist update immediately
            _ = Task.Run(() => UpdateMainPlaylistAsync(_mainCts?.Token ?? CancellationToken.None));
        }

        public Task<bool> IsStreamingAsync() => Task.FromResult(_isStreaming);

        public async Task StopAsync()
        {
            _logger.LogInformation("Stopping LiquidSoap service...");
            _mainCts?.Cancel();
            await StopLiquidSoapAsync();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _mainCts?.Cancel();
            _queueSignal.Dispose();
            _userRequests.Clear();
            _defaultPlaylist.Clear();
            _logger.LogInformation("LiquidSoapStreamingService disposed.");
        }

              public List<Song> GetQueue() => _userRequests.ToList();

        public Song GetCurrentlyPlaying()
        {
            lock (_currentlyPlayingLock)
            {
                return _currentSong ?? new Song();
            }
        }

        // Enhanced methods for robust radio station
        public List<Song> GetUserRequests() => _userRequests.ToList();
        public List<Song> GetDefaultPlaylist() => _defaultPlaylist.ToList();
        public int GetDefaultPlaylistCount() => _defaultPlaylist.Count;

        // Radio station status
        public object GetRadioStationStatus()
        {
            return new
            {
                IsStreaming = _isStreaming,
                CurrentSong = _currentSong,
                UserRequestsCount = _userRequests.Count,
                DefaultPlaylistCount = _defaultPlaylist.Count,
                DownloadedFilesCount = _downloadedFiles.Count,
                CurrentQueueSize = _userRequests.Count,
                IsContinuous = true,
                HasDefaultPlaylist = _defaultPlaylist.Count > 0,
                LastPlaylistUpdate = _lastPlaylistUpdate
            };
        }

        private string? FindLiquidSoap()
        {
            try
            {
                _logger.LogInformation("🔍 Searching for LiquidSoap executable...");

                // First try the 'where' command (Windows 'which' equivalent)
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "where",
                        Arguments = "liquidsoap",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                if (process.ExitCode == 0 && !string.IsNullOrEmpty(output))
                {
                    var path = output.Split('\n')[0].Trim();
                    _logger.LogInformation("✅ Found LiquidSoap at: {Path}", path);
                    return path;
                }

                _logger.LogWarning("LiquidSoap not found in PATH using 'where' command");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error searching for LiquidSoap with 'where' command");
            }

            // Try common installation locations for Windows
            var commonPaths = new[]
            {
                @"C:\Program Files\liquidsoap\liquidsoap.exe",
                @"C:\Program Files (x86)\liquidsoap\liquidsoap.exe",
                @"C:\liquidsoap\liquidsoap.exe",
                @"C:\tools\liquidsoap\liquidsoap.exe"
            };

            foreach (var path in commonPaths)
            {
                if (File.Exists(path))
                {
                    _logger.LogInformation("✅ Found LiquidSoap at: {Path}", path);
                    return path;
                }
            }

            _logger.LogWarning("❌ LiquidSoap not found. Please install LiquidSoap or add it to PATH.");
            return null;
        }
    }
}
