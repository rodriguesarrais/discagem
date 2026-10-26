using Discagem.Media.Rtp;

namespace Discagem.Tests.Media;

public sealed class RtpTests
{
    private static byte[] MakeFrame(int size, byte seed)
    {
        var data = new byte[size];
        for (var i = 0; i < size; i++)
        {
            data[i] = (byte)(seed + i * 7);
        }

        return data;
    }

    [Fact]
    public void Packetizer_SplitsAndFlagsFrames()
    {
        var packetizer = new VideoPacketizer(65534);
        var frame = MakeFrame(VideoPayload.MaxFragmentSize * 2 + 10, 1);
        var packets = packetizer.Packetize(frame, 1234, isKeyFrame: true);

        Assert.Equal(3, packets.Count);
        Assert.Equal(new ushort[] { 65534, 65535, 0 }, packets.Select(p => p.MediaSeq));
        Assert.True(packets[0].IsFrameStart);
        Assert.False(packets[1].IsFrameStart);
        Assert.True(packets[2].IsFrameEnd);
        Assert.All(packets, p => Assert.True(p.IsKeyFrame));
        Assert.All(packets, p => Assert.True(p.Payload.Length <= VideoPayload.HeaderSize + VideoPayload.MaxFragmentSize));
        Assert.Equal(0x10, packets[0].Payload[0]); // VP8 descriptor S bit.
        Assert.Equal(frame, packets.SelectMany(p => p.Fragment.ToArray()));

        var parsed = VideoPacket.Parse(packets[1].Payload, 1234);
        Assert.NotNull(parsed);
        Assert.Equal(packets[1].MediaSeq, parsed.MediaSeq);
        Assert.Equal(packets[1].Flags, parsed.Flags);
    }

    [Fact]
    public void Assembler_ReordersPackets()
    {
        var packetizer = new VideoPacketizer(100);
        var key = MakeFrame(3000, 1);
        var delta = MakeFrame(2500, 2);
        var packets = packetizer.Packetize(key, 0, true).Concat(packetizer.Packetize(delta, 3000, false)).ToList();

        var assembler = new FrameAssembler();
        var output = new List<AssembledFrame>();
        foreach (var index in new[] { 1, 0, 3, 2, 5, 4 })
        {
            assembler.Insert(packets[index], 0, output);
        }

        Assert.Equal(2, output.Count);
        Assert.Equal(key, output[0].Data);
        Assert.True(output[0].IsKeyFrame);
        Assert.Equal(delta, output[1].Data);
        Assert.False(output[1].IsKeyFrame);
    }

    [Fact]
    public void Assembler_WaitsForInitialKeyFrame()
    {
        var packetizer = new VideoPacketizer(10);
        var delta = packetizer.Packetize(MakeFrame(500, 1), 0, false);
        var key = packetizer.Packetize(MakeFrame(500, 2), 3000, true);

        var assembler = new FrameAssembler();
        var output = new List<AssembledFrame>();
        assembler.Insert(delta[0], 0, output);
        Assert.Empty(output);
        Assert.True(assembler.ShouldRequestKeyFrame(0));
        Assert.False(assembler.ShouldRequestKeyFrame(10));

        assembler.Insert(key[0], 20, output);
        Assert.Single(output);
        Assert.True(output[0].IsKeyFrame);
        Assert.False(assembler.IsWaitingForKeyFrame);
    }

    [Fact]
    public void Assembler_RecoversWithRetransmission()
    {
        var packetizer = new VideoPacketizer(500);
        var packets = packetizer.Packetize(MakeFrame(3000, 1), 0, true)
            .Concat(packetizer.Packetize(MakeFrame(3000, 2), 3000, false))
            .ToList();

        var assembler = new FrameAssembler();
        var nacks = new NackTracker { InitialDelayMs = 0 };
        var output = new List<AssembledFrame>();

        // Packet 4 is lost on the first attempt.
        foreach (var (packet, i) in packets.Select((p, i) => (p, i)))
        {
            if (i == 4)
            {
                continue;
            }

            nacks.OnPacket(packet.MediaSeq, 0);
            assembler.Insert(packet, 0, output);
        }

        Assert.Single(output);
        var requested = nacks.CollectNacks(10);
        Assert.Equal(new[] { packets[4].MediaSeq }, requested);

        nacks.OnPacket(packets[4].MediaSeq, 20);
        assembler.Insert(packets[4], 20, output);
        Assert.Equal(2, output.Count);
        Assert.Empty(nacks.CollectNacks(500));
    }

    [Fact]
    public void Assembler_GivesUpAndWaitsForNextKeyFrame()
    {
        var packetizer = new VideoPacketizer(1);
        var key1 = packetizer.Packetize(MakeFrame(2000, 1), 0, true);
        var delta = packetizer.Packetize(MakeFrame(2000, 2), 3000, false);
        var delta2 = packetizer.Packetize(MakeFrame(2000, 3), 6000, false);
        var key2 = packetizer.Packetize(MakeFrame(2000, 4), 9000, true);

        var assembler = new FrameAssembler { GiveUpMs = 100 };
        var output = new List<AssembledFrame>();
        foreach (var p in key1)
        {
            assembler.Insert(p, 0, output);
        }

        assembler.Insert(delta[1], 0, output); // delta[0] lost forever
        foreach (var p in delta2)
        {
            assembler.Insert(p, 10, output);
        }

        Assert.Single(output);
        assembler.Tick(50, output);
        Assert.False(assembler.IsWaitingForKeyFrame);
        assembler.Tick(200, output);
        Assert.True(assembler.IsWaitingForKeyFrame);
        Assert.True(assembler.ShouldRequestKeyFrame(200));

        foreach (var p in key2)
        {
            assembler.Insert(p, 250, output);
        }

        Assert.Equal(2, output.Count);
        Assert.True(output[1].IsKeyFrame);
        Assert.Equal(9000u, output[1].Timestamp);
    }

    [Fact]
    public void Assembler_JumpsToCompleteKeyFrameWhenBlocked()
    {
        var packetizer = new VideoPacketizer(1);
        var key1 = packetizer.Packetize(MakeFrame(1000, 1), 0, true);
        var delta = packetizer.Packetize(MakeFrame(2000, 2), 3000, false);
        var key2 = packetizer.Packetize(MakeFrame(1000, 3), 6000, true);

        var assembler = new FrameAssembler();
        var output = new List<AssembledFrame>();
        assembler.Insert(key1[0], 0, output);
        assembler.Insert(delta[1], 0, output);
        assembler.Insert(key2[0], 0, output);

        Assert.Equal(2, output.Count);
        Assert.Equal(6000u, output[1].Timestamp);
        Assert.Equal(1, assembler.FramesDropped);

        // The stale delta packet that finally arrives is ignored.
        assembler.Insert(delta[0], 10, output);
        Assert.Equal(2, output.Count);
    }

    [Fact]
    public void Assembler_IgnoresDuplicates()
    {
        var packetizer = new VideoPacketizer(7);
        var key = packetizer.Packetize(MakeFrame(3000, 1), 0, true);
        var assembler = new FrameAssembler();
        var output = new List<AssembledFrame>();
        foreach (var p in key.Concat(key))
        {
            assembler.Insert(p, 0, output);
        }

        Assert.Single(output);
        Assert.Equal(0, assembler.BufferedPackets);
    }

    [Fact]
    public void NackTracker_RetriesAndExpires()
    {
        var tracker = new NackTracker { InitialDelayMs = 5, RetryIntervalMs = 50, MaxRetries = 2 };
        tracker.OnPacket(65533, 0);
        tracker.OnPacket(1, 0); // 65534, 65535 and 0 are missing (wraps around)

        Assert.Empty(tracker.CollectNacks(1));
        Assert.Equal(new ushort[] { 65534, 65535, 0 }, tracker.CollectNacks(10));
        Assert.Empty(tracker.CollectNacks(20));

        tracker.OnPacket(65535, 30);
        Assert.Equal(new ushort[] { 65534, 0 }, tracker.CollectNacks(70));
        Assert.Empty(tracker.CollectNacks(200));
        Assert.Equal(0, tracker.MissingCount);
    }

    [Fact]
    public void NackItems_RoundTrip()
    {
        ushort[] seqs = [65530, 65531, 65535, 3, 10, 40];
        var items = RtcpFeedback.BuildNackItems(seqs);
        // 65531..10 all fall within 16 packets of 65530; 40 needs its own item.
        Assert.Equal(2, items.Count);
        var expanded = items.SelectMany(i => RtcpFeedback.ExpandNack(i.Pid, i.Blp)).ToArray();
        Assert.Equal(seqs, expanded);
    }

    [Fact]
    public void PacketHistory_ReturnsOnlyMatchingSequence()
    {
        var history = new PacketHistory(8);
        var packetizer = new VideoPacketizer(0);
        var packets = packetizer.Packetize(MakeFrame(VideoPayload.MaxFragmentSize * 10, 1), 0, true);
        foreach (var p in packets)
        {
            history.Add(p);
        }

        Assert.True(history.TryGet(9, out var found));
        Assert.Equal(9, found.MediaSeq);
        Assert.False(history.TryGet(1, out _)); // overwritten by seq 9
    }

    [Fact]
    public void SequenceUnwrapper_HandlesWrapAndReorder()
    {
        var unwrapper = new SequenceUnwrapper();
        var a = unwrapper.Unwrap(65535);
        var b = unwrapper.Unwrap(0);
        var c = unwrapper.Unwrap(65534);
        Assert.Equal(a + 1, b);
        Assert.Equal(a - 1, c);
    }
}
