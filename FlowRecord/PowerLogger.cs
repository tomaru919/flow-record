using System.Diagnostics;
using System.IO;

namespace FlowRecord.Logging;

// Debug.WriteLine はデバッガ未アタッチ時に見えないため、スリープ/復帰まわりの診断をファイルにも残す
public static class PowerLogger {
    private static string AppDataDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FlowRecord"
        );

#if DEBUG
    private static string LogPath => Path.Combine(AppDataDir, "power.debug.log");
#else
    private static string LogPath => Path.Combine(AppDataDir, "power.log");
#endif

    public static void Log(string message) {
        try {
            Directory.CreateDirectory(AppDataDir);
            File.AppendAllText(LogPath, $"{DateTime.Now:O} {message}{Environment.NewLine}");
        } catch (Exception ex) {
            Debug.WriteLine($"Error logging power event: {ex.Message}");
        }
    }
}
