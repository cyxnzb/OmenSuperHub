using System;
using System.IO;

namespace OmenSuperHub {
  public static class Logger {
    public static readonly string logFileName = "OmenSuperHub.log";
    private static readonly string LogPath
        = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, logFileName);
    private static readonly object FileLock = new object();

    // Keep diagnostics bounded. A hardware utility can run for weeks, so an
    // unbounded log file is both unnecessary and harder to share when debugging.
    private const long MaxLogBytes = 2L * 1024L * 1024L;
    private const int MaxArchivedLogs = 3;

    // Throttle identical messages so repeated hardware polling failures do not
    // flood the log. Access is protected by FileLock to keep the state coherent
    // across timer/background threads.
    private static string lastMessage = "";
    private static DateTime lastWriteTime = DateTime.MinValue;
    private const int ThrottleSeconds = 30;

    public static void Info(string message) {
      Console.WriteLine(message);
      WriteToFile("INFO", message);
    }

    public static void Warn(string message) {
      Console.WriteLine("[WARN] " + message);
      WriteToFile("WARN", message);
    }

    public static void Error(string message) {
      Console.WriteLine("[ERROR] " + message);
      WriteToFile("ERROR", message);
    }

    public static void Error(string message, Exception exception) {
      string detail = exception == null
          ? message
          : message + Environment.NewLine + exception;
      Console.WriteLine("[ERROR] " + detail);
      WriteToFile("ERROR", detail);
    }

    private static void WriteToFile(string level, string message) {
      string normalizedMessage = message ?? "";
      string throttleKey = level + ":" + normalizedMessage;

      lock (FileLock) {
        DateTime now = DateTime.Now;
        if (throttleKey == lastMessage &&
            (now - lastWriteTime).TotalSeconds < ThrottleSeconds) {
          return;
        }

        try {
          RotateIfNeeded();
          File.AppendAllText(
              LogPath,
              string.Format(
                  "{0:yyyy-MM-dd HH:mm:ss.fff} [{1}] [T{2}] {3}{4}",
                  now,
                  level,
                  System.Threading.Thread.CurrentThread.ManagedThreadId,
                  normalizedMessage,
                  Environment.NewLine));

          // Only throttle after a successful write. A transient filesystem
          // failure should not suppress the next diagnostic attempt.
          lastMessage = throttleKey;
          lastWriteTime = now;
        } catch (Exception ex) {
          // Logging must never be able to take down hardware control or the UI.
          try {
            Console.Error.WriteLine("Logger write failed: " + ex.Message);
          } catch { }
        }
      }
    }

    private static void RotateIfNeeded() {
      if (!File.Exists(LogPath)) return;

      FileInfo info = new FileInfo(LogPath);
      if (info.Length < MaxLogBytes) return;

      string oldest = LogPath + "." + MaxArchivedLogs;
      if (File.Exists(oldest)) {
        File.Delete(oldest);
      }

      for (int i = MaxArchivedLogs - 1; i >= 1; i--) {
        string source = LogPath + "." + i;
        string destination = LogPath + "." + (i + 1);
        if (!File.Exists(source)) continue;
        if (File.Exists(destination)) File.Delete(destination);
        File.Move(source, destination);
      }

      string firstArchive = LogPath + ".1";
      if (File.Exists(firstArchive)) File.Delete(firstArchive);
      File.Move(LogPath, firstArchive);
    }
  }
}
