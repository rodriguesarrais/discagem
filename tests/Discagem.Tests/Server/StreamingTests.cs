using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Discagem.Media.Pipeline;
using Discagem.Media.WebRtc;
using Discagem.Shared;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace Discagem.Tests.Server;

/// <summary>A moving test pattern. The top-left 64x64 block is a fixed colour that viewers can verify.</summary>
internal sealed unsafe class SyntheticFrameSource : IFrameSource
{
    private readonly int _width;
    private readonly int _height;
    private readonly byte* _buffer;
    private int _frame;

    public SyntheticFrameSource(int width, int height)
    {
        _width = width;
        _height = height;
        _buffer = (byte*)NativeMemory.AllocZeroed((nuint)(width * height * 4));
    }

    public string Name => "Synthetic";

    public bool TryCapture(out CapturedFrame frame)
    {
        var bar = (_frame++ * 8) % _width;
        for (var y = 0; y < _height; y++)
        {
            var row = _buffer + (long)y * _width * 4;
            for (var x = 0; x < _width; x++)
            {
                var p = row + x * 4;
                if (x < 64 && y < 64)
                {
                    p[0] = 200; p[1] = 40; p[2] = 30; // BGR: a strong blue
                }
                else if (Math.Abs(x - bar) < 20)
                {
                    p[0] = 255; p[1] = 255; p[2] = 255;
                }
                else
                {
                    p[0] = (byte)(x / 5); p[1] = (byte)(y / 3); p[2] = 60;
                }

                p[3] = 255;
            }
        }

        frame = new CapturedFrame((IntPtr)_buffer, _width, _height, _width * 4);
        return true;
    }

    public void Dispose() => NativeMemory.Free(_buffer);
}

public sealed class StreamingTests(Xunit.Abstractions.ITestOutputHelper output) : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private readonly DiscagemFactory _factory = new();
    private HubConnection _alice = null!;
    private HubConnection _bob = null!;

    public async Task InitializeAsync()
    {
        var (_, alice) = await _factory.RegisterAsync("alice");
        var (_, bob) = await _factory.RegisterAsync("bob");
        _alice = await _factory.ConnectHubAsync(alice.Token);
        _bob = await _factory.ConnectHubAsync(bob.Token);
    }

    public async Task DisposeAsync()
    {
        await _alice.DisposeAsync();
        await _bob.DisposeAsync();
        _factory.Dispose();
    }

    private static async Task WaitUntil(Func<bool> condition, string description)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"Timed out waiting for: {description}");
            }

            await Task.Delay(50);
        }
    }

    private sealed class StreamHarness : IDisposable
    {
        public required ScreenSharePublisher Publisher { get; init; }
        public required CaptureEncodePipeline Pipeline { get; init; }
        public required SyntheticFrameSource Source { get; init; }
        public required string StreamId { get; init; }

        public void Dispose()
        {
            // The pipeline owns (and disposes) the source.
            Pipeline.Dispose();
            Publisher.Dispose();
        }
    }

    private async Task<StreamHarness> StartPublishingAsync(Func<Discagem.Media.Rtp.VideoPacket, bool>? dropFilter = null)
    {
        var publisher = new ScreenSharePublisher([], 2500) { DropFilter = dropFilter };
        var offer = await publisher.CreateOfferAsync();
        var answer = await _alice.InvokeAsync<MediaSessionAnswer>(nameof(IChatHub.StartStream), new StartStreamRequest(offer, "Test stream", 1280, 720, 30));
        publisher.ApplyAnswer(answer.AnswerSdp);
        Assert.True(await publisher.WaitForConnectionAsync(Timeout), "publisher did not connect");

        var source = new SyntheticFrameSource(1280, 720);
        var pipeline = new CaptureEncodePipeline(source, publisher, new StreamQuality("test", 720, 30));
        pipeline.Start();
        return new StreamHarness { Publisher = publisher, Pipeline = pipeline, Source = source, StreamId = answer.StreamId };
    }

    private sealed class ViewerHarness : IDisposable
    {
        private int _frames;
        private int _correctColour;
        public int KeyFrames;
        public int DecodeErrors;

        public required ScreenShareViewer Viewer { get; init; }
        public required DecodePipeline Decoder { get; init; }
        public required string SessionId { get; init; }

        public int Frames => Volatile.Read(ref _frames);

        public int CorrectColour => Volatile.Read(ref _correctColour);

        public void OnFrame(Discagem.Media.Imaging.BgraFrame frame)
        {
            if (frame.Width != 1280 || frame.Height != 720)
            {
                return;
            }

            Interlocked.Increment(ref _frames);
            var i = (16 * frame.Width + 16) * 4;
            if (Math.Abs(frame.Data[i] - 200) < 24 && Math.Abs(frame.Data[i + 1] - 40) < 24 && Math.Abs(frame.Data[i + 2] - 30) < 24)
            {
                Interlocked.Increment(ref _correctColour);
            }
        }

        public void Dispose()
        {
            Viewer.Dispose();
            Decoder.Dispose();
        }
    }

    private async Task<ViewerHarness> WatchAsync(HubConnection hub, string streamId)
    {
        var viewer = new ScreenShareViewer([]);
        var decoder = new DecodePipeline();
        viewer.FrameReceived += decoder.Enqueue;
        decoder.DecodeError += viewer.ReportDecodeError;

        var offer = await viewer.CreateOfferAsync();
        var answer = await hub.InvokeAsync<MediaSessionAnswer>(nameof(IChatHub.WatchStream), streamId, offer);
        var harness = new ViewerHarness { Viewer = viewer, Decoder = decoder, SessionId = answer.SessionId };
        decoder.FrameDecoded += harness.OnFrame;
        viewer.FrameReceived += f => { if (f.IsKeyFrame) Interlocked.Increment(ref harness.KeyFrames); };
        decoder.DecodeError += () => Interlocked.Increment(ref harness.DecodeErrors);
        viewer.ApplyAnswer(answer.AnswerSdp);
        Assert.True(await viewer.WaitForConnectionAsync(Timeout), "viewer did not connect");
        return harness;
    }

    [Fact]
    public async Task ScreenShare_IsForwardedToViewers()
    {
        var started = new TaskCompletionSource<StreamInfoDto>();
        _bob.On<StreamInfoDto>(nameof(IChatClient.StreamStarted), s => started.TrySetResult(s));

        using var stream = await StartPublishingAsync();
        var info = await started.Task.WaitAsync(Timeout);
        Assert.Equal(stream.StreamId, info.StreamId);
        Assert.Equal("Test stream", info.Title);

        using var viewer = await WatchAsync(_bob, stream.StreamId);
        await WaitUntil(() => viewer.Frames >= 45, "45 decoded frames");

        Assert.True(viewer.CorrectColour >= viewer.Frames - 2, $"{viewer.CorrectColour} of {viewer.Frames} frames had the expected colour");
        Assert.Equal((1280, 720), stream.Pipeline.EncodedSize);
    }

    [Fact]
    public async Task ScreenShare_RecoversFromPacketLoss()
    {
        // Drop 5% of first transmissions on the uplink; retransmissions get through.
        var attempted = new ConcurrentDictionary<ushort, bool>();
        var random = new Random(1234);
        bool Drop(Discagem.Media.Rtp.VideoPacket packet)
        {
            if (!attempted.TryAdd(packet.MediaSeq, true))
            {
                return false;
            }

            lock (random)
            {
                return random.NextDouble() < 0.05;
            }
        }

        using var stream = await StartPublishingAsync(Drop);
        using var viewer = await WatchAsync(_bob, stream.StreamId);
        await WaitUntil(() => viewer.Frames >= 60, "60 decoded frames under loss");
        output.WriteLine($"frames={viewer.Frames} key={viewer.KeyFrames} decodeErrors={viewer.DecodeErrors} dropped={viewer.Viewer.FramesDropped} " +
            $"sent={stream.Publisher.PacketsSent} nacked={stream.Publisher.NackedPackets} retransmitted={stream.Publisher.PacketsRetransmitted} recv={viewer.Viewer.PacketsReceived} {viewer.Viewer.Diagnostics}");

        Assert.True(stream.Publisher.PacketsRetransmitted > 0, "expected retransmissions");
        // Without working retransmissions roughly every sixth frame would be lost at this loss rate.
        // A few drops are tolerated for overloaded CI machines, where the pacer may flush its queue.
        Assert.True(viewer.Viewer.FramesDropped <= 5, $"too many frames dropped: {viewer.Viewer.FramesDropped}");
        Assert.True(viewer.CorrectColour >= viewer.Frames - 3);
    }

    [Fact]
    public async Task StoppingStream_NotifiesViewers()
    {
        using var stream = await StartPublishingAsync();
        using var viewer = await WatchAsync(_bob, stream.StreamId);

        var updated = new TaskCompletionSource<StreamInfoDto>();
        var ended = new TaskCompletionSource<string>();
        _alice.On<StreamInfoDto>(nameof(IChatClient.StreamUpdated), s => updated.TrySetResult(s));
        _bob.On<string>(nameof(IChatClient.StreamEnded), id => ended.TrySetResult(id));

        var sync = await _bob.InvokeAsync<SyncStateDto>(nameof(IChatHub.Sync));
        Assert.Single(sync.Streams);
        Assert.Single(sync.Streams[0].ViewerIds);

        await _alice.InvokeAsync(nameof(IChatHub.StopStream));
        Assert.Equal(stream.StreamId, await ended.Task.WaitAsync(Timeout));

        sync = await _bob.InvokeAsync<SyncStateDto>(nameof(IChatHub.Sync));
        Assert.Empty(sync.Streams);

        await Assert.ThrowsAsync<HubException>(() => _bob.InvokeAsync<MediaSessionAnswer>(nameof(IChatHub.WatchStream), stream.StreamId, "v=0"));
    }

    [Fact]
    public async Task PublisherDisconnect_EndsStream()
    {
        using var stream = await StartPublishingAsync();
        var ended = new TaskCompletionSource<string>();
        _bob.On<string>(nameof(IChatClient.StreamEnded), id => ended.TrySetResult(id));

        await _alice.StopAsync();
        Assert.Equal(stream.StreamId, await ended.Task.WaitAsync(Timeout));
    }
}
