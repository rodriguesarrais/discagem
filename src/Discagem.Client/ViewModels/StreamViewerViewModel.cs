using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Discagem.Client.Services;
using Discagem.Media.Imaging;
using Discagem.Media.Pipeline;
using SIPSorcery.Net;

namespace Discagem.Client.ViewModels;

/// <summary>The stream currently being watched: owns the media session and the video surface.</summary>
public sealed partial class StreamViewerViewModel : ObservableObject, IDisposable
{
    private readonly object _sync = new();
    private WriteableBitmap? _bufferA;
    private WriteableBitmap? _bufferB;
    private WriteableBitmap? _displayed;
    private WriteableBitmap? _latest;
    private bool _presentPending;
    private WatchSession? _session;
    private bool _disposed;

    private readonly ClientSettings _settings;

    public StreamViewerViewModel(LiveStreamViewModel stream, ClientSettings settings)
    {
        Stream = stream;
        _settings = settings;
        _volumePercent = settings.StreamVolumes.TryGetValue(stream.Streamer.Id, out var volume) ? volume * 100 : 100;
    }

    public LiveStreamViewModel Stream { get; }

    public string StreamId => Stream.StreamId;

    /// <summary>Raised on the UI thread when the viewer should be closed (stream ended or user closed it).</summary>
    public event Action<StreamViewerViewModel, string?>? CloseRequested;

    /// <summary>
    /// Raised on the UI thread once the viewer is closed, whoever closed it (the stream ended, another
    /// stream was opened, the connection dropped), so that a pop-out window showing it closes too.
    /// </summary>
    public event Action<StreamViewerViewModel>? Closed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFrame))]
    private Bitmap? _frame;

    [ObservableProperty]
    private string _status = "Connecting…";

    [ObservableProperty]
    private string _statsText = "";

    [ObservableProperty]
    private bool _isPoppedOut;

    public bool HasFrame => Frame is not null;

    /// <summary>Stream volume, 0–200 %, remembered per streamer.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumeLabel))]
    private double _volumePercent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSilenced))]
    private bool _isAudioMuted;

    /// <summary>Deafening in voice also silences streams.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSilenced))]
    private bool _isDeafened;

    public bool IsSilenced => IsAudioMuted || IsDeafened;

    public string VolumeLabel => $"{VolumePercent:0}%";

    partial void OnVolumePercentChanged(double value)
    {
        _settings.StreamVolumes[Stream.Streamer.Id] = (float)(value / 100);
        _settings.Save();
        ApplyAudio();
    }

    partial void OnIsAudioMutedChanged(bool value) => ApplyAudio();

    partial void OnIsDeafenedChanged(bool value) => ApplyAudio();

    private void ApplyAudio()
    {
        if (_session is { } session)
        {
            session.Audio.Volume = (float)(VolumePercent / 100);
            session.Audio.IsMuted = IsSilenced;
        }
    }

    [RelayCommand]
    private void ToggleAudio() => IsAudioMuted = !IsAudioMuted;

    public void Attach(WatchSession session)
    {
        _session = session;
        ApplyAudio();
        session.Decoder.FrameDecoded += OnFrameDecoded;
        long lastAudioPackets = 0;
        session.Decoder.StatsUpdated += stats =>
        {
            // Whether sound arrived since the last update: tells "the streamer shares no sound" from "nothing is playing".
            var audioPackets = session.Audio.PacketsReceived;
            var sound = audioPackets > lastAudioPackets ? " · sound" : Stream.HasAudio ? " · no sound playing" : " · no sound shared";
            lastAudioPackets = audioPackets;
            Dispatcher.UIThread.Post(() =>
                StatsText = stats.Width > 0 ? $"{stats.Width}×{stats.Height} · {stats.FramesPerSecond:0} FPS · {stats.ReceivedKbps / 1000:0.0} Mbps{sound}" : "");
        };
        session.Viewer.StateChanged += state => Dispatcher.UIThread.Post(() =>
        {
            Status = state switch
            {
                RTCPeerConnectionState.connected => HasFrame ? "" : "Waiting for video…",
                RTCPeerConnectionState.connecting or RTCPeerConnectionState.@new => "Connecting…",
                RTCPeerConnectionState.disconnected => "Reconnecting…",
                RTCPeerConnectionState.failed => "Connection failed. Check that the server's UDP ports are reachable.",
                _ => Status,
            };
        });
        session.Closed += reason => Dispatcher.UIThread.Post(() => CloseRequested?.Invoke(this, $"Disconnected from the stream ({reason})."));
    }

    private void OnFrameDecoded(BgraFrame frame)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            var target = ReferenceEquals(_displayed, _bufferA) ? _bufferB : _bufferA;
            if (target is null || target.PixelSize.Width != frame.Width || target.PixelSize.Height != frame.Height)
            {
                target = new WriteableBitmap(new PixelSize(frame.Width, frame.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
                if (ReferenceEquals(_displayed, _bufferA))
                {
                    _bufferB = target;
                }
                else
                {
                    _bufferA = target;
                }
            }

            using (var locked = target.Lock())
            {
                var rowBytes = frame.Width * 4;
                if (locked.RowBytes == frame.Stride)
                {
                    Marshal.Copy(frame.Data, 0, locked.Address, frame.Stride * frame.Height);
                }
                else
                {
                    for (var y = 0; y < frame.Height; y++)
                    {
                        Marshal.Copy(frame.Data, y * frame.Stride, locked.Address + y * locked.RowBytes, rowBytes);
                    }
                }
            }

            _latest = target;
            if (_presentPending)
            {
                return;
            }

            _presentPending = true;
        }

        Dispatcher.UIThread.Post(Present, DispatcherPriority.Render);
    }

    private void Present()
    {
        WriteableBitmap? latest;
        lock (_sync)
        {
            _presentPending = false;
            latest = _latest;
            _displayed = latest;
        }

        if (latest is not null && !_disposed)
        {
            Frame = latest;
            Status = "";
        }
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, null);

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        if (_session is not null)
        {
            _session.Decoder.FrameDecoded -= OnFrameDecoded;
            _session.Dispose();
        }

        Frame = null;
        if (Dispatcher.UIThread.CheckAccess())
        {
            Closed?.Invoke(this);
        }
        else
        {
            Dispatcher.UIThread.Post(() => Closed?.Invoke(this));
        }
    }
}
