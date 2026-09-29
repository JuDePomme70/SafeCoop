using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SafeCoop.Protocol;
using SafeCoop.Transport;

namespace SafeCoop.Network;

/// <summary>
/// Keeps one authenticated Tailscale peer connected. The host stays available
/// after a disconnect and a guest retries automatically while its host is loading.
/// </summary>
public sealed class LanSessionCoordinator : IDisposable {
    private const string GameVersion = "0.8.7d";
    private const string ModVersion = "0.4.0";
    private const int ReconnectDelayMs = 2000;
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(5);

    private readonly LanSessionSettings settings;
    private readonly Action<string> info;
    private readonly Action<string> warning;
    private readonly Action<SessionStatus, string> statusChanged;
    private readonly string saveFingerprint;
    private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
    private readonly ConcurrentQueue<SyncedCommand> receivedCommands = new ConcurrentQueue<SyncedCommand>();
    private TcpListener? listener;
    private LanPeerConnection? connection;
    private long nextLocalSequence;
    private long lastRemoteSequence;
    private int connected;
    private int started;
    private int disposed;

    public LanSessionCoordinator(
        LanSessionSettings settings,
        string saveFingerprint,
        Action<string> info,
        Action<string> warning,
        Action<SessionStatus, string> statusChanged) {
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.saveFingerprint = string.IsNullOrWhiteSpace(saveFingerprint)
            ? throw new ArgumentException("A save fingerprint is required.", nameof(saveFingerprint))
            : saveFingerprint;
        this.info = info ?? throw new ArgumentNullException(nameof(info));
        this.warning = warning ?? throw new ArgumentNullException(nameof(warning));
        this.statusChanged = statusChanged ?? throw new ArgumentNullException(nameof(statusChanged));
    }

    public bool IsConnected => Volatile.Read(ref connected) != 0;

    public void Start() {
        if (Interlocked.Exchange(ref started, 1) != 0) {
            throw new InvalidOperationException("SafeCoop session has already started.");
        }

        _ = RunAsync();
    }

    public bool QueueLocalCommand(byte[] payload) {
        if (!IsConnected || Volatile.Read(ref disposed) != 0) {
            return false;
        }

        var command = new SyncedCommand(Interlocked.Increment(ref nextLocalSequence), payload);
        _ = SendCommandAsync(command);
        return true;
    }

    public bool TryDequeueRemoteCommand(out SyncedCommand command) {
        return receivedCommands.TryDequeue(out command!);
    }

    public void Dispose() {
        if (Interlocked.Exchange(ref disposed, 1) != 0) {
            return;
        }

        Publish(SessionStatus.Stopped, settings.IsHostMachine
            ? "Session ferm?e. Ton pote a ?t? d?connect?."
            : "Tu as quitt? la session.");
        cancellation.Cancel();
        listener?.Stop();
        connection?.Dispose();
        Volatile.Write(ref connected, 0);
    }

    private async Task RunAsync() {
        try {
            if (settings.IsHostMachine) {
                await RunHostAsync().ConfigureAwait(false);
            }
            else {
                await RunGuestAsync().ConfigureAwait(false);
            }
        }
        catch (Exception) when (cancellation.IsCancellationRequested || Volatile.Read(ref disposed) != 0) {
            // Closing the game cancels pending network work by design.
        }
        catch (Exception exception) {
            warning($"SafeCoop session stopped unexpectedly: {exception.Message}");
            Publish(SessionStatus.Failed, "La session a rencontr? une erreur : " + exception.Message);
        }
        finally {
            Volatile.Write(ref connected, 0);
            connection?.Dispose();
            connection = null;
            listener?.Stop();
            listener = null;
        }
    }

    private async Task RunHostAsync() {
        listener = new TcpListener(settings.HostAddress, settings.Port);
        listener.Start();
        info("SafeCoop is waiting for one Tailscale peer.");
        Publish(SessionStatus.WaitingForPeer, "Session pr?te : en attente de ton pote.");

        while (!cancellation.IsCancellationRequested) {
            LanPeerConnection? peer = null;
            var peerWasConnected = false;
            try {
                peer = await LanPeerConnection.AcceptAsync(listener).ConfigureAwait(false);
                connection = peer;
                await RunVerifiedPeerAsync(peer).ConfigureAwait(false);
            }
            catch (InvalidDataException exception) when (!cancellation.IsCancellationRequested) {
                warning($"SafeCoop rejected a peer: {exception.Message}");
                Publish(SessionStatus.WaitingForPeer, "Connexion refus?e : la version, le code ou la sauvegarde ne correspond pas.");
            }
            catch (Exception exception) when (!cancellation.IsCancellationRequested && Volatile.Read(ref disposed) == 0) {
                warning($"SafeCoop host connection ended: {exception.Message}");
            }
            finally {
                peerWasConnected = Interlocked.Exchange(ref connected, 0) != 0;
                peer?.Dispose();
                if (ReferenceEquals(connection, peer)) {
                    connection = null;
                }
            }

            if (!cancellation.IsCancellationRequested) {
                if (peerWasConnected) {
                    info("SafeCoop peer disconnected. Waiting for a reconnection.");
                    Publish(SessionStatus.PeerDisconnected, "Ton pote a quitt? la session. En attente d'un retour.");
                }
                else {
                    Publish(SessionStatus.WaitingForPeer, "Session pr?te : en attente de ton pote.");
                }
            }
        }
    }

    private async Task RunGuestAsync() {
        while (!cancellation.IsCancellationRequested) {
            LanPeerConnection? peer = null;
            var peerWasConnected = false;
            try {
                Publish(SessionStatus.Connecting, "Connexion ? la session de ton pote?");
                peer = await LanPeerConnection.ConnectAsync(settings.HostAddress, settings.Port).ConfigureAwait(false);
                connection = peer;
                await RunVerifiedPeerAsync(peer).ConfigureAwait(false);
            }
            catch (InvalidDataException exception) when (!cancellation.IsCancellationRequested) {
                warning($"SafeCoop session was refused: {exception.Message}");
                Publish(SessionStatus.Failed, "Connexion refus?e : la version, le code ou la sauvegarde ne correspond pas.");
                return;
            }
            catch (Exception exception) when (!cancellation.IsCancellationRequested && Volatile.Read(ref disposed) == 0) {
                warning($"SafeCoop could not reach the host yet: {exception.Message}");
            }
            finally {
                peerWasConnected = Interlocked.Exchange(ref connected, 0) != 0;
                peer?.Dispose();
                if (ReferenceEquals(connection, peer)) {
                    connection = null;
                }
            }

            if (cancellation.IsCancellationRequested) {
                break;
            }

            if (peerWasConnected) {
                info("SafeCoop host connection ended. Retrying.");
                Publish(SessionStatus.PeerDisconnected, "Connexion perdue. Nouvelle tentative?");
            }
            else {
                Publish(SessionStatus.Connecting, "L'h?te n'est pas encore pr?t. Nouvelle tentative?");
            }

            try {
                await Task.Delay(ReconnectDelayMs, cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) {
                break;
            }
        }
    }

    private async Task RunVerifiedPeerAsync(LanPeerConnection peer) {
        var localHello = new SessionHello(
            GameVersion,
            ModVersion,
            saveFingerprint,
            SessionFingerprint.FromText(settings.SessionCode));
        var peerHello = await peer.ExchangeHelloAsync(localHello).ConfigureAwait(false);
        var compatibilityError = localHello.ValidateAgainst(peerHello);
        if (!string.IsNullOrEmpty(compatibilityError)) {
            throw new InvalidDataException(compatibilityError);
        }

        Interlocked.Exchange(ref lastRemoteSequence, 0);
        Volatile.Write(ref connected, 1);
        info("SafeCoop peer verified. Command synchronization is active.");
        Publish(SessionStatus.Connected, settings.IsHostMachine
            ? "Ton pote a rejoint la session. Synchronisation active."
            : "Connect? ? la session. Synchronisation active.");

        using (var peerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token)) {
            var keepAlive = SendKeepAlivesAsync(peer, peerCancellation.Token);
            try {
                while (!cancellation.IsCancellationRequested) {
                    var frame = await peer.ReceiveFrameAsync().ConfigureAwait(false);
                    switch (frame.Kind) {
                        case SessionFrameKind.Command:
                            EnqueueRemoteCommand(SyncedCommandCodec.Decode(frame.Payload));
                            break;
                        case SessionFrameKind.KeepAlive:
                            break;
                        case SessionFrameKind.Disconnect:
                            return;
                        default:
                            throw new InvalidDataException("Peer sent a message not supported by this build.");
                    }
                }
            }
            finally {
                peerCancellation.Cancel();
                try {
                    await keepAlive.ConfigureAwait(false);
                }
                catch (Exception) when (cancellation.IsCancellationRequested || Volatile.Read(ref disposed) != 0) {
                    // The game is closing, so the connection is being intentionally torn down.
                }
            }
        }
    }

    private static async Task SendKeepAlivesAsync(LanPeerConnection peer, CancellationToken token) {
        try {
            while (!token.IsCancellationRequested) {
                await Task.Delay(KeepAliveInterval, token).ConfigureAwait(false);
                await peer.SendFrameAsync(new SessionFrame(SessionFrameKind.KeepAlive, Array.Empty<byte>())).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) {
            // Expected whenever the peer session ends.
        }
        catch (ObjectDisposedException) {
            // Expected when the game or connection closes.
        }
        catch (IOException) {
            // The receive loop will report this disconnection and trigger retry logic.
        }
    }

    private async Task SendCommandAsync(SyncedCommand command) {
        try {
            var activeConnection = connection;
            if (activeConnection == null || !IsConnected) {
                return;
            }

            await activeConnection.SendCommandAsync(command).ConfigureAwait(false);
        }
        catch (Exception) when (cancellation.IsCancellationRequested || Volatile.Read(ref disposed) != 0) {
            // The session is being closed normally.
        }
        catch (Exception exception) {
            warning($"SafeCoop could not send a game action: {exception.Message}");
        }
    }

    private void EnqueueRemoteCommand(SyncedCommand command) {
        if (command.Sequence <= Interlocked.Read(ref lastRemoteSequence)) {
            throw new InvalidDataException("Peer sent a duplicate or out-of-order game action.");
        }

        Interlocked.Exchange(ref lastRemoteSequence, command.Sequence);
        receivedCommands.Enqueue(command);
    }

    private void Publish(SessionStatus state, string message) {
        statusChanged(state, message);
    }
}
