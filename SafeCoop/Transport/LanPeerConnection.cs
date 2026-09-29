using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SafeCoop.Protocol;

namespace SafeCoop.Transport;

/// <summary>
/// One authenticated LAN peer connection. It carries a small fixed set of
/// framed messages and owns no listener, process, native library, or storage.
/// Calls to ReceiveFrameAsync must be made by one reader at a time.
/// </summary>
public sealed class LanPeerConnection : IDisposable {
    private readonly TcpClient client;
    private readonly NetworkStream stream;
    private readonly SemaphoreSlim writeLock = new SemaphoreSlim(1, 1);
    private bool disposed;

    private LanPeerConnection(TcpClient client) {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        stream = client.GetStream();
    }

    public static async Task<LanPeerConnection> ConnectAsync(IPAddress address, int port) {
        if (address == null) {
            throw new ArgumentNullException(nameof(address));
        }

        ValidatePort(port);
        var client = new TcpClient(address.AddressFamily) {
            NoDelay = true,
        };
        try {
            await client.ConnectAsync(address, port).ConfigureAwait(false);
            return new LanPeerConnection(client);
        }
        catch {
            client.Close();
            throw;
        }
    }

    public static async Task<LanPeerConnection> AcceptAsync(TcpListener listener) {
        if (listener == null) {
            throw new ArgumentNullException(nameof(listener));
        }

        var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
        client.NoDelay = true;
        return new LanPeerConnection(client);
    }

    public async Task<SessionHello> ExchangeHelloAsync(SessionHello localHello) {
        if (localHello == null) {
            throw new ArgumentNullException(nameof(localHello));
        }

        await SendFrameAsync(new SessionFrame(SessionFrameKind.Hello, SessionWireCodec.Encode(localHello))).ConfigureAwait(false);
        var remoteFrame = await ReceiveFrameAsync().ConfigureAwait(false);
        if (remoteFrame.Kind != SessionFrameKind.Hello) {
            throw new InvalidDataException("The peer did not begin with a handshake.");
        }

        return SessionWireCodec.Decode(remoteFrame.Payload);
    }

    public Task SendCommandAsync(SyncedCommand command) {
        return SendFrameAsync(new SessionFrame(SessionFrameKind.Command, SyncedCommandCodec.Encode(command)));
    }

    public async Task SendFrameAsync(SessionFrame frame) {
        ThrowIfDisposed();
        var encoded = SessionFrameCodec.Encode(frame);
        await writeLock.WaitAsync().ConfigureAwait(false);
        try {
            await stream.WriteAsync(encoded, 0, encoded.Length).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }
        finally {
            writeLock.Release();
        }
    }

    public async Task<SessionFrame> ReceiveFrameAsync() {
        ThrowIfDisposed();
        var header = await ReadExactlyAsync(sizeof(int)).ConfigureAwait(false);
        var bodyLength = BitConverter.ToInt32(header, 0);
        if (bodyLength < sizeof(byte) || bodyLength > SessionFrameCodec.MaxFrameLength - sizeof(int)) {
            throw new InvalidDataException("Peer sent a frame with an invalid length.");
        }

        var body = await ReadExactlyAsync(bodyLength).ConfigureAwait(false);
        var combined = new byte[header.Length + body.Length];
        Buffer.BlockCopy(header, 0, combined, 0, header.Length);
        Buffer.BlockCopy(body, 0, combined, header.Length, body.Length);
        return SessionFrameCodec.Decode(combined);
    }

    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;
        stream.Close();
        client.Close();
        writeLock.Dispose();
    }

    private async Task<byte[]> ReadExactlyAsync(int length) {
        var buffer = new byte[length];
        var offset = 0;
        while (offset < length) {
            var read = await stream.ReadAsync(buffer, offset, length - offset).ConfigureAwait(false);
            if (read == 0) {
                throw new EndOfStreamException("Peer disconnected while a frame was being read.");
            }

            offset += read;
        }

        return buffer;
    }

    private static void ValidatePort(int port) {
        if (port < IPEndPoint.MinPort || port > IPEndPoint.MaxPort) {
            throw new ArgumentOutOfRangeException(nameof(port));
        }
    }

    private void ThrowIfDisposed() {
        if (disposed) {
            throw new ObjectDisposedException(nameof(LanPeerConnection));
        }
    }
}
