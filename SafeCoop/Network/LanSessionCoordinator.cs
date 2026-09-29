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
/// Runs one opt-in, single-peer Tailscale session. It accepts only a bounded
/// stream of serialized game commands after both peers prove save equality.
/// </summary>
public sealed class LanSessionCoordinator : IDisposable {
    private const string GameVersion = "0.8.7d";
    private const string ModVersion = "0.3.1";

    private readonly LanSessionSettings settings;
    private readonly Action<string> info;
    private readonly Action<string> warning;
    private readonly string saveFingerprint;
    private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
    private readonly ConcurrentQueue<SyncedCommand> receivedCommands = new ConcurrentQueue<SyncedCommand>();
    private TcpListener? listener;
    private LanPeerConnection? connection;
    private long nextLocalSequence;
    private long lastRemoteSequence;
    private int disposed;

    public LanSessionCoordinator(LanSessionSettings settings, string saveFingerprint, Action<string> info, Action<string> warning) {
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.saveFingerprint = string.IsNullOrWhiteSpace(saveFingerprint)
            ? throw new ArgumentException("A save fingerprint is required.", nameof(saveFingerprint))
            : saveFingerprint;
        this.info = info ?? throw new ArgumentNullException(nameof(info));
        this.warning = warning ?? throw new ArgumentNullException(nameof(warning));
    }

    public bool IsConnected { get; private set; }

    public void Start() {
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

        cancellation.Cancel();
        listener?.Stop();
        connection?.Dispose();
        cancellation.Dispose();
        IsConnected = false;
    }

    private async Task RunAsync() {
        try {
            var localHello = new SessionHello(
                GameVersion,
                ModVersion,
                saveFingerprint,
                SessionFingerprint.FromText(settings.SessionCode));
            if (settings.IsHostMachine) {
                listener = new TcpListener(settings.HostAddress, settings.Port);
                listener.Start();
                info("SafeCoop is waiting for one Tailscale peer.");
                connection = await LanPeerConnection.AcceptAsync(listener).ConfigureAwait(false);
            }
            else {
                info("SafeCoop is connecting to the Tailscale host.");
                connection = await LanPeerConnection.ConnectAsync(settings.HostAddress, settings.Port).ConfigureAwait(false);
            }

            var peerHello = await connection.ExchangeHelloAsync(localHello).ConfigureAwait(false);
            var compatibilityError = localHello.ValidateAgainst(peerHello);
            if (!string.IsNullOrEmpty(compatibilityError)) {
                throw new InvalidDataException(compatibilityError);
            }

            IsConnected = true;
            info("SafeCoop peer verified. Command synchronization is active.");

            while (!cancellation.IsCancellationRequested) {
                var frame = await connection.ReceiveFrameAsync().ConfigureAwait(false);
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
        catch (Exception) when (cancellation.IsCancellationRequested || Volatile.Read(ref disposed) != 0) {
            // Closing a game cancels pending accept/read operations by design.
        }
        catch (Exception exception) {
            warning($"SafeCoop session was not started: {exception.Message}");
        }
        finally {
            IsConnected = false;
            connection?.Dispose();
            connection = null;
            listener?.Stop();
            listener = null;
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
        if (command.Sequence <= lastRemoteSequence) {
            throw new InvalidDataException("Peer sent a duplicate or out-of-order game action.");
        }

        lastRemoteSequence = command.Sequence;
        receivedCommands.Enqueue(command);
    }
}
