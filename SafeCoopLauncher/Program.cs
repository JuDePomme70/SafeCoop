namespace SafeCoopLauncher;

internal static class Program {
    [STAThread]
    private static void Main(string[] args) {
        if (args.Length == 3 && string.Equals(args[0], "--apply-update", StringComparison.Ordinal)) {
            LauncherUpdater.ApplyPendingUpdate(args[1], args[2]);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
