using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Discagem.Server.Options;
using Microsoft.Extensions.Options;

namespace Discagem.Server.Media;

/// <summary>
/// Minimal STUN (RFC 5389) binding responder so clients can discover their public address
/// without relying on third-party STUN servers.
/// </summary>
public sealed class StunResponder(IOptions<DiscagemOptions> options, ILogger<StunResponder> logger) : BackgroundService
{
    private const uint MagicCookie = 0x2112A442;
    private const ushort BindingRequest = 0x0001;
    private const ushort BindingSuccess = 0x0101;
    private const ushort AttrMappedAddress = 0x0001;
    private const ushort AttrXorMappedAddress = 0x0020;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var port = options.Value.Media.StunPort;
        if (port <= 0)
        {
            return;
        }

        using var socket = CreateSocket(port);
        logger.LogInformation("STUN responder listening on UDP port {Port}", port);

        var buffer = new byte[1500];
        EndPoint any = socket.AddressFamily == AddressFamily.InterNetworkV6
            ? new IPEndPoint(IPAddress.IPv6Any, 0)
            : new IPEndPoint(IPAddress.Any, 0);

        while (!stoppingToken.IsCancellationRequested)
        {
            SocketReceiveFromResult received;
            try
            {
                received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException ex)
            {
                // ICMP port unreachable and similar errors surface here on some platforms.
                logger.LogDebug(ex, "STUN receive error");
                continue;
            }

            var response = BuildResponse(buffer.AsSpan(0, received.ReceivedBytes), (IPEndPoint)received.RemoteEndPoint);
            if (response is not null)
            {
                try
                {
                    await socket.SendToAsync(response, SocketFlags.None, received.RemoteEndPoint, stoppingToken);
                }
                catch (SocketException ex)
                {
                    logger.LogDebug(ex, "STUN send error");
                }
            }
        }
    }

    private static Socket CreateSocket(int port)
    {
        try
        {
            var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp) { DualMode = true };
            socket.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
            return socket;
        }
        catch (SocketException)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Any, port));
            return socket;
        }
    }

    /// <summary>Builds a binding success response, or returns null if the datagram is not a binding request.</summary>
    internal static byte[]? BuildResponse(ReadOnlySpan<byte> request, IPEndPoint remote)
    {
        if (request.Length < 20 ||
            BinaryPrimitives.ReadUInt16BigEndian(request) != BindingRequest ||
            BinaryPrimitives.ReadUInt32BigEndian(request[4..]) != MagicCookie)
        {
            return null;
        }

        var transactionId = request.Slice(8, 12);
        var address = remote.Address.IsIPv4MappedToIPv6 ? remote.Address.MapToIPv4() : remote.Address;
        var addressBytes = address.GetAddressBytes();
        var family = (byte)(addressBytes.Length == 4 ? 0x01 : 0x02);
        var attributeLength = 4 + addressBytes.Length;

        var response = new byte[20 + 2 * (4 + attributeLength)];
        BinaryPrimitives.WriteUInt16BigEndian(response, BindingSuccess);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(2), (ushort)(response.Length - 20));
        BinaryPrimitives.WriteUInt32BigEndian(response.AsSpan(4), MagicCookie);
        transactionId.CopyTo(response.AsSpan(8));

        // XOR-MAPPED-ADDRESS
        var offset = 20;
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(offset), AttrXorMappedAddress);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(offset + 2), (ushort)attributeLength);
        response[offset + 5] = family;
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(offset + 6), (ushort)(remote.Port ^ (MagicCookie >> 16)));
        Span<byte> xorKey = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(xorKey, MagicCookie);
        transactionId.CopyTo(xorKey[4..]);
        for (var i = 0; i < addressBytes.Length; i++)
        {
            response[offset + 8 + i] = (byte)(addressBytes[i] ^ xorKey[i]);
        }

        // MAPPED-ADDRESS (for older clients)
        offset += 4 + attributeLength;
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(offset), AttrMappedAddress);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(offset + 2), (ushort)attributeLength);
        response[offset + 5] = family;
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(offset + 6), (ushort)remote.Port);
        addressBytes.CopyTo(response.AsSpan(offset + 8));

        return response;
    }
}
