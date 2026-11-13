using System.IO;

namespace SYSMR.Infrastructure;

/// <summary>Log de debug simples em arquivo para diagnóstico em produção.</summary>
public static class DebugLog
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SYSMR",
        "sysmr-debug.log");
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}";
            lock (Gate)
            {
                File.AppendAllText(LogPath, line);
            }
        }
        catch
        {
            // Não deixa o log derrubar a aplicação.
        }
    }

    public static string GetLogPath() => LogPath;

    public static void Clear()
    {
        try
        {
            lock (Gate)
            {
                if (File.Exists(LogPath))
                    File.Delete(LogPath);
            }
        }
        catch
        {
            // Ignora.
        }
    }
}
