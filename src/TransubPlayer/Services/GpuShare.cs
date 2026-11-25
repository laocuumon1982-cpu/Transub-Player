namespace TransubPlayer.Services;

/// <summary>
/// Decides whether translation (llama-server) may stay on the GPU while Whisper ASR also uses it.
/// The upstream default demotes MT to CPU whenever ASR is active, which is safe on 4–6 GB cards
/// but leaves big GPUs (e.g. RTX 3060 12 GB) mostly idle and makes MT CPU-bound.
/// </summary>
internal static class GpuShare
{
    public const string Auto = "auto";
    public const string On = "on";
    public const string Off = "off";

    /// <summary>Auto mode shares the GPU when the discrete card has at least this much VRAM.</summary>
    public const long AutoMinVramMb = 7500;

    public static string Normalize(string? raw)
    {
        var v = (raw ?? "").Trim().ToLowerInvariant();
        return v switch
        {
            On or "true" or "1" or "yes" => On,
            Off or "false" or "0" or "no" => Off,
            _ => Auto,
        };
    }

    public static bool MtCanShareWithAsr(AppSettings settings)
    {
        if (AsrBackends.Normalize(settings.AsrBackend) == AsrBackends.Cpu)
            return true; // Whisper on CPU — GPU is free for MT anyway.
        return Normalize(settings.MtGpuShare) switch
        {
            On => true,
            Off => false,
            _ => GpuAdapterProbe.PreferredDiscreteVramMb >= AutoMinVramMb,
        };
    }
}
