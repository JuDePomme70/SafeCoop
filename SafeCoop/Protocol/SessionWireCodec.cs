using System;
using System.IO;
using System.Text;

namespace SafeCoop.Protocol;

/// <summary>
/// Small, deterministic and bounded wire format for the initial co-op handshake.
/// The format contains no user identity, telemetry, executable data, or save data.
/// </summary>
public static class SessionWireCodec {
    private const int ProtocolVersion = SessionHello.CurrentProtocolVersion;
    private const int MaxFieldLength = 256;

    public static byte[] Encode(SessionHello hello) {
        if (hello == null) {
            throw new ArgumentNullException(nameof(hello));
        }

        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true)) {
            writer.Write(ProtocolVersion);
            WriteBoundedString(writer, hello.GameVersion);
            WriteBoundedString(writer, hello.ModVersion);
            WriteBoundedString(writer, hello.SaveFingerprint);
            WriteBoundedString(writer, hello.SessionKeyFingerprint);
            writer.Flush();
            return stream.ToArray();
        }
    }

    public static SessionHello Decode(byte[] payload) {
        if (payload == null || payload.Length == 0) {
            throw new InvalidDataException("Handshake payload is empty.");
        }

        using (var stream = new MemoryStream(payload, false))
        using (var reader = new BinaryReader(stream, Encoding.UTF8, true)) {
            var version = reader.ReadInt32();
            if (version != ProtocolVersion) {
                throw new InvalidDataException("Handshake protocol version is not supported.");
            }

            var hello = new SessionHello(
                ReadBoundedString(reader),
                ReadBoundedString(reader),
                ReadBoundedString(reader),
                ReadBoundedString(reader));

            if (stream.Position != stream.Length) {
                throw new InvalidDataException("Handshake payload has unexpected trailing data.");
            }

            return hello;
        }
    }

    private static void WriteBoundedString(BinaryWriter writer, string value) {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > MaxFieldLength) {
            throw new InvalidDataException("Handshake field is too long.");
        }

        writer.Write((ushort)bytes.Length);
        writer.Write(bytes);
    }

    private static string ReadBoundedString(BinaryReader reader) {
        var length = reader.ReadUInt16();
        if (length > MaxFieldLength) {
            throw new InvalidDataException("Handshake field is too long.");
        }

        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length) {
            throw new EndOfStreamException("Handshake payload ended unexpectedly.");
        }

        return Encoding.UTF8.GetString(bytes);
    }
}
