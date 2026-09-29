using System;
using System.IO;

namespace SafeCoop.Protocol;

/// <summary>
/// An opaque, bounded game-command payload. The payload is created only by the
/// game's serializer and is later decoded only by that same game version.
/// </summary>
public sealed class SyncedCommand {
    public const int MaxPayloadLength = 768 * 1024;

    public SyncedCommand(long sequence, byte[] payload) {
        if (sequence <= 0) {
            throw new ArgumentOutOfRangeException(nameof(sequence));
        }

        if (payload == null || payload.Length == 0 || payload.Length > MaxPayloadLength) {
            throw new ArgumentException("Command payload has an invalid length.", nameof(payload));
        }

        Sequence = sequence;
        Payload = payload;
    }

    public long Sequence { get; }
    public byte[] Payload { get; }
}

public static class SyncedCommandCodec {
    public static byte[] Encode(SyncedCommand command) {
        if (command == null) {
            throw new ArgumentNullException(nameof(command));
        }

        using (var stream = new MemoryStream(sizeof(long) + sizeof(int) + command.Payload.Length))
        using (var writer = new BinaryWriter(stream)) {
            writer.Write(command.Sequence);
            writer.Write(command.Payload.Length);
            writer.Write(command.Payload);
            writer.Flush();
            return stream.ToArray();
        }
    }

    public static SyncedCommand Decode(byte[] payload) {
        if (payload == null) {
            throw new ArgumentNullException(nameof(payload));
        }

        using (var stream = new MemoryStream(payload, false))
        using (var reader = new BinaryReader(stream)) {
            var sequence = reader.ReadInt64();
            var length = reader.ReadInt32();
            if (length <= 0 || length > SyncedCommand.MaxPayloadLength || length != stream.Length - stream.Position) {
                throw new InvalidDataException("Command payload length is invalid.");
            }

            var commandPayload = reader.ReadBytes(length);
            if (commandPayload.Length != length || stream.Position != stream.Length) {
                throw new EndOfStreamException("Command payload ended unexpectedly.");
            }

            return new SyncedCommand(sequence, commandPayload);
        }
    }
}
