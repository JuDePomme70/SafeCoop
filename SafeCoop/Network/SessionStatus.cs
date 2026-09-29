using System;
using System.IO;
using System.Text;

namespace SafeCoop.Network;

/// <summary>Small, local-only state file read by the launcher to show session events.</summary>
public enum SessionStatus {
    Preparing,
    WaitingForSave,
    WaitingForPeer,
    Connecting,
    Connected,
    PeerDisconnected,
    Failed,
    Stopped,
}

public static class SessionStatusReporter {
    private static readonly object sync = new object();

    public static string StatusFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Captain of Industry", "Mods", "SafeCoop", "session-status.json");

    public static void Publish(SessionStatus state, string message) {
        try {
            lock (sync) {
                var directory = Path.GetDirectoryName(StatusFilePath);
                if (string.IsNullOrEmpty(directory)) {
                    return;
                }

                Directory.CreateDirectory(directory);
                var json = "{\"state\":\"" + Escape(state.ToString()) + "\",\"message\":\"" +
                    Escape(message) + "\",\"updatedUtcTicks\":" + DateTime.UtcNow.Ticks + "}";
                File.WriteAllText(StatusFilePath, json, new UTF8Encoding(false));
            }
        }
        catch (IOException) {
            // A status indicator must never interrupt the game session.
        }
        catch (UnauthorizedAccessException) {
            // A status indicator must never interrupt the game session.
        }
    }

    private static string Escape(string value) {
        return (value ?? string.Empty)
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r", " ")
            .Replace("\n", " ");
    }
}
