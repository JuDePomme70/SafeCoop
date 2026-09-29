using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SafeCoop.Protocol;

/// <summary>
/// Data that both players must agree on before any future shared session begins.
/// It is pure data: this class opens no socket and reads no game state.
/// </summary>
public sealed class SessionHello {
    public const int CurrentProtocolVersion = 2;

    public SessionHello(string gameVersion, string modVersion, string saveFingerprint, string sessionKeyFingerprint) {
        GameVersion = RequireValue(gameVersion, nameof(gameVersion));
        ModVersion = RequireValue(modVersion, nameof(modVersion));
        SaveFingerprint = RequireValue(saveFingerprint, nameof(saveFingerprint));
        SessionKeyFingerprint = RequireValue(sessionKeyFingerprint, nameof(sessionKeyFingerprint));
    }

    public string GameVersion { get; }
    public string ModVersion { get; }
    public string SaveFingerprint { get; }
    public string SessionKeyFingerprint { get; }

    public string ValidateAgainst(SessionHello peer) {
        if (peer == null) {
            return "Peer handshake is missing.";
        }

        if (!string.Equals(GameVersion, peer.GameVersion, StringComparison.Ordinal)) {
            return "Both players must use exactly the same Captain of Industry version.";
        }

        if (!string.Equals(ModVersion, peer.ModVersion, StringComparison.Ordinal)) {
            return "Both players must use exactly the same SafeCoop version.";
        }

        if (!string.Equals(SaveFingerprint, peer.SaveFingerprint, StringComparison.Ordinal)) {
            return "The selected saves are different.";
        }

        if (!string.Equals(SessionKeyFingerprint, peer.SessionKeyFingerprint, StringComparison.Ordinal)) {
            return "The private session codes are different.";
        }

        return string.Empty;
    }

    private static string RequireValue(string value, string name) {
        if (string.IsNullOrWhiteSpace(value)) {
            throw new ArgumentException("A value is required.", name);
        }

        return value.Trim();
    }
}

public static class SessionFingerprint {
    public static string FromText(string value) {
        if (string.IsNullOrWhiteSpace(value)) {
            throw new ArgumentException("A value is required.", nameof(value));
        }

        return FromBytes(Encoding.UTF8.GetBytes(value.Trim()));
    }

    public static string FromBytes(byte[] data) {
        if (data == null || data.Length == 0) {
            throw new ArgumentException("Save data is required.", nameof(data));
        }

        using (var sha256 = SHA256.Create()) {
            var hash = sha256.ComputeHash(data);
            var builder = new StringBuilder(hash.Length * 2);
            foreach (var value in hash) {
                builder.Append(value.ToString("x2"));
            }

            return builder.ToString();
        }
    }

    public static string FromFile(string filePath) {
        if (string.IsNullOrWhiteSpace(filePath)) {
            throw new ArgumentException("A file path is required.", nameof(filePath));
        }

        using (var stream = File.OpenRead(filePath))
        using (var sha256 = SHA256.Create()) {
            var hash = sha256.ComputeHash(stream);
            var builder = new StringBuilder(hash.Length * 2);
            foreach (var value in hash) {
                builder.Append(value.ToString("x2"));
            }

            return builder.ToString();
        }
    }
}
