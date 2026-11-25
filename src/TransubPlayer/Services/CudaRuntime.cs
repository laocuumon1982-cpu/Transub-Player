using System.IO.Compression;

namespace TransubPlayer.Services;

/// <summary>
/// Optional NVIDIA CUDA runtime for Whisper (ASR) and llama-server (MT). Usually faster than Vulkan
/// on GeForce cards. Downloaded on demand (~620 MB: cudart/cuBLAS + llama CUDA build + Whisper.net CUDA)
/// into <c>{app data}/cuda</c>; Vulkan stays as the automatic fallback when CUDA is missing or fails.
/// Layout:
/// <list type="bullet">
/// <item><c>cuda/cudart/</c> — cudart64_12 / cublas64_12 / cublasLt64_12 (shared, put on PATH)</item>
/// <item><c>cuda/llama/llama-server.exe</c> — llama.cpp CUDA build</item>
/// <item><c>cuda/whisper/runtimes/cuda/win-x64/</c> — Whisper.net CUDA natives (via <c>RuntimeOptions.LibraryPath</c>)</item>
/// </list>
/// </summary>
internal static class CudaRuntime
{
    /// <summary>Must match the Whisper.net package version in TransubPlayer.csproj.</summary>
    public const string WhisperNetVersion = "1.8.1";
    private const string CudaVersion = "12.4";

    private static readonly object PathGate = new();
    private static bool _pathPrepended;
    private static int _installing;

    public static string Root => Path.Combine(AppPaths.AppDataDir, "cuda");
    public static string CudartDir => Path.Combine(Root, "cudart");
    public static string LlamaDir => Path.Combine(Root, "llama");
    public static string WhisperRoot => Path.Combine(Root, "whisper");
    public static string WhisperNativeDir => Path.Combine(WhisperRoot, "runtimes", "cuda", "win-x64");

    private static string CudartUrl =>
        $"https://github.com/ggml-org/llama.cpp/releases/download/{ManagedLlmCatalog.LlamaCppTag}/cudart-llama-bin-win-cuda-{CudaVersion}-x64.zip";
    private static string LlamaCudaUrl =>
        $"https://github.com/ggml-org/llama.cpp/releases/download/{ManagedLlmCatalog.LlamaCppTag}/llama-{ManagedLlmCatalog.LlamaCppTag}-bin-win-cuda-{CudaVersion}-x64.zip";
    private static string WhisperCudaUrl =>
        $"https://www.nuget.org/api/v2/package/Whisper.net.Runtime.Cuda.Windows/{WhisperNetVersion}";

    private static string Marker(string dir, string tag) => Path.Combine(dir, $".ready-{tag}");

    public static bool CudartReady => File.Exists(Marker(CudartDir, CudaVersion))
                                      && File.Exists(Path.Combine(CudartDir, "cudart64_12.dll"));
    public static bool LlamaReady => CudartReady
                                     && File.Exists(Marker(LlamaDir, ManagedLlmCatalog.LlamaCppTag))
                                     && File.Exists(Path.Combine(LlamaDir, "llama-server.exe"));
    public static bool WhisperReady => CudartReady
                                       && File.Exists(Marker(WhisperRoot, WhisperNetVersion))
                                       && File.Exists(Path.Combine(WhisperNativeDir, "whisper.dll"));

    /// <summary>User wants CUDA and the machine has an NVIDIA GPU.</summary>
    public static bool Wanted(AppSettings settings) => settings.UseCuda && GpuAdapterProbe.HasNvidia;

    /// <summary>CUDA llama-server.exe when installed and wanted; otherwise null (use Vulkan build).</summary>
    public static string? LlamaExe(AppSettings settings)
        => Wanted(settings) && LlamaReady ? Path.Combine(LlamaDir, "llama-server.exe") : null;

    /// <summary>Put cudart/cuBLAS on this process's PATH (inherited by llama-server).</summary>
    public static void PrependPath()
    {
        if (!CudartReady) return;
        lock (PathGate)
        {
            if (_pathPrepended) return;
            var cur = Environment.GetEnvironmentVariable("PATH") ?? "";
            Environment.SetEnvironmentVariable("PATH", CudartDir + Path.PathSeparator + cur);
            _pathPrepended = true;
        }
    }

    /// <summary>Background download/extract of missing CUDA parts. Safe to call repeatedly.</summary>
    public static async Task EnsureAsync(AppSettings settings, Action<string> log, CancellationToken ct)
    {
        if (!Wanted(settings)) return;
        if (CudartReady && LlamaReady && WhisperReady) return;
        if (Interlocked.CompareExchange(ref _installing, 1, 0) != 0) return;
        try
        {
            await ModelDownloadActivity.RunAsync(async token =>
            {
                Directory.CreateDirectory(Root);
                if (!CudartReady)
                {
                    log("CUDA: tải cudart/cuBLAS (~370 MB)…");
                    await DownloadExtractAsync(CudartUrl, CudartDir, CudaVersion,
                        e => e.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase),
                        e => e.Name, 100_000_000, log, token).ConfigureAwait(false);
                }

                if (!LlamaReady)
                {
                    log("CUDA: tải llama-server CUDA (~240 MB)…");
                    await DownloadExtractAsync(LlamaCudaUrl, LlamaDir, ManagedLlmCatalog.LlamaCppTag,
                        e => !string.IsNullOrEmpty(e.Name),
                        e => e.Name, 50_000_000, log, token).ConfigureAwait(false);
                }

                if (!WhisperReady)
                {
                    log("CUDA: tải Whisper.net CUDA runtime…");
                    // nupkg = zip; natives live under build/win-x64/.
                    await DownloadExtractAsync(WhisperCudaUrl, WhisperNativeDir, WhisperNetVersion,
                        e => e.FullName.Replace('\\', '/').StartsWith("build/win-x64/", StringComparison.OrdinalIgnoreCase)
                             && e.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase),
                        e => e.Name, 5_000_000, log, token, markerDir: WhisperRoot).ConfigureAwait(false);
                }

                log("CUDA: sẵn sàng. Dịch dùng CUDA từ lần khởi động dịch tiếp theo; nhận dạng giọng nói dùng CUDA sau khi mở lại app.");
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
        catch (Exception ex)
        {
            log("CUDA: cài đặt thất bại, tiếp tục dùng Vulkan · " + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _installing, 0);
        }
    }

    private static async Task DownloadExtractAsync(
        string url,
        string destDir,
        string tag,
        Func<ZipArchiveEntry, bool> include,
        Func<ZipArchiveEntry, string> targetName,
        long minBytes,
        Action<string> log,
        CancellationToken ct,
        string? markerDir = null)
    {
        var zip = Path.Combine(Root, Path.GetFileName(destDir) + "-" + tag + ".zip");
        await RemoteFileDownloader.DownloadAsync(url, zip, status: null, ct, minCompleteBytes: minBytes).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(destDir);
        var count = 0;
        using (var archive = ZipFile.OpenRead(zip))
        {
            foreach (var entry in archive.Entries)
            {
                if (!include(entry)) continue;
                var name = targetName(entry);
                if (string.IsNullOrWhiteSpace(name)) continue;
                entry.ExtractToFile(Path.Combine(destDir, name), overwrite: true);
                count++;
            }
        }

        try { File.Delete(zip); } catch { /* ignore */ }
        if (count == 0)
            throw new InvalidOperationException("Gói tải về không có tệp cần thiết: " + url);
        File.WriteAllText(Marker(markerDir ?? destDir, tag), DateTime.UtcNow.ToString("O"));
        log($"CUDA: đã cài {count} tệp → {destDir}");
    }
}
