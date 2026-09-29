using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using SafeCoop.Protocol;

namespace SafeCoop.Transport;

/// <summary>
/// Opt-in TCP handshake transport for a future LAN session.
/// It does not start automatically and carries only SessionHello metadata.
/// </summary>
public static class HandshakeTransport {
    private const int MaxPayloadLength = 4096;

    public static async Task<SessionHello> ConnectAndExchangeAsync(
        IPAddress address,
        int port,
        SessionHello localHello) {
        if (address == null) {
            throw new ArgumentNullException(nameof(address));
        }

        using (var client = new TcpClient()) {
            await client.ConnectAsync(address, port).ConfigureAwait(false);
            return await ExchangeAsync(client.GetStream(), localHello).ConfigureAwait(false);
        }
    }

    public static async Task<SessionHello> AcceptAndExchangeAsync(
        TcpListener listener,
        SessionHello localHello) {
        if (listener == null) {
            throw new ArgumentNullException(nameof(listener));
        }

        using (var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false)) {
            return await ExchangeAsync(client.GetStream(), localHello).ConfigureAwait(false);
        }
    }

    private static async Task<SessionHello> ExchangeAsync(NetworkStream stream, SessionHello localHello) {
        if (localHello == null) {
            throw new ArgumentNullException(nameof(localHello));
        }

        var localPayload = SessionWireCodec.Encode(localHello);
        await WriteFrameAsync(stream, localPayload).ConfigureAwait(false);
        var remotePayload = await ReadFrameAsync(stream).ConfigureAwait(false);
        return SessionWireCodec.Decode(remotePayload);
    }

    private static async Task WriteFrameAsync(Stream stream, byte[] payload) {
        if (payload.Length > MaxPayloadLength) {
            throw new InvalidDataException("Handshake payload is too large.");
        }

        var length = BitConverter.GetBytes(payload.Length);
        await stream.WriteAsync(length, 0, length.Length).ConfigureAwait(false);
        await stream.WriteAsync(payload, 0, payload.Length).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadFrameAsync(Stream stream) {
        var lengthBytes = await ReadExactlyAsync(stream, sizeof(int)).ConfigureAwait(false);
        var length = BitConverter.ToInt32(lengthBytes, 0);
        if (length <= 0 || length > MaxPayloadLength) {
            throw new InvalidDataException("Handshake frame length is invalid.");
        }

        return await ReadExactlyAsync(stream, length).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int length) {
        var buffer = new byte[length];
        var offset = 0;
        while (offset < length) {
            var read = await stream.ReadAsync(buffer, offset, length - offset).ConfigureAwait(false);
            if (read == 0) {
                throw new EndOfStreamException("Peer closed the connection during the handshake.");
            }

            offset += read;
        }

        return buffer;
    }
}
