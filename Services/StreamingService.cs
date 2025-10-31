using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Google.Apis.Util;
using RadioStation.Models;
using YoutubeExplode;
using YoutubeExplode.Videos.Streams;
using Microsoft.Extensions.Logging;

namespace RadioStation.Services
{
    public class StreamingService
    {
        private readonly string _icecastMountpoint;
        private readonly string _icecastPassword;
        private readonly string _icecastUrl;
        private Process? _ffmpegProcess;
        private readonly object _lock = new object();
        private readonly ILogger<StreamingService> _logger;
        private static readonly Lazy<YoutubeClient> _youtubeClient = new Lazy<YoutubeClient>(() => new YoutubeClient());

        public StreamingService(string icecastUrl, string icecastMountpoint, string icecastPassword, ILogger<StreamingService> logger)
        {
            _icecastUrl = icecastUrl;
            _icecastMountpoint = icecastMountpoint;
            _icecastPassword = icecastPassword;
            _logger = logger;
            
            // Check if FFmpeg is available
            if (!CheckFFmpegAvailability())
            {
                throw new InvalidOperationException("FFmpeg is not available. Please ensure FFmpeg is installed and accessible in the system PATH.");
            }
        }

        private bool CheckFFmpegAvailability()
        {
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "ffmpeg",
                        Arguments = "-version",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                
                process.Start();
                process.WaitForExit(5000); // Wait up to 5 seconds
                return process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        public async Task<string> DownloadAudioAsync(string youtubeUrl)
        {
            _logger.LogInformation($"[StreamingService] Starting to download audio from: {youtubeUrl}");
            
            try
            {
                var youtube = _youtubeClient.Value;
                _logger.LogInformation($"[StreamingService] Using YoutubeClient from lazy initialization");
                
                // Get video ID from URL
                var videoId = YoutubeExplode.Videos.VideoId.Parse(youtubeUrl);
                _logger.LogInformation($"[StreamingService] Parsed video ID: {videoId}");
                
                // Get stream manifest
                var streamManifest = await youtube.Videos.Streams.GetManifestAsync(videoId);
                _logger.LogInformation($"[StreamingService] Got stream manifest");
                
                // Get the best audio stream
                var audioStreamInfo = streamManifest.GetAudioOnlyStreams().GetWithHighestBitrate();
                if (audioStreamInfo == null)
                {
                    _logger.LogError($"[StreamingService] No audio streams found");
                    throw new InvalidOperationException("No audio streams found for this video");
                }
                _logger.LogInformation($"[StreamingService] Found audio stream with bitrate: {audioStreamInfo.Bitrate}");
                
                // Create output directory if it doesn't exist
                var outputDir = Path.Combine(Directory.GetCurrentDirectory(), "downloads");
                Directory.CreateDirectory(outputDir);
                
                var outputFile = Path.Combine(outputDir, $"{videoId}.mp3");
                
                // Download the audio stream
                _logger.LogInformation($"[StreamingService] Downloading audio stream to: {outputFile}");
                await youtube.Videos.Streams.DownloadAsync(audioStreamInfo, outputFile);
                _logger.LogInformation($"[StreamingService] Audio download completed");
                
                return outputFile;
            }
            catch (Exception ex)
            {
                _logger.LogError($"[StreamingService] Error downloading audio: {ex.Message}");
                _logger.LogError($"[StreamingService] Stack trace: {ex.StackTrace}");
                if (ex.InnerException != null)
                {
                    _logger.LogError($"[StreamingService] Inner exception: {ex.InnerException.Message}");
                }
                throw new InvalidOperationException($"Failed to download audio from {youtubeUrl}: {ex.Message}", ex);
            }
        }

        public bool IsStreaming()
        {
            lock (_lock)
            {
                return _ffmpegProcess != null && !_ffmpegProcess.HasExited;
            }
        }

        public void StartStreaming(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                throw new ArgumentNullException(nameof(filePath), "File path cannot be null or empty");
            }

            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException("Audio file not found", filePath);
            }

            lock (_lock)
            {
                StopStreaming(); // Ensure any existing stream is stopped

                var ffmpegArgs = $"-re -i \"{filePath}\" -vn -c:a libmp3lame -b:a 128k " +
                  $"-f mp3 " +
                  $"-content_type audio/mpeg " +
                  $"-metadata title=\"Live song requests\" " +
                  $"icecast://source:{_icecastPassword}@{_icecastUrl}/{_icecastMountpoint}";

                _ffmpegProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "ffmpeg",
                        Arguments = ffmpegArgs,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardInput = true
                    },
                    EnableRaisingEvents = true
                };

                var errorOutput = new StringBuilder();
                _ffmpegProcess.ErrorDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        errorOutput.AppendLine(e.Data);
                    }
                };

                _ffmpegProcess.Exited += (sender, e) =>
                {
                    if (_ffmpegProcess.ExitCode != 0)
                    {
                        // Log the error output
                        throw new Exception($"FFmpeg process exited with code {_ffmpegProcess.ExitCode}. Error: {errorOutput}");
                    }
                    CleanupSong(filePath);
                };

                try
                {
                    _ffmpegProcess.Start();
                    _ffmpegProcess.BeginErrorReadLine(); // Start asynchronous reading of the error output
                }
                catch (Exception ex)
                {
                    throw new Exception($"Failed to start FFmpeg process: {ex.Message}", ex);
                }
            }
        }

        public void StopStreaming()
        {
            lock (_lock)
            {
                if (_ffmpegProcess != null && !_ffmpegProcess.HasExited)
                {
                    try
                    {
                        _ffmpegProcess.StandardInput.WriteLine("q"); // Send 'q' to quit ffmpeg gracefully
                        if (!_ffmpegProcess.WaitForExit(5000)) // Wait up to 5 seconds
                        {
                            _ffmpegProcess.Kill(); // Force kill if it doesn't exit gracefully
                        }
                    }
                    catch
                    {
                        // If anything goes wrong, make sure the process is killed
                        try { _ffmpegProcess?.Kill(); } catch { }
                    }
                    _ffmpegProcess = default;
                }
            }
        }

        public void CleanupSong(string filePath)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
            catch
            {
                // Ignore errors during cleanup
            }
        }
    }
}
