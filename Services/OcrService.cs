using System.Net.Http;
using System.Runtime.InteropServices;
using Tesseract;

namespace LectureSmith.Services;

public class OcrService : IDisposable
{
    private TesseractEngine? _engine;
    private bool _disposed;
    private static bool _nativePathConfigured;

    private const string TrainedDataUrl =
        "https://github.com/tesseract-ocr/tessdata/raw/main/eng.traineddata";

    /// <summary>
    /// Ensures the tessdata files are available, downloading them if necessary.
    /// Call this before using OCR to give the user a chance to see download progress.
    /// </summary>
    public async Task EnsureTrainedDataAsync(IProgress<string>? status = null)
    {
        var tessDataFolder = GetTessDataFolder();
        if (tessDataFolder != null) return; // Already available

        var localDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LectureSmith", "tessdata");
        Directory.CreateDirectory(localDir);

        var targetFile = Path.Combine(localDir, "eng.traineddata");

        status?.Report("Downloading Tesseract OCR language data…");
        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromMinutes(5);

        var response = await http.GetAsync(TrainedDataUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync();
        await using var fs = new FileStream(targetFile, FileMode.Create, FileAccess.Write, FileShare.None);
        await stream.CopyToAsync(fs);

        status?.Report("Tesseract OCR language data downloaded.");
    }

    /// <summary>
    /// Runs OCR on a single image file and returns the recognised text.
    /// </summary>
    public async Task<string> ExtractTextFromImageAsync(string imagePath)
    {
        ThrowIfDisposed();
        return await Task.Run(() =>
        {
            var engine = GetEngine();
            using var img = Pix.LoadFromFile(imagePath);
            using var page = engine.Process(img);
            return page.GetText().Trim();
        });
    }

    /// <summary>
    /// Runs OCR on multiple slide images concurrently using multi-core processing. Returns one string per slide in order.
    /// </summary>
    public async Task<List<string>> ExtractTextFromImagesAsync(List<string> imagePaths,
        IProgress<(int current, int total)>? progress = null)
    {
        ThrowIfDisposed();
        if (imagePaths.Count == 0) return [];

        EnsureNativeLibraryPath();
        var tessDataFolder = GetTessDataFolder();
        if (tessDataFolder == null) throw new InvalidOperationException("Tesseract trained data not found.");

        var results = new string[imagePaths.Count];
        int completedCount = 0;
        int maxDegree = Math.Clamp(Environment.ProcessorCount, 1, 8);

        await Parallel.ForAsync(0, imagePaths.Count, new ParallelOptions { MaxDegreeOfParallelism = maxDegree }, (i, ct) =>
        {
            try
            {
                using var localEngine = new TesseractEngine(tessDataFolder, "eng", EngineMode.Default);
                using var img = Pix.LoadFromFile(imagePaths[i]);
                using var page = localEngine.Process(img);
                results[i] = page.GetText().Trim();
            }
            catch
            {
                results[i] = string.Empty;
            }

            var count = Interlocked.Increment(ref completedCount);
            progress?.Report((count, imagePaths.Count));
            return ValueTask.CompletedTask;
        });

        return [.. results];
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _engine?.Dispose();
            _engine = null;
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }

    // ── Private helpers ───────────────────────────────────────────────

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    /// <summary>
    /// Ensures the native Tesseract/Leptonica DLLs are loaded into the process.
    /// The Tesseract NuGet places them in a platform subfolder (e.g. x64/).
    /// </summary>
    private static void EnsureNativeLibraryPath()
    {
        if (_nativePathConfigured) return;

        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            _ => "x64"
        };

        var nativePath = Path.Combine(AppContext.BaseDirectory, arch);
        if (!Directory.Exists(nativePath))
        {
            _nativePathConfigured = true;
            return;
        }

        // 1. Add to PATH so child P/Invoke calls can find dependencies
        var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
        if (!currentPath.Contains(nativePath, StringComparison.OrdinalIgnoreCase))
        {
            Environment.SetEnvironmentVariable("PATH", $"{nativePath};{currentPath}");
        }

        // 2. Use Win32 AddDllDirectory for the OS-level loader
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            AddDllDirectory(nativePath);
        }

        // 3. Explicitly preload the native DLLs so they're already in memory
        //    when the Tesseract wrapper tries to resolve them.
        foreach (var dllName in new[] { "leptonica-1.82.0", "tesseract50" })
        {
            var dllPath = Path.Combine(nativePath, $"{dllName}.dll");
            if (File.Exists(dllPath))
            {
                NativeLibrary.Load(dllPath);
            }
        }

        _nativePathConfigured = true;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int AddDllDirectory(string newDirectory);

    /// <summary>
    /// Finds the tessdata folder from known candidate locations.
    /// Returns null if not found.
    /// </summary>
    private static string? GetTessDataFolder()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "tessdata"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LectureSmith", "tessdata"),
            @"C:\Program Files\Tesseract-OCR\tessdata"
        };

        return candidates.FirstOrDefault(dir =>
            Directory.Exists(dir) && File.Exists(Path.Combine(dir, "eng.traineddata")));
    }

    /// <summary>
    /// Lazily initialises the Tesseract engine, searching for tessdata in common locations.
    /// </summary>
    private TesseractEngine GetEngine()
    {
        if (_engine != null) return _engine;

        // Make sure native DLLs are on the search path
        EnsureNativeLibraryPath();

        var tessDataFolder = GetTessDataFolder();

        if (tessDataFolder == null)
        {
            var localDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LectureSmith", "tessdata");
            Directory.CreateDirectory(localDir);
            throw new FileNotFoundException(
                $"Tesseract trained data not found. Please download 'eng.traineddata' from " +
                $"{TrainedDataUrl} " +
                $"and place it in: {localDir}");
        }

        // The TesseractEngine constructor in Tesseract NuGet 5.2.0 expects the path
        // to the tessdata folder directly (the folder containing eng.traineddata).
        var dataPath = tessDataFolder;

        // Set TESSDATA_PREFIX to help Tesseract locate the tessdata folder
        Environment.SetEnvironmentVariable("TESSDATA_PREFIX", dataPath);

        // Try multiple engine modes for maximum compatibility
        TesseractException? lastError = null;
        foreach (var mode in new[] { EngineMode.Default, EngineMode.LstmOnly, EngineMode.TesseractOnly })
        {
            try
            {
                _engine = new TesseractEngine(dataPath, "eng", mode);
                return _engine;
            }
            catch (TesseractException ex)
            {
                lastError = ex;
                // Try next mode
            }
        }

        var fileSize = new FileInfo(Path.Combine(tessDataFolder, "eng.traineddata")).Length;
        throw new InvalidOperationException(
            $"Failed to initialise Tesseract OCR engine (tried all engine modes). " +
            $"Data path: '{dataPath}', tessdata folder: '{tessDataFolder}'. " +
            $"eng.traineddata size: {fileSize} bytes. " +
            $"Please ensure you have the correct eng.traineddata from " +
            $"{TrainedDataUrl} — " +
            $"Inner error: {lastError?.Message}", lastError);
    }
}
