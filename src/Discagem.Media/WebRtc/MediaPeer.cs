using SIPSorcery.Net;

namespace Discagem.Media.WebRtc;

/// <summary>An ICE server for client peer connections.</summary>
public sealed record IceServerConfig(string Urls, string? Username = null, string? Credential = null);

/// <summary>
/// Client-side WebRTC peer connection to the Discagem SFU. The client always creates the offer;
/// the server answers. Signalling is left to the caller.
/// </summary>
public abstract class MediaPeer : IDisposable
{
    private int _disposed;

    protected MediaPeer(IEnumerable<IceServerConfig> iceServers, MediaStreamStatusEnum direction)
    {
        var configuration = new RTCConfiguration
        {
            iceServers = iceServers
                .Select(s => new RTCIceServer { urls = s.Urls, username = s.Username, credential = s.Credential })
                .ToList(),
            X_UseRtpFeedbackProfile = true,
        };

        PeerConnection = new RTCPeerConnection(configuration);
        PeerConnection.addTrack(DiscagemVideo.CreateTrack(direction));
        PeerConnection.onicecandidate += candidate =>
        {
            if (candidate is not null)
            {
                LocalCandidate?.Invoke(candidate.candidate, candidate.sdpMid, candidate.sdpMLineIndex);
            }
        };
        PeerConnection.onconnectionstatechange += state => StateChanged?.Invoke(state);
    }

    public RTCPeerConnection PeerConnection { get; }

    public RTCPeerConnectionState State => PeerConnection.connectionState;

    public bool IsConnected => PeerConnection.connectionState == RTCPeerConnectionState.connected;

    protected bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>A local ICE candidate that must be forwarded to the server (candidate, sdpMid, sdpMLineIndex).</summary>
    public event Action<string, string?, int?>? LocalCandidate;

    public event Action<RTCPeerConnectionState>? StateChanged;

    public async Task<string> CreateOfferAsync()
    {
        var offer = PeerConnection.createOffer();
        await PeerConnection.setLocalDescription(offer);
        return offer.sdp;
    }

    public void ApplyAnswer(string sdp)
    {
        var result = PeerConnection.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = sdp });
        if (result != SetDescriptionResultEnum.OK)
        {
            throw new InvalidOperationException($"The server's session description was rejected ({result}).");
        }
    }

    public void AddRemoteCandidate(string candidate, string? sdpMid, int? sdpMLineIndex)
    {
        try
        {
            PeerConnection.addIceCandidate(new RTCIceCandidateInit
            {
                candidate = candidate,
                sdpMid = sdpMid ?? "0",
                sdpMLineIndex = (ushort)Math.Max(0, sdpMLineIndex ?? 0),
            });
        }
        catch (Exception)
        {
            // Malformed candidates are ignored, as browsers do.
        }
    }

    /// <summary>Waits until the connection is established, fails, or the timeout elapses.</summary>
    public async Task<bool> WaitForConnectionAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(RTCPeerConnectionState state)
        {
            if (state == RTCPeerConnectionState.connected)
            {
                tcs.TrySetResult(true);
            }
            else if (state is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed)
            {
                tcs.TrySetResult(false);
            }
        }

        StateChanged += Handler;
        try
        {
            Handler(State);
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeout, cancellationToken));
            return completed == tcs.Task && tcs.Task.Result;
        }
        finally
        {
            StateChanged -= Handler;
        }
    }

    protected virtual void OnDisposing()
    {
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        OnDisposing();
        try
        {
            PeerConnection.Close("closed by client");
        }
        catch
        {
            // Already closed.
        }

        GC.SuppressFinalize(this);
    }
}
