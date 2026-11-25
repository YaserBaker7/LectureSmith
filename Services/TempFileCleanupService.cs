namespace LectureSmith.Services;

public static class TempFileCleanupService
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
                if (!Directory.Exists(AppPaths.TempDir)) return;

                var cutoff = DateTime.Now.AddHours(-24);
                var dirInfo = new DirectoryInfo(AppPaths.TempDir);

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
                            // File might be locked by another process
                        }
                    }
                }
            }
            catch
            {
                // Silently ignore non-critical background cleanup errors
            }
        });
    }
}
