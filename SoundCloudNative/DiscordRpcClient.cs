using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SoundCloudNative
{
    public class DiscordRpcClient : IDisposable
    {
        private const string ApplicationId = "802958833214423081";
        private NamedPipeClientStream? _pipe;
        private BinaryWriter? _writer;
        private BinaryReader? _reader;
        private CancellationTokenSource? _cts;
        private readonly object _lock = new object();
        private bool _isConnected;
        private string? _lastTitle;
        private string? _lastArtist;
        private string? _lastArtwork;
        private double _lastDuration;
        private double _lastCurrent;
        private bool _lastPlaying;
        private bool _hasLastActivity;

        public void Initialize()
        {
            _cts = new CancellationTokenSource();
            Task.Run(() => WorkerLoop(_cts.Token));
        }

        private async Task WorkerLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                if (!_isConnected)
                {
                    TryConnect();
                    if (_isConnected && _hasLastActivity)
                    {
                        // Restore last known activity on reconnect
                        UpdateActivity(_lastTitle ?? "", _lastArtist ?? "", _lastArtwork, _lastDuration, _lastCurrent, _lastPlaying);
                    }
                }
                else
                {
                    // Ping/check pipe status by reading incoming frame if any
                    try
                    {
                        if (_pipe != null && _reader != null && _pipe.IsConnected)
                        {
                            // Drain any incoming response frames without blocking
                            while (_pipe.CanRead && _pipe.DataAvailable())
                            {
                                int op = _reader.ReadInt32();
                                int len = _reader.ReadInt32();
                                if (len > 0)
                                {
                                    _reader.ReadBytes(len);
                                }
                            }
                        }
                    }
                    catch
                    {
                        ClosePipe();
                    }
                }

                try
                {
                    await Task.Delay(4000, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private void TryConnect()
        {
            lock (_lock)
            {
                for (int i = 0; i < 10; i++)
                {
                    try
                    {
                        var pipe = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
                        pipe.Connect(500);

                        var writer = new BinaryWriter(pipe);
                        var reader = new BinaryReader(pipe);

                        // Handshake
                        var handshake = JsonSerializer.Serialize(new { v = 1, client_id = ApplicationId });
                        byte[] hsBytes = Encoding.UTF8.GetBytes(handshake);
                        writer.Write((int)0);
                        writer.Write(hsBytes.Length);
                        writer.Write(hsBytes);
                        writer.Flush();

                        int respOp = reader.ReadInt32();
                        int respLen = reader.ReadInt32();
                        if (respLen > 0)
                        {
                            reader.ReadBytes(respLen);
                        }

                        _pipe = pipe;
                        _writer = writer;
                        _reader = reader;
                        _isConnected = true;
                        Debug.WriteLine($"[DiscordRPC] Connected on discord-ipc-{i}");
                        return;
                    }
                    catch
                    {
                        // Try next pipe index
                    }
                }
            }
        }

        public void UpdateActivity(string title, string artist, string? artworkUrl, double durationSec, double currentSec, bool isPlaying)
        {
            _lastTitle = title;
            _lastArtist = artist;
            _lastArtwork = artworkUrl;
            _lastDuration = durationSec;
            _lastCurrent = currentSec;
            _lastPlaying = isPlaying;
            _hasLastActivity = true;

            if (!_isConnected || _writer == null || _pipe == null || !_pipe.IsConnected) return;

            try
            {
                lock (_lock)
                {
                    long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    object? timestamps = null;

                    string stateText = string.IsNullOrWhiteSpace(artist) ? "SoundCloud" : Truncate(artist, 120);
                    string detailsText = string.IsNullOrWhiteSpace(title) ? "SoundClode" : Truncate(title, 120);

                    if (isPlaying)
                    {
                        long start = Math.Max(0, now - (long)currentSec);
                        long end = durationSec > 0 ? (start + (long)durationSec) : 0;
                        timestamps = end > start ? (object)new { start = start, end = end } : (object)new { start = start };
                    }
                    else
                    {
                        stateText = "На паузе • " + stateText;
                    }

                    string largeImg = !string.IsNullOrWhiteSpace(artworkUrl) && artworkUrl.StartsWith("http")
                        ? artworkUrl
                        : "https://cdn.rcd.gg/PreMiD/websites/S/SoundCloud/assets/logo.png";

                    var activityObj = new
                    {
                        details = detailsText,
                        state = stateText,
                        timestamps = timestamps,
                        assets = new
                        {
                            large_image = largeImg,
                            large_text = detailsText,
                            small_image = "https://cdn.rcd.gg/PreMiD/websites/S/SoundCloud/assets/logo.png",
                            small_text = isPlaying ? "Слушает в SoundClode" : "Пауза"
                        },
                        instance = true
                    };

                    var payload = JsonSerializer.Serialize(new
                    {
                        cmd = "SET_ACTIVITY",
                        args = new
                        {
                            pid = Environment.ProcessId,
                            activity = activityObj
                        },
                        nonce = Guid.NewGuid().ToString()
                    });

                    byte[] actBytes = Encoding.UTF8.GetBytes(payload);
                    _writer.Write((int)1);
                    _writer.Write(actBytes.Length);
                    _writer.Write(actBytes);
                    _writer.Flush();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DiscordRPC] Send error: " + ex.Message);
                ClosePipe();
            }
        }

        public void ClearActivity()
        {
            _hasLastActivity = false;
            if (!_isConnected || _writer == null || _pipe == null || !_pipe.IsConnected) return;

            try
            {
                lock (_lock)
                {
                    var payload = JsonSerializer.Serialize(new
                    {
                        cmd = "SET_ACTIVITY",
                        args = new
                        {
                            pid = Environment.ProcessId,
                            activity = (object?)null
                        },
                        nonce = Guid.NewGuid().ToString()
                    });

                    byte[] actBytes = Encoding.UTF8.GetBytes(payload);
                    _writer.Write((int)1);
                    _writer.Write(actBytes.Length);
                    _writer.Write(actBytes);
                    _writer.Flush();
                }
            }
            catch
            {
                ClosePipe();
            }
        }

        private void ClosePipe()
        {
            lock (_lock)
            {
                _isConnected = false;
                try { _writer?.Dispose(); } catch { }
                try { _reader?.Dispose(); } catch { }
                try { _pipe?.Dispose(); } catch { }
                _writer = null;
                _reader = null;
                _pipe = null;
            }
        }

        private static string Truncate(string str, int maxLen)
        {
            if (string.IsNullOrEmpty(str)) return "";
            return str.Length <= maxLen ? str : str.Substring(0, maxLen - 1) + "…";
        }

        public void Dispose()
        {
            _cts?.Cancel();
            ClosePipe();
        }
    }

    internal static class PipeExtensions
    {
        public static bool DataAvailable(this NamedPipeClientStream pipe)
        {
            try
            {
                return pipe.InBufferSize > 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
