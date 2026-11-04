namespace Limona.Interop;

/// <summary>Debug tracing for startup/crash diagnostics (dev only).</summary>
internal static class Dbg
{
    private static readonly object Lock = new();
    private static readonly string LogFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Limona", "debug.log");

    public static void Log(string message)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
                using var fs = new FileStream(LogFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(fs);
                writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {message}");
            }
        }
        catch
        {
            // Never let tracing break the app.
        }
    }
}
