using System.IO;

namespace LightingControl;

/// <summary>Timestamped trace of device discovery, kept small, for diagnosing slow starts after logon.</summary>
internal static class Diag
{
    private static readonly string Path_ = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LightingControl", "diag.log");
    private static readonly object Sync = new();

    public static string FilePath => Path_;

    public static void Log(string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path_)!);
                if (File.Exists(Path_) && new FileInfo(Path_).Length > 200_000)
                    File.Delete(Path_);
                File.AppendAllText(Path_, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch (IOException)
        {
        }
    }
}
