using DogAI.Core.Images;

namespace DogAI.Engine.Images;

/// <summary>
/// Measures the main graphics card for the card coordinator: total and free memory from nvidia-smi, and what the chat
/// engine and the picture engine each hold from Windows' per-program counters.
/// </summary>
public sealed class CardMeter(HardwareProbe probe, EngineManager chat, ImageServer pictures) : ICardMeter
{
    public async Task<CardMemory?> ReadAsync(CancellationToken ct = default)
    {
        var gpu = await probe.MainGpuAsync(ct).ConfigureAwait(false);
        if (gpu is null || gpu.TotalBytes <= 0) return null;
        var chatBytes = chat.ProcessId is int c ? await probe.VramOfProcessAsync(c, ct).ConfigureAwait(false) : 0;
        var pictureBytes = pictures.ProcessId is int p ? await probe.VramOfProcessAsync(p, ct).ConfigureAwait(false) : 0;
        return new CardMemory(gpu.TotalBytes, gpu.FreeBytes, pictureBytes, chatBytes);
    }
}
