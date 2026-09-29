using System;
using System.IO;

namespace SafeCoop.Protocol;

/// <summary>
/// The only message types which may travel across a SafeCoop LAN connection.
/// This deliberately is not a general-purpose RPC protocol: executable code,
/// files, and save data are never valid frames.
/// </summary>
public enum SessionFrameKind : byte {
    Hello = 1,
    Command = 2,
    KeepAlive = 3,
    Disconnect = 4,
}

public sealed class SessionFrame {
    public SessionFrame(SessionFrameKind kind, byte[] payload) {
        if (!Enum.IsDefined(typeof(SessionFrameKind), kind)) {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        Kind = kind;
        Payload = payload ?? throw new ArgumentNullException(nameof(payload));
    }

    public SessionFrameKind Kind { get; }
    public byte[] Payload { get; }
}

public static class SessionFrameCodec {
    public const int MaxFrameLength = 1024 * 1024;
    private const int HeaderLength = sizeof(int) + sizeof(byte);

    public static byte[] Encode(SessionFrame frame) {
        if (frame == null) {
            throw new ArgumentNullException(nameof(frame));
        }

        if (frame.Payload.Length > MaxFrameLength - HeaderLength) {
            throw new InvalidDataException("Session frame is too large.");
        }

        using (var stream = new MemoryStream(HeaderLength + frame.Payload.Length))
        using (var writer = new BinaryWriter(stream)) {
            writer.Write(frame.Payload.Length + sizeof(byte));
            writer.Write((byte)frame.Kind);
            writer.Write(frame.Payload);
            writer.Flush();
            return stream.ToArray();
        }
    }

    public static SessionFrame Decode(byte[] encoded) {
        if (encoded == null) {
            throw new ArgumentNullException(nameof(encoded));
        }

        if (encoded.Length < HeaderLength || encoded.Length > MaxFrameLength) {
            throw new InvalidDataException("Session frame has an invalid length.");
        }

        using (var stream = new MemoryStream(encoded, false))
        using (var reader = new BinaryReader(stream)) {
            var declaredLength = reader.ReadInt32();
            if (declaredLength != encoded.Length - sizeof(int) || declaredLength < sizeof(byte)) {
                throw new InvalidDataException("Session frame length does not match its payload.");
            }

            var rawKind = reader.ReadByte();
            if (!Enum.IsDefined(typeof(SessionFrameKind), rawKind)) {
                throw new InvalidDataException("Session frame type is not supported.");
            }

            var payload = reader.ReadBytes(declaredLength - sizeof(byte));
            if (payload.Length != declaredLength - sizeof(byte) || stream.Position != stream.Length) {
                throw new EndOfStreamException("Session frame ended unexpectedly.");
            }

            return new SessionFrame((SessionFrameKind)rawKind, payload);
        }
    }
}
