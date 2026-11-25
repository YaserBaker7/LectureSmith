using System.Collections.Concurrent;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using Tesseract;

namespace LectureSmith.Services;

public class OcrService : IOcrService
{
    private static readonly object NativeInitLock = new();
    private static bool _nativePathConfigured;

    private const string TrainedDataUrl =
        "https://github.com/tesseract-ocr/tessdata/raw/main/eng.traineddata";

    private bool _disposed;

    public async Task EnsureTrainedDataAsync(IProgress<string>? status = null, CancellationToken ct = default)
    {
        if (GetTessDataFolder() != null)
            return;

        Directory.CreateDirectory(AppPaths.TessDataDir);
        var targetFile = Path.Combine(AppPaths.TessDataDir, "eng.traineddata");
        var tempFile = $"{targetFile}.{Guid.NewGuid():N}.tmp";

        status?.Report("Downloading Tesseract OCR language data…");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            using var response = await http.GetAsync(TrainedDataUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            await using (var stream = await response.Content.ReadAsStreamAsync(ct))
            await using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await stream.CopyToAsync(fs, ct);
            }

            File.Move(tempFile, targetFile, overwrite: true);
            status?.Report("Tesseract OCR language data downloaded.");
        }
        finally
        {
            try
            {
                if (File.Exists(tempFile))
                    File.Delete(tempFile);
            }
            catch { }
        }
    }

    public async Task<List<string>> ExtractTextFromImagesAsync(List<string> imagePaths,
        IProgress<(int current, int total)>? progress = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (imagePaths.Count == 0) return [];

        EnsureNativeLibraryPath();
        var tessDataFolder = GetTessDataFolder()
            ?? throw new FileNotFoundException($"Tesseract trained data ('eng.traineddata') was not found in '{AppPaths.TessDataDir}'.");

        Environment.SetEnvironmentVariable("TESSDATA_PREFIX", tessDataFolder);

        var results = new string[imagePaths.Count];
        int completedCount = 0;
        int failedCount = 0;
        Exception? firstError = null;
        int maxDegree = Math.Clamp(Environment.ProcessorCount, 1, 8);
        var enginePool = new ConcurrentBag<TesseractEngine>();

        try
        {
            // Warm up native Leptonica/TessApi initialization on a single thread before fanning out
            enginePool.Add(CreateCompatibleEngine(tessDataFolder));

            await Parallel.ForAsync(0, imagePaths.Count,
                new ParallelOptions { MaxDegreeOfParallelism = maxDegree, CancellationToken = ct },
                (i, cancelToken) =>
                {
                    TesseractEngine? localEngine = null;
                    try
                    {
                        if (!enginePool.TryTake(out localEngine))
                            localEngine = CreateCompatibleEngine(tessDataFolder);

                        using var img = Pix.LoadFromFile(imagePaths[i]);
                        using var page = localEngine.Process(img);
                        results[i] = page.GetText().Trim();
                    }
                    catch (OperationCanceledException) when (cancelToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref failedCount);
                        Interlocked.CompareExchange(ref firstError, UnwrapException(ex), null);
                        results[i] = string.Empty;
                    }
                    finally
                    {
                        if (localEngine != null)
                            enginePool.Add(localEngine);
                    }

                    var count = Interlocked.Increment(ref completedCount);
                    progress?.Report((count, imagePaths.Count));
                    return ValueTask.CompletedTask;
                });
        }
        finally
        {
            while (enginePool.TryTake(out var engine))
                engine.Dispose();
        }

        if (failedCount == imagePaths.Count && firstError != null)
        {
            throw new InvalidOperationException(
                $"OCR processing failed on all {imagePaths.Count} slides: {firstError.Message}", firstError);
        }

        return [.. results];
    }

    public void Dispose()
    {
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private static Exception UnwrapException(Exception ex)
    {
        while (ex is TargetInvocationException tie && tie.InnerException != null)
            ex = tie.InnerException;
        return ex;
    }

    private static TesseractEngine CreateCompatibleEngine(string tessDataFolder)
    {
        Exception? lastError = null;
        foreach (var mode in new[] { EngineMode.Default, EngineMode.LstmOnly, EngineMode.TesseractOnly })
        {
            try
            {
                return new TesseractEngine(tessDataFolder, "eng", mode);
            }
            catch (Exception ex)
            {
                lastError = UnwrapException(ex);
            }
        }

        throw new InvalidOperationException(
            $"Failed to initialize Tesseract OCR engine from '{tessDataFolder}': {lastError?.Message}", lastError);
    }

    /// <summary>
    /// Resolves the directory containing Tesseract's architecture subfolder (e.g. x64/tesseract50.dll),
    /// supporting both regular builds (AppContext.BaseDirectory) and single-file self-extract bundles
    /// (NATIVE_DLL_SEARCH_DIRECTORIES in %TEMP%\.net\...).
    /// </summary>
    internal static (string? ParentDir, string? NativeArchDir) ResolveTesseractNativeFolders(
        string? baseDirectory, string? nativeSearchDirs, string arch)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(nativeSearchDirs))
        {
            foreach (var dir in nativeSearchDirs.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                candidates.Add(dir.Trim());
        }
        if (!string.IsNullOrWhiteSpace(baseDirectory))
            candidates.Add(baseDirectory.Trim());

        foreach (var dir in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var archDir = Path.Combine(dir, arch);
            if (File.Exists(Path.Combine(archDir, "tesseract50.dll")))
                return (dir, archDir);

            if (File.Exists(Path.Combine(dir, "tesseract50.dll")))
                return (Directory.GetParent(dir)?.FullName ?? dir, dir);
        }

        return (null, null);
    }

    private static void EnsureNativeLibraryPath()
    {
        if (_nativePathConfigured) return;

        lock (NativeInitLock)
        {
            if (_nativePathConfigured) return;

            var arch = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "x64",
                Architecture.X86 => "x86",
                Architecture.Arm64 => "arm64",
                _ => "x64"
            };

            var nativeSearchDirs = AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") as string;
            var (parentDir, nativePath) = ResolveTesseractNativeFolders(AppContext.BaseDirectory, nativeSearchDirs, arch);

            if (string.IsNullOrEmpty(nativePath))
            {
                _nativePathConfigured = true;
                return;
            }

            // Crucial for .NET single-file apps: point Tesseract's InteropDotNet.LibraryLoader directly
            // to the parent folder containing x64\ since Assembly.Location is empty in single-file mode.
            if (!string.IsNullOrEmpty(parentDir))
                TesseractEnviornment.CustomSearchPath = parentDir;

            var currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            if (!currentPath.Contains(nativePath, StringComparison.OrdinalIgnoreCase))
                Environment.SetEnvironmentVariable("PATH", $"{nativePath};{currentPath}");

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    AddDllDirectory(nativePath);
                }
                catch { }
            }

            foreach (var dllName in new[] { "leptonica-1.82.0", "tesseract50" })
            {
                var dllPath = Path.Combine(nativePath, $"{dllName}.dll");
                if (File.Exists(dllPath))
                    NativeLibrary.Load(dllPath);
            }

            _nativePathConfigured = true;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AddDllDirectory(string newDirectory);

    private static string? GetTessDataFolder()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "tessdata"),
            AppPaths.TessDataDir,
            @"C:\Program Files\Tesseract-OCR\tessdata"
        };

        return candidates.FirstOrDefault(dir =>
            Directory.Exists(dir) && File.Exists(Path.Combine(dir, "eng.traineddata")));
    }
}
