using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SafeCoopLauncher;

internal sealed class MainForm : Form {
    private const int DefaultPort = 27960;
    private readonly TextBox hostAddress = new() { ReadOnly = true, Dock = DockStyle.Top };
    private readonly TextBox joinCode = new() { ReadOnly = true, Dock = DockStyle.Top };
    private readonly TextBox pastedJoinCode = new() { Dock = DockStyle.Top, PlaceholderText = "Colle ici le code re?u de l?h?te" };
    private readonly Label status = new() { AutoSize = false, Height = 50, Dock = DockStyle.Bottom, TextAlign = ContentAlignment.MiddleLeft };
    private readonly System.Windows.Forms.Timer sessionStatusTimer = new() { Interval = 1000 };
    private readonly NotifyIcon sessionNotifier = new() { Icon = System.Drawing.SystemIcons.Application, Visible = true };
    private long latestStatusTicks;
    private string latestStatusState = string.Empty;
    private bool watchSessionStatus;

    public MainForm() {
        Text = "SafeCoop Launcher";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        ClientSize = new Size(580, 365);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(CreateHostPage());
        tabs.TabPages.Add(CreateJoinPage());

        var launchButton = new Button { Text = "Lancer Captain of Industry", Dock = DockStyle.Bottom, Height = 38 };
        launchButton.Click += (_, _) => LaunchGame();

        Controls.Add(tabs);
        Controls.Add(status);
        Controls.Add(launchButton);
        RefreshHostAddress();
        SetStatus("Pr?t. Ferme le jeu avant de pr?parer une session.");
        sessionStatusTimer.Tick += (_, _) => RefreshSessionStatus();
        FormClosed += (_, _) => {
            sessionStatusTimer.Dispose();
            sessionNotifier.Dispose();
        };
    }

    private TabPage CreateHostPage() {
        var page = new TabPage("Cr?er une partie");
        var panel = CreatePanel();
        panel.Controls.Add(CreateLabel("1. V?rifie ton adresse Tailscale :"));
        panel.Controls.Add(hostAddress);

        var refresh = new Button { Text = "Actualiser l?adresse", AutoSize = true };
        refresh.Click += (_, _) => RefreshHostAddress();
        panel.Controls.Add(refresh);

        var prepare = new Button { Text = "Pr?parer ma partie", AutoSize = true };
        prepare.Click += (_, _) => PrepareHost();
        panel.Controls.Add(prepare);

        panel.Controls.Add(CreateLabel("2. Envoie ce code ? ton pote :"));
        panel.Controls.Add(joinCode);

        var copy = new Button { Text = "Copier le code", AutoSize = true };
        copy.Click += (_, _) => {
            if (!string.IsNullOrWhiteSpace(joinCode.Text)) {
                Clipboard.SetText(joinCode.Text);
                SetStatus("Code copi?. Envoie-le ? ton pote.");
            }
        };
        panel.Controls.Add(copy);
        panel.Controls.Add(CreateLabel("3. Laisse ce launcher ouvert : il t'indiquera quand ton pote rejoint ou quitte."));
        page.Controls.Add(panel);
        return page;
    }

    private TabPage CreateJoinPage() {
        var page = new TabPage("Rejoindre un pote");
        var panel = CreatePanel();
        panel.Controls.Add(CreateLabel("1. Colle le code envoy? par l?h?te :"));
        panel.Controls.Add(pastedJoinCode);

        var prepare = new Button { Text = "Pr?parer ma connexion", AutoSize = true };
        prepare.Click += (_, _) => PrepareGuest();
        panel.Controls.Add(prepare);
        panel.Controls.Add(CreateLabel("2. Lance le jeu avec la m?me sauvegarde de test que l'h?te."));
        panel.Controls.Add(CreateLabel("Le lanceur installe le bon mod et r?gle la connexion ; il ne copie ni n?envoie aucune sauvegarde."));
        page.Controls.Add(panel);
        return page;
    }

    private static FlowLayoutPanel CreatePanel() {
        return new FlowLayoutPanel {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(18),
            AutoScroll = true,
        };
    }

    private static Label CreateLabel(string text) {
        return new Label { Text = text, AutoSize = true, MaximumSize = new Size(520, 0), Margin = new Padding(0, 9, 0, 2) };
    }

    private void RefreshHostAddress() {
        hostAddress.Text = FindTailscaleAddress()?.ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(hostAddress.Text)) {
            SetStatus("Adresse Tailscale introuvable. Connecte Tailscale puis actualise.");
        }
    }

    private void PrepareHost() {
        if (!IPAddress.TryParse(hostAddress.Text, out var address) || !IsTailscaleAddress(address)) {
            SetStatus("Aucune adresse Tailscale valide n?a ?t? trouv?e.");
            return;
        }

        var code = CreateSecretCode();
        try {
            InstallAndConfigure(address, DefaultPort, code);
            joinCode.Text = $"SC1;{address};{DefaultPort};{code}";
            StartWatchingSessionStatus();
            SetStatus("Ta partie est pr?te. Copie le code pour ton pote, puis lance le jeu.");
        }
        catch (Exception exception) {
            SetStatus($"Pr?paration impossible : {exception.Message}");
        }
    }

    private void PrepareGuest() {
        if (!TryParseJoinCode(pastedJoinCode.Text, out var address, out var port, out var code)) {
            SetStatus("Le code est invalide. Demande ? l?h?te de le copier ? nouveau.");
            return;
        }

        try {
            InstallAndConfigure(address, port, code);
            StartWatchingSessionStatus();
            SetStatus("Connexion pr?te. Lance le jeu et ouvre la m?me sauvegarde que l?h?te.");
        }
        catch (Exception exception) {
            SetStatus($"Pr?paration impossible : {exception.Message}");
        }
    }

    private static void InstallAndConfigure(IPAddress host, int port, string code) {
        if (Process.GetProcessesByName("Captain of Industry").Length > 0) {
            throw new InvalidOperationException("Ferme Captain of Industry avant de modifier le mod.");
        }

        var target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Captain of Industry", "Mods", "SafeCoop");
        foreach (var fileName in new[] { "SafeCoop.dll", "manifest.json", "readme.txt" }) {
            Directory.CreateDirectory(target);
            WriteEmbeddedPayload(fileName, Path.Combine(target, fileName));
        }

        File.WriteAllText(Path.Combine(target, "config.json"), CreateConfig(host, port, code), new UTF8Encoding(false));
        WriteLauncherSessionStatus("Preparing", "Configuration termin?e. Lance Captain of Industry.");
    }

    private static void WriteEmbeddedPayload(string fileName, string destination) {
        var resourceName = $"SafeCoopLauncher.Payload.{fileName}";
        using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        if (source == null) {
            throw new FileNotFoundException("Le lanceur ne contient pas tous les fichiers SafeCoop.", resourceName);
        }

        using var output = File.Create(destination);
        source.CopyTo(output);
    }

    private static string CreateConfig(IPAddress host, int port, string code) {
        return $$"""
        {
          "enable_lan_session": { "default": true, "description": "Enable SafeCoop for this copied test save." },
          "host_address": { "default": "{{host}}", "max_length": 15, "regex": "^$|^100\\.(6[4-9]|[7-9][0-9]|1[0-1][0-9]|12[0-7])\\.([0-9]{1,3})\\.([0-9]{1,3})$", "description": "SafeCoop host Tailscale address." },
          "session_port": { "default": {{port}}, "min": 1024, "max": 65535, "is_integer": true, "description": "SafeCoop session port." },
          "session_code": { "default": "{{code}}", "max_length": 64, "description": "Private SafeCoop session code." }
        }
        """;
    }

    private static string SessionStatusFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Captain of Industry", "Mods", "SafeCoop", "session-status.json");

    private static void WriteLauncherSessionStatus(string state, string message) {
        var json = JsonSerializer.Serialize(new {
            state,
            message,
            updatedUtcTicks = DateTime.UtcNow.Ticks,
        });
        File.WriteAllText(SessionStatusFilePath, json, new UTF8Encoding(false));
    }

    private void StartWatchingSessionStatus() {
        latestStatusTicks = 0;
        latestStatusState = string.Empty;
        watchSessionStatus = true;
        sessionStatusTimer.Start();
        RefreshSessionStatus();
    }

    private void RefreshSessionStatus() {
        if (!watchSessionStatus || !File.Exists(SessionStatusFilePath)) {
            return;
        }

        try {
            using var document = JsonDocument.Parse(File.ReadAllText(SessionStatusFilePath));
            var root = document.RootElement;
            if (!root.TryGetProperty("updatedUtcTicks", out var ticksValue) || !ticksValue.TryGetInt64(out var ticks) || ticks <= latestStatusTicks ||
                !root.TryGetProperty("state", out var stateValue) || !root.TryGetProperty("message", out var messageValue)) {
                return;
            }

            var state = stateValue.GetString() ?? string.Empty;
            var message = messageValue.GetString() ?? string.Empty;
            latestStatusTicks = ticks;
            SetStatus(message);

            if (!string.Equals(state, latestStatusState, StringComparison.Ordinal) &&
                (string.Equals(state, "Connected", StringComparison.Ordinal) ||
                 string.Equals(state, "PeerDisconnected", StringComparison.Ordinal) ||
                 string.Equals(state, "Failed", StringComparison.Ordinal) ||
                 string.Equals(state, "Stopped", StringComparison.Ordinal))) {
                sessionNotifier.ShowBalloonTip(5000, "SafeCoop", message, ToolTipIcon.Info);
            }

            latestStatusState = state;
        }
        catch (IOException) {
            // The mod is updating the tiny status file; the next timer tick will retry.
        }
        catch (JsonException) {
            // Ignore a partially written status update and retry on the next tick.
        }
    }

    private static IPAddress? FindTailscaleAddress() {
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces()) {
            foreach (var unicastAddress in networkInterface.GetIPProperties().UnicastAddresses) {
                if (IsTailscaleAddress(unicastAddress.Address)) {
                    return unicastAddress.Address;
                }
            }
        }

        return null;
    }

    private static bool TryParseJoinCode(string value, out IPAddress address, out int port, out string code) {
        address = IPAddress.None;
        port = 0;
        code = string.Empty;
        var parts = value.Trim().Split(';');
        if (parts.Length != 4 || !string.Equals(parts[0], "SC1", StringComparison.Ordinal) ||
            parts[3].Length < 16 || parts[3].Length > 64 ||
            !IPAddress.TryParse(parts[1], out var parsedAddress) || parsedAddress == null ||
            !IsTailscaleAddress(parsedAddress) || !int.TryParse(parts[2], out port) ||
            port < 1024 || port > 65535) {
            return false;
        }

        address = parsedAddress;
        code = parts[3]!;
        return true;
    }

    private static bool IsTailscaleAddress(IPAddress address) {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127;
    }

    private static string CreateSecretCode() {
        var bytes = RandomNumberGenerator.GetBytes(18);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private void LaunchGame() {
        var game = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steamapps", "common", "Captain of Industry", "Captain of Industry.exe");
        if (!File.Exists(game)) {
            SetStatus("Je ne trouve pas le jeu dans son dossier Steam habituel. Lance-le depuis Steam.");
            return;
        }

        Process.Start(new ProcessStartInfo(game) {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(game)!,
        });
    }

    private void SetStatus(string message) {
        status.Text = message;
    }
}
