using SafeCoop.Protocol;
using SafeCoop.Transport;
using System.Net;
using System.Net.Sockets;

var hostPeer = new SessionHello("0.8.7d", "0.1.0", "same-save", "same-code");
var matchingPeer = new SessionHello("0.8.7d", "0.1.0", "same-save", "same-code");
var wrongGamePeer = new SessionHello("0.8.7c", "0.1.0", "same-save", "same-code");
var wrongSavePeer = new SessionHello("0.8.7d", "0.1.0", "other-save", "same-code");
var wrongCodePeer = new SessionHello("0.8.7d", "0.1.0", "same-save", "other-code");

Assert(string.IsNullOrEmpty(hostPeer.ValidateAgainst(matchingPeer)), "matching peers must be accepted");
Assert(!string.IsNullOrEmpty(hostPeer.ValidateAgainst(wrongGamePeer)), "a different game version must be rejected");
Assert(!string.IsNullOrEmpty(hostPeer.ValidateAgainst(wrongSavePeer)), "a different save must be rejected");
Assert(!string.IsNullOrEmpty(hostPeer.ValidateAgainst(wrongCodePeer)), "a different session code must be rejected");
Assert(SessionFingerprint.FromBytes(new byte[] { 1, 2, 3, 4 }).Length == 64, "fingerprints must be SHA-256 hashes");
Assert(SessionFingerprint.FromText("private-code").Length == 64, "text session codes must be fingerprinted");
var command = new SyncedCommand(1, new byte[] { 3, 1, 4, 1, 5 });
var commandRoundTrip = SyncedCommandCodec.Decode(SyncedCommandCodec.Encode(command));
Assert(commandRoundTrip.Sequence == command.Sequence, "command sequence must survive encoding");
Assert(commandRoundTrip.Payload.SequenceEqual(command.Payload), "command payload must survive encoding");

var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
try {
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var hostExchange = HandshakeTransport.AcceptAndExchangeAsync(listener, hostPeer);
    var guestExchange = HandshakeTransport.ConnectAndExchangeAsync(IPAddress.Loopback, port, matchingPeer);
    await Task.WhenAll(hostExchange, guestExchange);
    Assert(string.IsNullOrEmpty(hostPeer.ValidateAgainst(guestExchange.Result)), "host must receive the guest handshake");
    Assert(string.IsNullOrEmpty(matchingPeer.ValidateAgainst(hostExchange.Result)), "guest must receive the host handshake");
}
finally {
    listener.Stop();
}

var persistentListener = new TcpListener(IPAddress.Loopback, 0);
persistentListener.Start();
try {
    var port = ((IPEndPoint)persistentListener.LocalEndpoint).Port;
    var hostTask = Task.Run(async () => {
        using (var host = await LanPeerConnection.AcceptAsync(persistentListener)) {
            var hello = await host.ExchangeHelloAsync(hostPeer);
            var frame = await host.ReceiveFrameAsync();
            Assert(frame.Kind == SessionFrameKind.Command, "host must receive a command frame");
            var received = SyncedCommandCodec.Decode(frame.Payload);
            Assert(received.Sequence == 1 && received.Payload.SequenceEqual(command.Payload), "host must receive the expected command");
            await host.SendFrameAsync(new SessionFrame(SessionFrameKind.KeepAlive, Array.Empty<byte>()));
            return hello;
        }
    });

    using (var guest = await LanPeerConnection.ConnectAsync(IPAddress.Loopback, port)) {
        var remoteHello = await guest.ExchangeHelloAsync(matchingPeer);
        Assert(string.IsNullOrEmpty(matchingPeer.ValidateAgainst(remoteHello)), "persistent connection must exchange handshakes");
        await guest.SendCommandAsync(command);
        var reply = await guest.ReceiveFrameAsync();
        Assert(reply.Kind == SessionFrameKind.KeepAlive, "guest must receive a reply frame");
    }

    Assert(string.IsNullOrEmpty(hostPeer.ValidateAgainst(await hostTask)), "host persistent handshake must match");
}
finally {
    persistentListener.Stop();
}

Console.WriteLine("Protocol and localhost transport tests passed.");

static void Assert(bool condition, string message) {
    if (!condition) {
        throw new InvalidOperationException(message);
    }
}
