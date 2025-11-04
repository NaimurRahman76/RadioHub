using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using NAudio.Lame;
using NAudio.CoreAudioApi;
using RadioStation.Models;
using RadioStation.Hubs;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using YoutubeExplode;
using YoutubeExplode.Videos.Streams;

namespace RadioStation.Services
{
    /// <summary>
    /// NAudio-based streaming service for seamless audio playback
    /// Uses NAudio for audio processing and direct Icecast streaming
    /// </summary>
    public class NAudioStreamingService : IStreamingService, IDisposable
    {
        private readonly ILogger<NAudioStreamingService> _logger;
        private readonly IConfiguration _config;
        private readonly IHubContext<RadioHub> _hub;
        private readonly YoutubeClient _youtube;

        private readonly ConcurrentQueue<Song> _queue = new();
        private readonly ConcurrentDictionary<string, string> _downloadedFiles = new();
        private readonly ConcurrentDictionary<string, SongStatus> _songStatus = new();
        private readonly SemaphoreSlim _queueSignal = new(0);
        private readonly object _currentlyPlayingLock = new();

        public enum SongStatus
        {
            Queued,
            Preparing,
            Ready,
            Playing,
            Completed,
            Failed
        }

        private Song? _currentSong;
        private CancellationTokenSource? _mainCts;
        private Task? _streamingTask;
        private bool _isStreaming = false;
        private bool _disposed = false;

        // Icecast configuration
        private readonly string _icecastHost;
        private readonly int _icecastPort;
        private readonly string _icecastPassword;
        private readonly string _icecastMount;
        private readonly string _audioFilesPath;

        // Audio processing components
        private TcpClient? _icecastClient;
        private NetworkStream? _icecastStream;
        private LameMP3FileWriter? _mp3Writer;
        private MemoryStream? _mp3Stream;

        // Buffer for continuous streaming
        private readonly BufferedWaveProvider _bufferedWaveProvider;
        private readonly object _bufferLock = new();

        // Reconnection rate limiting
        private DateTime _lastReconnectionAttempt = DateTime.MinValue;
        private readonly TimeSpan _minReconnectionInterval = TimeSpan.FromSeconds(10);

        public Task<bool> IsStreamingAsync()
        {
            return Task.FromResult(_isStreaming);
        }

        public NAudioStreamingService(ILogger<NAudioStreamingService> logger, IConfiguration config, IHubContext<RadioHub> hub)
        {
            _logger = logger;
            _config = config;
            _hub = hub;
            _youtube = new YoutubeClient();

            _audioFilesPath = config["AudioFiles:Path"] ?? Path.Combine(Directory.GetCurrentDirectory(), "AudioFiles");

            // Parse Icecast configuration
            var icecastUrl = config["Icecast:Url"] ?? "http://localhost:8087/live";
            _logger.LogInformation("Icecast URL from config: {Url}", icecastUrl);

            // Ensure the URL has a protocol
            if (!icecastUrl.StartsWith("http://") && !icecastUrl.StartsWith("https://"))
            {
                icecastUrl = $"http://{icecastUrl}";
            }

            var uri = new Uri(icecastUrl);
            _icecastHost = uri.Host;
            _icecastPort = uri.Port > 0 ? uri.Port : 8087;
            _icecastMount = string.IsNullOrEmpty(uri.AbsolutePath) || uri.AbsolutePath == "/" ? "live" : uri.AbsolutePath.TrimStart('/');
            _icecastPassword = config["Icecast:Password"] ?? "hackme76";

            _logger.LogInformation("Parsed Icecast config - Host: {Host}, Port: {Port}, Mount: {Mount}",
                _icecastHost, _icecastPort, _icecastMount);

            Directory.CreateDirectory(_audioFilesPath);

            // Initialize buffered wave provider for seamless transitions
            _bufferedWaveProvider = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2))
            {
                BufferDuration = TimeSpan.FromSeconds(30),
                DiscardOnBufferOverflow = true
            };

            _logger.LogInformation("=== NAudio Streaming Service Initialized ===");
            _logger.LogInformation("Audio files path: {Path}", _audioFilesPath);
            _logger.LogInformation("Icecast: {Host}:{Port}/{Mount} (Password: {Password})", _icecastHost, _icecastPort, _icecastMount, _icecastPassword);

            StartStreaming();
        }

        public void EnqueueSong(Song song)
        {
            _queue.Enqueue(song);
            _songStatus.TryAdd(song.YoutubeVideoId, SongStatus.Queued);
            _queueSignal.Release();

            _logger.LogInformation("Song enqueued: {Title} (Queue size: {Size})", song.Title, _queue.Count);

            // Notify clients
            _ = Task.Run(async () => await _hub.Clients.All.SendAsync("QueueUpdated", new { song.Title, song.YoutubeVideoId }));
        }

        public List<Song> GetQueue() => _queue.ToList();

        public Song GetCurrentlyPlaying()
        {
            lock (_currentlyPlayingLock)
            {
                return _currentSong ?? new Song();
            }
        }

        public async Task StopAsync()
        {
            _logger.LogInformation("Stopping NAudio streaming...");
            _mainCts?.Cancel();

            if (_streamingTask != null)
            {
                await _streamingTask;
            }

            await DisconnectFromIcecastAsync();
            _isStreaming = false;

            await _hub.Clients.All.SendAsync("StreamStopped");
        }

        private void StartStreaming()
        {
            _mainCts = new CancellationTokenSource();
            _streamingTask = Task.Run(StreamingLoopAsync, _mainCts.Token);
        }

        private async Task StreamingLoopAsync()
        {
            _logger.LogInformation("Starting NAudio streaming loop");
            _isStreaming = true;

            try
            {
                // Connect to Icecast immediately
                await ConnectToIcecastAsync();

                // Start audio processing task
                var audioProcessingTask = Task.Run(AudioProcessingLoopAsync, _mainCts.Token);

                // Start continuous content generation (silence when no songs)
                var contentTask = Task.Run(ContinuousContentGenerationAsync, _mainCts.Token);

                // Process song queue
                while (!_mainCts.Token.IsCancellationRequested && !_disposed)
                {
                    try
                    {
                        // Check if we need to reconnect before processing songs
                        if (!_isStreaming || !await ValidateIcecastConnectionAsync(_mainCts.Token))
                        {
                            _logger.LogInformation("Connection lost. Attempting to reconnect...");
                            try
                            {
                                await ReconnectToIcecastAsync(_mainCts.Token);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Failed to reconnect to Icecast");
                                await Task.Delay(5000, _mainCts.Token); // Wait before retry
                                continue;
                            }
                        }

                        // Wait for songs in queue with timeout to prevent deadlock
                        await Task.WhenAny(_queueSignal.WaitAsync(_mainCts.Token), Task.Delay(5000, _mainCts.Token));

                        if (_queue.TryDequeue(out var song))
                        {
                            await ProcessSongAsync(song, _mainCts.Token);
                        }
                        else
                        {
                            // No songs, continue with silence
                            await Task.Delay(1000, _mainCts.Token);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error in streaming loop");
                        await Task.Delay(2000, _mainCts.Token);
                    }
                }

                await Task.WhenAll(audioProcessingTask, contentTask);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fatal error in streaming loop");
            }
            finally
            {
                _isStreaming = false;
                await DisconnectFromIcecastAsync();
            }
        }

        private async Task ConnectToIcecastAsync()
        {
            try
            {
                _logger.LogInformation("Connecting to Icecast at {Host}:{Port}...", _icecastHost, _icecastPort);
                _icecastClient = new TcpClient();
                await _icecastClient.ConnectAsync(_icecastHost, _icecastPort);
                _icecastStream = _icecastClient.GetStream();
                _logger.LogInformation("TCP connection established to Icecast");

                // Send Icecast source headers
                var headers = $"SOURCE /{_icecastMount} HTTP/1.1\r\n" +
                             $"Host: {_icecastHost}:{_icecastPort}\r\n" +
                             $"Authorization: Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($"source:{_icecastPassword}"))}\r\n" +
                             $"User-Agent: RadioHub NAudioStreamer\r\n" +
                             $"Content-Type: audio/mpeg\r\n" +
                             $"Content-Length: 0\r\n" +
                             $"Public: 1\r\n" +
                             $"Ice-Name: RadioHub\r\n" +
                             $"Ice-Description: NAudio-powered radio station\r\n" +
                             $"Ice-Genre: Music\r\n" +
                             $"Ice-URL: http://{_icecastHost}:{_icecastPort}/\r\n" +
                             $"Ice-Audio-Info: ice-samplerate=44100;ice-bitrate=128;ice-channels=2\r\n" +
                             "\r\n";

                _logger.LogInformation("Sending Icecast headers:\n{Headers}", headers);

                var headerBytes = Encoding.UTF8.GetBytes(headers);
                await _icecastStream.WriteAsync(headerBytes, 0, headerBytes.Length);
                await _icecastStream.FlushAsync();

                // Read response to verify connection
                var responseBuffer = new byte[2048];
                var bytesRead = await _icecastStream.ReadAsync(responseBuffer, 0, responseBuffer.Length);
                var response = Encoding.UTF8.GetString(responseBuffer, 0, bytesRead);

                _logger.LogInformation("Icecast response: {Response}", response.Trim());

                if (response.Contains("200 OK") || response.Contains("200") || response.Contains("HTTP/1.1 200"))
                {
                    _logger.LogInformation("Successfully connected to Icecast server");

                    // Initialize MP3 writer for streaming with a memory buffer
                    _mp3Stream = new MemoryStream();
                    _mp3Writer = new LameMP3FileWriter(_mp3Stream,
                        new WaveFormat(44100, 2),
                        128); // 128 kbps

                    // Start a background task to continuously send data to Icecast
                    _ = Task.Run(() => SendMpegStreamToIcecastAsync(_mainCts.Token));

                    await _hub.Clients.All.SendAsync("StreamStarted");
                }
                else
                {
                    _logger.LogError("Icecast connection failed. Server response: {Response}", response);

                    // Check for specific error codes
                    if (response.Contains("401") || response.Contains("Unauthorized"))
                    {
                        throw new Exception("Icecast authentication failed - check source password");
                    }
                    else if (response.Contains("403") || response.Contains("Forbidden"))
                    {
                        throw new Exception("Icecast access forbidden - mount point may be in use");
                    }
                    else if (response.Contains("404") || response.Contains("Not Found"))
                    {
                        throw new Exception($"Icecast mount point '/{_icecastMount}' not found");
                    }
                    else
                    {
                        throw new Exception($"Icecast connection failed: {response}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to connect to Icecast");
                throw;
            }
        }

        private async Task DisconnectFromIcecastAsync()
        {
            try
            {
                _mp3Writer?.Dispose();
                _icecastStream?.Close();
                _icecastClient?.Close();
                _logger.LogInformation("Disconnected from Icecast");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disconnecting from Icecast");
            }
        }

        private async Task ProcessSongAsync(Song song, CancellationToken token)
        {
            try
            {
                lock (_currentlyPlayingLock)
                {
                    _currentSong = song;
                }

                _logger.LogInformation("Starting to stream: {Title}", song.Title);
                _songStatus.TryAdd(song.YoutubeVideoId, SongStatus.Preparing);

                await _hub.Clients.All.SendAsync("SongPreparing", new { song.Title, song.YoutubeVideoId });

                // Get or download the audio file
                var filePath = await GetOrDownloadSongAsync(song, token);
                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                {
                    _logger.LogWarning("Audio file not found for: {Title}", song.Title);
                    await PlaySilenceAsync(token);
                    return;
                }

                _songStatus.TryAdd(song.YoutubeVideoId, SongStatus.Playing);
                await _hub.Clients.All.SendAsync("SongStarted", new
                {
                    song.Title,
                    song.YoutubeVideoId,
                    queueSize = _queue.Count
                });

                // Stream the audio file using NAudio
                await StreamAudioFileAsync(filePath, token);

                _logger.LogInformation("Finished streaming: {Title}", song.Title);
                _songStatus.TryAdd(song.YoutubeVideoId, SongStatus.Completed);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error streaming song: {Title}", song.Title);
                _songStatus.TryAdd(song.YoutubeVideoId, SongStatus.Failed);
            }
        }

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

        private async Task StreamAudioFileAsync(string filePath, CancellationToken token)
        {
            try
            {
                using var audioFile = new AudioFileReader(filePath);

                // Check if we have a valid connection before streaming
                if (!await ValidateIcecastConnectionAsync(token))
                {
                    _logger.LogWarning("Icecast connection not available. Skipping song playback.");
                    return;
                }

                if (_mp3Writer == null) return;

                // Read audio data in chunks
                var buffer = new byte[8192]; // 8KB buffer
                int consecutiveErrors = 0;
                const int maxConsecutiveErrors = 3; // Reduced to prevent long loops
                int bytesRead;

                _logger.LogInformation("Starting to stream audio: {FilePath}", filePath);

                while ((bytesRead = audioFile.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (token.IsCancellationRequested) break;

                    try
                    {
                        // Write the raw audio bytes to the MP3 encoder
                        _mp3Writer.Write(buffer, 0, bytesRead);

                        // Reset error counter on successful write
                        consecutiveErrors = 0;

                        // Periodically flush to send data to Icecast
                        if (bytesRead % 4096 == 0) // Flush every 4KB
                        {
                            await _mp3Writer.FlushAsync(token);
                        }
                    }
                    catch (InvalidOperationException ex) when (ex.Message.Contains("Output stream closed"))
                    {
                        consecutiveErrors++;
                        _logger.LogWarning("Icecast stream closed (error {Count}/{Max}). Connection may be unstable.", consecutiveErrors, maxConsecutiveErrors);

                        if (consecutiveErrors >= maxConsecutiveErrors)
                        {
                            _logger.LogError("Too many consecutive stream errors. Aborting song playback.");
                            // Mark connection as broken and let the main loop handle recovery
                            _isStreaming = false;
                            break;
                        }

                        // Brief delay before retry
                        await Task.Delay(500, token);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error writing audio chunk");
                        consecutiveErrors++;

                        if (consecutiveErrors >= maxConsecutiveErrors)
                        {
                            _logger.LogError("Too many consecutive errors. Aborting song playback.");
                            _isStreaming = false;
                            break;
                        }

                        await Task.Delay(1000, token);
                    }
                }

                _logger.LogInformation("Finished streaming audio: {FilePath}", filePath);

                // Final flush to ensure all data is sent
                try
                {
                    await _mp3Writer.FlushAsync(token);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error during final flush");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error streaming audio file: {FilePath}", filePath);
            }
        }

        private async Task PlaySilenceAsync(CancellationToken token)
        {
            _logger.LogInformation("Playing silence");

            // Generate silence using NAudio
            var silenceProvider = new SilenceProvider(new WaveFormat(44100, 2));
            var buffer = new byte[4096];

            // Play 5 seconds of silence
            for (int i = 0; i < 5 * 44100 * 4 / 4096; i++) // 5 seconds at 44.1kHz stereo
            {
                if (token.IsCancellationRequested) break;

                var bytesRead = silenceProvider.Read(buffer, 0, buffer.Length);
                if (_mp3Writer != null)
                {
                    await _mp3Writer.WriteAsync(buffer, 0, bytesRead);
                }
            }
        }

        private async Task ContinuousContentGenerationAsync()
        {
            _logger.LogInformation("Starting continuous content generation");

            int consecutiveSilenceErrors = 0;
            const int maxSilenceErrors = 3;

            while (!_mainCts.Token.IsCancellationRequested && !_disposed)
            {
                try
                {
                    // Check if there's a currently playing song
                    bool hasSong = false;
                    lock (_currentlyPlayingLock)
                    {
                        hasSong = _currentSong != null;
                    }

                    if (!hasSong)
                    {
                        // No song playing, generate silence
                        await GenerateSilenceAsync(_mainCts.Token);
                        consecutiveSilenceErrors = 0; // Reset on success
                    }
                    else
                    {
                        // Song is playing, wait a bit before checking again
                        await Task.Delay(1000, _mainCts.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    consecutiveSilenceErrors++;
                    _logger.LogError(ex, "Error in continuous content generation ({Count}/{Max})", consecutiveSilenceErrors, maxSilenceErrors);

                    if (consecutiveSilenceErrors >= maxSilenceErrors)
                    {
                        _logger.LogWarning("Too many consecutive content generation errors. Attempting stream recovery...");
                        try
                        {
                            await ReconnectToIcecastAsync(_mainCts.Token);
                            consecutiveSilenceErrors = 0; // Reset after successful reconnection
                        }
                        catch (Exception reconnectEx)
                        {
                            _logger.LogError(reconnectEx, "Stream recovery failed");
                            await Task.Delay(5000, _mainCts.Token); // Longer delay if recovery fails
                        }
                    }
                    else
                    {
                        await Task.Delay(2000, _mainCts.Token);
                    }
                }
            }
        }

        private async Task GenerateSilenceAsync(CancellationToken token)
        {
            try
            {
                // Validate connection before generating silence
                if (!await ValidateIcecastConnectionAsync(token))
                {
                    _logger.LogDebug("Icecast connection not available for silence generation");
                    await Task.Delay(1000, token);
                    return;
                }

                if (_mp3Writer == null) return;

                // Generate 1 second of silence (16-bit PCM, 44.1kHz, stereo)
                var silenceData = new byte[44100 * 2 * 2]; // 1 second at 44.1kHz 16-bit stereo

                try
                {
                    _mp3Writer.Write(silenceData, 0, silenceData.Length);
                    await _mp3Writer.FlushAsync(token);
                    _logger.LogDebug("Generated 1 second of silence");
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("Output stream closed"))
                {
                    _logger.LogWarning("Icecast stream closed during silence generation. Marking connection as broken.");
                    _isStreaming = false; // Let main loop handle reconnection
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error writing silence to MP3 writer");
                    _isStreaming = false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error generating silence");
            }
        }

        private async Task SendMpegStreamToIcecastAsync(CancellationToken token)
        {
            try
            {
                if (_mp3Stream == null || _icecastStream == null)
                    return;

                var buffer = new byte[8192];
                while (!token.IsCancellationRequested && _isStreaming)
                {
                    var position = _mp3Stream.Position;
                    if (position < _mp3Stream.Length)
                    {
                        var availableBytes = (int)Math.Min(buffer.Length, _mp3Stream.Length - position);
                        var bytesRead = await _mp3Stream.ReadAsync(buffer, 0, availableBytes, token);

                        if (bytesRead > 0)
                        {
                            await _icecastStream.WriteAsync(buffer, 0, bytesRead, token);
                            await _icecastStream.FlushAsync(token);
                        }
                        else
                        {
                            await Task.Delay(10, token); // Brief pause if no data
                        }
                    }
                    else
                    {
                        await Task.Delay(100, token); // Wait for more data
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending MP3 stream to Icecast");
                _isStreaming = false;
            }
        }

        private async Task<bool> ValidateIcecastConnectionAsync(CancellationToken token)
        {
            try
            {
                // Check if we have basic components
                if (_icecastStream == null || _mp3Writer == null || !_isStreaming)
                {
                    return false;
                }

                // Try a small test write to see if the stream is still alive
                var testData = new byte[100]; // Small test data
                _mp3Writer.Write(testData, 0, testData.Length);
                await _mp3Writer.FlushAsync(token);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Icecast connection validation failed");
                return false;
            }
        }

        private async Task ReconnectToIcecastAsync(CancellationToken token)
        {
            // Rate limiting: prevent too frequent reconnection attempts
            var timeSinceLastAttempt = DateTime.Now - _lastReconnectionAttempt;
            if (timeSinceLastAttempt < _minReconnectionInterval)
            {
                var waitTime = _minReconnectionInterval - timeSinceLastAttempt;
                _logger.LogInformation("Rate limiting reconnection. Waiting {Seconds} seconds before retry...", waitTime.TotalSeconds);
                await Task.Delay(waitTime, token);
            }

            _lastReconnectionAttempt = DateTime.Now;

            try
            {
                _logger.LogInformation("Attempting to reconnect to Icecast...");

                // Clean up existing connection
                await DisconnectFromIcecastAsync();

                // Brief delay before reconnection
                await Task.Delay(2000, token);

                // Reconnect
                await ConnectToIcecastAsync();

                _logger.LogInformation("Successfully reconnected to Icecast");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reconnect to Icecast");
                throw;
            }
        }

        private Task AudioProcessingLoopAsync()
        {
            // This could be used for advanced audio processing like crossfading
            // For now, we'll keep it simple
            return Task.Run(async () =>
            {
                while (!_mainCts.Token.IsCancellationRequested && !_disposed)
                {
                    try
                    {
                        await Task.Delay(100, _mainCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }, _mainCts.Token);
        }

        private string SanitizeFileName(string fileName)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            foreach (var c in invalidChars)
            {
                fileName = fileName.Replace(c, '_');
            }
            return fileName;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _logger.LogInformation("=== Disposing NAudio Streaming Service ===");

            _mainCts?.Cancel();

            try
            {
                _streamingTask?.Wait(TimeSpan.FromSeconds(10));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error waiting for streaming task");
            }

            _mp3Writer?.Dispose();
            _bufferedWaveProvider?.ClearBuffer();

            _mainCts?.Dispose();
            _queueSignal.Dispose();
            _queue.Clear();
            _songStatus.Clear();
            _downloadedFiles.Clear();

            _logger.LogInformation("=== NAudio Streaming Service Disposed ===");
        }
    }
}