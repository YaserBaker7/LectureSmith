using System;
using System.IO;
using System.Threading.Tasks;

namespace LectureSmith.Services;

public class TempFileCleanupService
{
    /// <summary>
    /// Sweeps the LectureSmith temporary directory and removes subdirectories older than 24 hours.
    /// </summary>
    public static void RunBackgroundCleanup()
    {
        Task.Run(() =>
        {
            try
            {
                var tempBaseDir = Path.Combine(Path.GetTempPath(), "LectureSmith");
                if (!Directory.Exists(tempBaseDir)) return;

                var cutoff = DateTime.Now.AddHours(-24);
                var dirInfo = new DirectoryInfo(tempBaseDir);

                foreach (var subDir in dirInfo.GetDirectories())
                {
                    if (subDir.LastWriteTime < cutoff)
                    {
                        try
                        {
                            subDir.Delete(true);
                        }
                        catch
                        {
                            // File might be locked by another process, ignore
                        }
                    }
                }
            }
            catch
            {
                // Silently swallow errors during background cleanup
            }
        });
    }
}
