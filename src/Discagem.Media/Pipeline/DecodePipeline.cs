using System.Collections.Concurrent;
using System.Diagnostics;
using Discagem.Media.Imaging;
using Discagem.Media.Rtp;
using Discagem.Media.Vpx;

namespace Discagem.Media.Pipeline;

/// <summary>Live statistics of an incoming stream.</summary>
public sealed record ViewStats(int Width, int Height, double FramesPerSecond, double ReceivedKbps);

/// <summary>Decodes incoming frames on a dedicated thread.</summary>
public sealed class DecodePipeline : IDisposable
{
    private readonly BlockingCollection<AssembledFrame> _queue = new(new ConcurrentQueue<AssembledFrame>(), 240);
    private readonly Thread _thread;
    private readonly Vp8Decoder _decoder = new();
    private BgraFrame? _frame;
    private volatile bool _stopped;

    public DecodePipeline()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Discagem decoder" };
        _thread.Start();
    }

    /// <summary>
    /// Raised on the decoder thread with the decoded picture. The buffer is reused for the next
    /// frame, so handlers must copy what they need before returning.
    /// </summary>
    public event Action<BgraFrame>? FrameDecoded;

    /// <summary>Raised when a frame could not be decoded (a key frame should be requested).</summary>
    public event Action? DecodeError;

    /// <summary>Raised about once per second.</summary>
    public event Action<ViewStats>? StatsUpdated;

    public long FramesDecoded { get; private set; }

    public void Enqueue(AssembledFrame frame)
    {
        if (_stopped)
        {
            return;
        }

        try
        {
            if (!_queue.TryAdd(frame))
            {
                // Hopelessly behind: start over from the next key frame.
                while (_queue.TryTake(out _))
                {
                }

                DecodeError?.Invoke();
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            // Raced with Dispose.
        }
    }

    private void Run()
    {
        var clock = Stopwatch.StartNew();
        var windowStart = 0.0;
        var framesInWindow = 0;
        long bytesInWindow = 0;
        var waitingForKeyFrame = false;

        try
        {
            foreach (var frame in _queue.GetConsumingEnumerable())
            {
                if (_stopped)
                {
                    break;
                }

                if (waitingForKeyFrame && !frame.IsKeyFrame)
                {
                    continue;
                }

                waitingForKeyFrame = false;
                bytesInWindow += frame.Data.Length;
                if (_decoder.Decode(frame.Data, ref _frame) && _frame is not null)
                {
                    FramesDecoded++;
                    framesInWindow++;
                    FrameDecoded?.Invoke(_frame);
                }
                else
                {
                    waitingForKeyFrame = true;
                    DecodeError?.Invoke();
                }

                var seconds = clock.Elapsed.TotalSeconds;
                if (seconds - windowStart >= 1.0)
                {
                    var elapsed = seconds - windowStart;
                    StatsUpdated?.Invoke(new ViewStats(
                        _frame?.Width ?? 0,
                        _frame?.Height ?? 0,
                        framesInWindow / elapsed,
                        bytesInWindow * 8 / 1000.0 / elapsed));
                    windowStart = seconds;
                    framesInWindow = 0;
                    bytesInWindow = 0;
                }
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    public void Dispose()
    {
        _stopped = true;
        _queue.CompleteAdding();
        if (Thread.CurrentThread != _thread)
        {
            _thread.Join(TimeSpan.FromSeconds(2));
        }

        _decoder.Dispose();
        _queue.Dispose();
    }
}
