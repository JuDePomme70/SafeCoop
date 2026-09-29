using System;
using System.IO;
using Mafi;
using Mafi.Core;
using Mafi.Core.Input;
using Mafi.Serialization;

namespace SafeCoop.Network;

/// <summary>
/// Serializes a scheduled game command with Captain of Industry's own save-game
/// serializer. The bytes are held only in memory and are valid only for the
/// exact game/mod/save combination accepted by the session handshake.
/// </summary>
public static class GameCommandCodec {
    public static byte[] Serialize(IInputCommand command) {
        if (command == null) {
            throw new ArgumentNullException(nameof(command));
        }

        // Scheduling is the last point at which all player input fields are
        // intact. Results are deliberately excluded because the remote game
        // must calculate its own result from the same starting state.
        var commandCopy = command.ShallowCloneWithoutResult();
        using (var stream = new MemoryStream())
        using (var writer = new BlobWriter(stream, null, false)) {
            writer.WriteGeneric(commandCopy);
            writer.FinalizeSerialization();
            writer.Flush();
            var payload = stream.ToArray();
            if (payload.Length == 0 || payload.Length > Protocol.SyncedCommand.MaxPayloadLength) {
                throw new InvalidDataException("Game command payload has an invalid length.");
            }

            return payload;
        }
    }

    public static IInputCommand Deserialize(byte[] payload, DependencyResolver resolver) {
        if (payload == null || payload.Length == 0 || payload.Length > Protocol.SyncedCommand.MaxPayloadLength) {
            throw new ArgumentException("Game command payload has an invalid length.", nameof(payload));
        }

        if (resolver == null) {
            throw new ArgumentNullException(nameof(resolver));
        }

        using (var stream = new MemoryStream(payload, false)) {
            var reader = new BlobReader(stream, 0, null, false);
            try {
                var command = reader.ReadGenericAs<IInputCommand>();
                reader.FinalizeLoading(resolver, () => { });
                if (command == null || reader.GetRemainingBytes() != 0) {
                    throw new InvalidDataException("Game command payload could not be fully restored.");
                }

                return command;
            }
            finally {
                reader.Destroy(false);
            }
        }
    }
}
