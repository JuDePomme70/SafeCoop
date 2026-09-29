using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace SafeCoopLauncher;

internal sealed record LauncherRelease(Version Version, string AssetUrl, string Sha256, long AssetSize);

internal static class LauncherUpdater {
    private const string Repository = "JuDePomme70/SafeCoop";
    private const long MaxAssetBytes = 250L * 1024 * 1024;
    private static readonly HttpClient httpClient = CreateHttpClient();

    public static async Task<LauncherRelease?> GetLatestReleaseAsync(Version currentVersion) {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
        request.Headers.UserAgent.ParseAdd($"SafeCoopLauncher/{currentVersion}");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");

        using var response = await httpClient.SendAsync(request).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync().ConfigureAwait(false));
        var root = document.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var remoteVersion) || remoteVersion <= currentVersion) {
            return null;
        }

        var expectedAssetName = $"SafeCoopLauncher_ready_{remoteVersion}.zip";
        foreach (var asset in root.GetProperty("assets").EnumerateArray()) {
            if (!string.Equals(asset.GetProperty("name").GetString(), expectedAssetName, StringComparison.Ordinal)) {
                continue;
            }

            var assetUrl = asset.GetProperty("browser_download_url").GetString() ?? string.Empty;
            var digest = asset.TryGetProperty("digest", out var digestValue) ? digestValue.GetString() ?? string.Empty : string.Empty;
            var size = asset.GetProperty("size").GetInt64();
            if (!Uri.TryCreate(assetUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
                !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
                !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) || digest.Length != "sha256:".Length + 64 ||
                size <= 0 || size > MaxAssetBytes) {
                throw new InvalidDataException("La mise ? jour publi?e n'est pas valide.");
            }

            return new LauncherRelease(remoteVersion, assetUrl, digest.Substring("sha256:".Length), size);
        }

        throw new InvalidDataException("Le fichier de mise ? jour officiel est introuvable.");
    }

    public static async Task PrepareAndStartUpdateAsync(LauncherRelease release) {
        var currentExecutable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentExecutable) || !File.Exists(currentExecutable)) {
            throw new InvalidOperationException("Impossible de localiser le launcher actuel.");
        }

        var updateDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SafeCoop", "updates", release.Version.ToString());
        Directory.CreateDirectory(updateDirectory);
        var archivePath = Path.Combine(updateDirectory, "SafeCoopLauncher.zip");
        var replacementPath = Path.Combine(updateDirectory, "SafeCoopLauncher.exe");

        await DownloadAndVerifyAsync(release, archivePath).ConfigureAwait(false);
        ExtractLauncherOnly(archivePath, replacementPath);

        Process.Start(new ProcessStartInfo(replacementPath, $"--apply-update \"{currentExecutable}\" {Environment.ProcessId}") {
            UseShellExecute = true,
            WorkingDirectory = updateDirectory,
        });
    }

    public static void ApplyPendingUpdate(string targetPath, string previousProcessId) {
        if (!int.TryParse(previousProcessId, out var processId) || !Path.IsPathFullyQualified(targetPath) ||
            !string.Equals(Path.GetFileName(targetPath), "SafeCoopLauncher.exe", StringComparison.OrdinalIgnoreCase)) {
            return;
        }

        try {
            using var previousProcess = Process.GetProcessById(processId);
            previousProcess.WaitForExit(30_000);
        }
        catch (ArgumentException) {
            // The old launcher already exited before the updater started.
        }

        var replacementPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(replacementPath) || !File.Exists(replacementPath)) {
            return;
        }

        for (var attempt = 0; attempt < 30; attempt++) {
            try {
                File.Copy(replacementPath, targetPath, true);
                Process.Start(new ProcessStartInfo(targetPath) {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(targetPath)!,
                });
                return;
            }
            catch (IOException) when (attempt < 29) {
                Thread.Sleep(500);
            }
            catch (UnauthorizedAccessException) {
                return;
            }
        }
    }

    private static async Task DownloadAndVerifyAsync(LauncherRelease release, string archivePath) {
        using var request = new HttpRequestMessage(HttpMethod.Get, release.AssetUrl);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long contentLength && contentLength != release.AssetSize) {
            throw new InvalidDataException("La taille du t?l?chargement ne correspond pas ? la version officielle.");
        }

        using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var output = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        using var hasher = SHA256.Create();
        var buffer = new byte[81920];
        long totalBytes = 0;
        while (true) {
            var read = await input.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
            if (read == 0) {
                break;
            }

            totalBytes += read;
            if (totalBytes > release.AssetSize || totalBytes > MaxAssetBytes) {
                throw new InvalidDataException("La mise ? jour est trop volumineuse.");
            }

            hasher.TransformBlock(buffer, 0, read, null, 0);
            await output.WriteAsync(buffer, 0, read).ConfigureAwait(false);
        }

        hasher.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        var actualHash = Convert.ToHexString(hasher.Hash!).ToLowerInvariant();
        if (totalBytes != release.AssetSize || !CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(actualHash), Convert.FromHexString(release.Sha256))) {
            throw new InvalidDataException("La v?rification de s?curit? de la mise ? jour a ?chou?.");
        }
    }

    private static void ExtractLauncherOnly(string archivePath, string replacementPath) {
        using var archive = ZipFile.OpenRead(archivePath);
        var launcherEntries = archive.Entries.Where(entry =>
            string.Equals(entry.FullName, "SafeCoopLauncher.exe", StringComparison.OrdinalIgnoreCase) && entry.Length > 0).ToArray();
        if (launcherEntries.Length != 1) {
            throw new InvalidDataException("Le paquet de mise ? jour ne contient pas le launcher attendu.");
        }

        using var input = launcherEntries[0].Open();
        using var output = new FileStream(replacementPath, FileMode.Create, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
    }

    private static HttpClient CreateHttpClient() {
        return new HttpClient {
            Timeout = TimeSpan.FromMinutes(2),
        };
    }
}
