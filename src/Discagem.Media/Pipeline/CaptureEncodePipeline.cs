using System.Diagnostics;
using System.Runtime.InteropServices;
using Discagem.Media.Imaging;
using Discagem.Media.Vpx;
using Discagem.Media.WebRtc;

namespace Discagem.Media.Pipeline;

/// <summary>Live statistics of an outgoing stream.</summary>
public sealed record PublishStats(int Width, int Height, double FramesPerSecond, int TargetKbps, double SentKbps, double LossPercent, double EncodeMs);

/// <summary>
/// Captures frames at a fixed rate, scales and converts them to I420, encodes them with VP8 and hands
/// them to the <see cref="ScreenSharePublisher"/>. Also adapts the bitrate to the observed loss.
/// The pipeline owns the frame source and disposes it once the capture thread has exited.
/// </summary>
public sealed unsafe class CaptureEncodePipeline : IDisposable
{
    private readonly IFrameSource _source;
    private readonly ScreenSharePublisher _publisher;
    private readonly StreamQuality _quality;
    private readonly Thread _thread;
    private readonly BgraScaler _scaler = new();
    private volatile bool _stopped;
    private volatile bool _keyFrameRequested;
    private Vp8Encoder? _encoder;
    private byte* _scaled;
    private nuint _scaledSize;
    private int _targetKbps;
    private int _currentKbps;
    private int _released;

    public CaptureEncodePipeline(IFrameSource source, ScreenSharePublisher publisher, StreamQuality quality)
    {
        _source = source;
        _publisher = publisher;
        _quality = quality;
        _publisher.KeyFrameRequested += () => _keyFrameRequested = true;
        _thread = new Thread(Run) { IsBackground = true, Name = "Discagem capture", Priority = ThreadPriority.AboveNormal };
    }

    /// <summary>Raised when the pipeline stops because of an unrecoverable error.</summary>
    public event Action<string>? Failed;

    /// <summary>Raised about once per second with fresh statistics.</summary>
    public event Action<PublishStats>? StatsUpdated;

    /// <summary>The encoded resolution (0 until the first frame).</summary>
    public (int Width, int Height) EncodedSize { get; private set; }

    public void Start() => _thread.Start();

    private void Run()
    {
        try
        {
            RunLoop();
        }
        catch (Exception ex)
        {
            if (!_stopped)
            {
                Failed?.Invoke(ex.Message);
            }
        }
        finally
        {
            _encoder?.Dispose();
            _encoder = null;
            ReleaseResources();
        }
    }

    private void RunLoop()
    {
        var clock = Stopwatch.StartNew();
        var interval = 1000.0 / Math.Clamp(_quality.FrameRate, 1, 120);
        var nextFrameAt = 0.0;
        var statsWindowStart = clock.Elapsed.TotalSeconds;
        var framesInWindow = 0;
        var encodeMsInWindow = 0.0;
        var lastBytesSent = 0L;
        var lastPacketsSent = 0L;
        var lastNacked = 0L;
        var cleanSeconds = 0;

        while (!_stopped)
        {
            var now = clock.Elapsed.TotalMilliseconds;
            if (now < nextFrameAt)
            {
                var wait = nextFrameAt - now;
                if (wait > 2)
                {
                    Thread.Sleep((int)(wait - 1));
                }
                else
                {
                    Thread.Yield();
                }

                continue;
            }

            // If we fell behind by more than a frame, resynchronise instead of bursting.
            nextFrameAt = now - nextFrameAt > interval ? now + interval : nextFrameAt + interval;

            if (!_publisher.IsConnected)
            {
                continue;
            }

            var started = clock.Elapsed.TotalMilliseconds;
            if (!EncodeOne((long)started))
            {
                continue;
            }

            framesInWindow++;
            encodeMsInWindow += clock.Elapsed.TotalMilliseconds - started;

            var seconds = clock.Elapsed.TotalSeconds;
            if (seconds - statsWindowStart >= 1.0)
            {
                var elapsed = seconds - statsWindowStart;
                var bytes = _publisher.BytesSent;
                var packets = _publisher.PacketsSent;
                var nacked = _publisher.NackedPackets;
                var packetDelta = Math.Max(1, packets - lastPacketsSent);
                var loss = Math.Min(100.0, (nacked - lastNacked) * 100.0 / packetDelta);
                AdaptBitrate(loss, ref cleanSeconds);

                StatsUpdated?.Invoke(new PublishStats(
                    EncodedSize.Width,
                    EncodedSize.Height,
                    framesInWindow / elapsed,
                    _currentKbps,
                    (bytes - lastBytesSent) * 8 / 1000.0 / elapsed,
                    loss,
                    framesInWindow > 0 ? encodeMsInWindow / framesInWindow : 0));

                lastBytesSent = bytes;
                lastPacketsSent = packets;
                lastNacked = nacked;
                framesInWindow = 0;
                encodeMsInWindow = 0;
                statsWindowStart = seconds;
            }
        }
    }

    private bool EncodeOne(long timestampMs)
    {
        if (!_source.TryCapture(out var frame) || frame.Width < 16 || frame.Height < 16)
        {
            return false;
        }

        var (width, height) = BgraScaler.FitWithin(frame.Width, frame.Height, _quality.MaxHeight);
        if (_encoder is null || _encoder.Width != width || _encoder.Height != height)
        {
            _encoder?.Dispose();
            _targetKbps = _quality.BitrateKbpsFor(width, height);
            _currentKbps = _targetKbps;
            _encoder = new Vp8Encoder(new Vp8EncoderOptions(width, height, _quality.FrameRate, _currentKbps));
            _publisher.SetBitrate(_currentKbps);
            EncodedSize = (width, height);
        }

        fixed (byte* i420 = _encoder.InputBuffer)
        {
            if (width == frame.Width && height == frame.Height)
            {
                PixelConverter.BgraToI420((byte*)frame.Data, frame.Stride, width, height, i420);
            }
            else
            {
                var needed = (nuint)width * (nuint)height * 4;
                if (_scaledSize < needed)
                {
                    if (_scaled != null)
                    {
                        NativeMemory.Free(_scaled);
                    }

                    _scaled = (byte*)NativeMemory.Alloc(needed);
                    _scaledSize = needed;
                }

                _scaler.Scale((byte*)frame.Data, frame.Width, frame.Height, frame.Stride, _scaled, width, height, width * 4);
                PixelConverter.BgraToI420(_scaled, width * 4, width, height, i420);
            }
        }

        if (_keyFrameRequested)
        {
            _keyFrameRequested = false;
            _encoder.RequestKeyFrame();
        }

        var encoded = _encoder.Encode(timestampMs);
        if (encoded is null)
        {
            return true;
        }

        var rtpTimestamp = (uint)(timestampMs * (DiscagemVideo.ClockRate / 1000));
        _publisher.SendFrame(encoded.Value.Data, rtpTimestamp, encoded.Value.IsKeyFrame);
        return true;
    }

    /// <summary>Simple loss-based rate control: back off quickly under loss, recover slowly.</summary>
    private void AdaptBitrate(double lossPercent, ref int cleanSeconds)
    {
        if (_encoder is null)
        {
            return;
        }

        var next = _currentKbps;
        if (lossPercent > 10)
        {
            next = (int)(_currentKbps * 0.75);
            cleanSeconds = 0;
        }
        else if (lossPercent > 4)
        {
            next = (int)(_currentKbps * 0.9);
            cleanSeconds = 0;
        }
        else if (lossPercent < 1.5 && ++cleanSeconds >= 4)
        {
            next = (int)(_currentKbps * 1.1);
        }

        next = Math.Clamp(next, Math.Max(400, _targetKbps / 4), _targetKbps);
        if (next != _currentKbps)
        {
            _currentKbps = next;
            _encoder.SetBitrate(next);
            _publisher.SetBitrate(next);
        }
    }

    /// <summary>Frees the scaling buffers and the frame source. Only called once nothing can touch them any more.</summary>
    private void ReleaseResources()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0)
        {
            return;
        }

        _scaler.Dispose();
        if (_scaled != null)
        {
            NativeMemory.Free(_scaled);
            _scaled = null;
            _scaledSize = 0;
        }

        _source.Dispose();
    }

    public void Dispose()
    {
        _stopped = true;
        if ((_thread.ThreadState & System.Threading.ThreadState.Unstarted) != 0)
        {
            ReleaseResources();
            return;
        }

        // A capture or encode call can block for a while (a hung window, a stalled X server). If the
        // thread does not stop in time it releases the resources itself when that call returns.
        if (Thread.CurrentThread != _thread)
        {
            _thread.Join(TimeSpan.FromSeconds(2));
        }
    }
}
