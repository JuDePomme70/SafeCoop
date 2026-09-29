using System;
using System.Net;
using System.Net.NetworkInformation;
using Mafi.Core.Mods;

namespace SafeCoop.Network;

/// <summary>
/// Per-save session settings. The machine whose Tailscale address matches
/// HostAddress becomes the host; every other machine joins it. This means the
/// same copied save can be used on both PCs without a separate host/guest flag.
/// </summary>
public sealed class LanSessionSettings {
    private LanSessionSettings(IPAddress hostAddress, int port, string sessionCode) {
        HostAddress = hostAddress;
        Port = port;
        SessionCode = sessionCode;
    }

    public IPAddress HostAddress { get; }
    public int Port { get; }
    public string SessionCode { get; }

    public bool IsHostMachine => IsLocalAddress(HostAddress);

    public static bool TryCreate(ModJsonConfig config, out LanSessionSettings? settings, out string message) {
        settings = null;
        message = string.Empty;
        if (!config.GetBool("enable_lan_session")) {
            return false;
        }

        var hostText = config.GetString("host_address");
        if (!IPAddress.TryParse(hostText, out var hostAddress) || !IsTailscaleAddress(hostAddress)) {
            message = "SafeCoop needs the host PC's Tailscale address (100.x.x.x).";
            return false;
        }

        var sessionCode = config.GetString("session_code").Trim();
        if (sessionCode.Length < 8) {
            message = "SafeCoop needs a session code of at least 8 characters.";
            return false;
        }

        var port = config.GetInt("session_port");
        if (port < IPEndPoint.MinPort || port > IPEndPoint.MaxPort) {
            message = "SafeCoop session port is invalid.";
            return false;
        }

        settings = new LanSessionSettings(hostAddress, port, sessionCode);
        return true;
    }

    private static bool IsLocalAddress(IPAddress expectedAddress) {
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces()) {
            var properties = networkInterface.GetIPProperties();
            foreach (var unicastAddress in properties.UnicastAddresses) {
                if (expectedAddress.Equals(unicastAddress.Address)) {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsTailscaleAddress(IPAddress address) {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127;
    }
}
