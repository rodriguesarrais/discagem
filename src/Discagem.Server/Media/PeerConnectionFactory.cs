using System.Net;
using System.Net.Sockets;
using Discagem.Server.Options;
using Microsoft.Extensions.Options;
using SIPSorcery.Net;
using SIPSorcery.Sys;

namespace Discagem.Server.Media;

/// <summary>Creates server-side peer connections bound to the configured UDP port range.</summary>
public sealed class PeerConnectionFactory
{
    private readonly MediaOptions _options;
    private readonly PortRange _portRange;
    private readonly IPAddress? _bindAddress;
    private readonly IPAddress? _publicAddress;

    public PeerConnectionFactory(IOptions<DiscagemOptions> options)
    {
        _options = options.Value.Media;
        var start = _options.RtpPortStart % 2 == 0 ? _options.RtpPortStart : _options.RtpPortStart + 1;
        _portRange = new PortRange(start, Math.Max(start + 2, _options.RtpPortEnd), shuffle: true);

        if (!string.IsNullOrWhiteSpace(_options.BindAddress))
        {
            _bindAddress = IPAddress.Parse(_options.BindAddress);
        }

        if (!string.IsNullOrWhiteSpace(_options.PublicIpAddress))
        {
            _publicAddress = IPAddress.Parse(_options.PublicIpAddress);
        }
    }

    public RTCPeerConnection Create()
    {
        var configuration = new RTCConfiguration
        {
            iceServers = [],
            X_UseRtpFeedbackProfile = true,
            X_BindAddress = _bindAddress,
        };

        var pc = new RTCPeerConnection(configuration, 0, _portRange);
        AnnouncePublicAddress(pc);
        return pc;
    }

    /// <summary>
    /// Behind 1:1 NAT the interfaces only carry private addresses, so the public address is announced
    /// as an additional host candidate on the same port.
    /// </summary>
    private void AnnouncePublicAddress(RTCPeerConnection pc)
    {
        if (_publicAddress is null)
        {
            return;
        }

        var ports = pc.GetRtpChannel().Candidates
            .Where(c => c.type == RTCIceCandidateType.host && c.protocol == RTCIceProtocol.udp)
            .Where(c => IPAddress.TryParse(c.address, out var address) && address.AddressFamily == _publicAddress.AddressFamily)
            .Select(c => c.port)
            .Distinct()
            .ToList();

        var alreadyAnnounced = pc.GetRtpChannel().Candidates.Any(c => c.address == _publicAddress.ToString());
        if (alreadyAnnounced)
        {
            return;
        }

        foreach (var port in ports)
        {
            var candidate = new RTCIceCandidate(RTCIceProtocol.udp, _publicAddress, port, RTCIceCandidateType.host);
            pc.addLocalIceCandidate(candidate);
        }
    }

    public static bool IsIpv4(IPAddress address) => address.AddressFamily == AddressFamily.InterNetwork;
}
