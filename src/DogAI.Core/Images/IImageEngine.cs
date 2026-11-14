namespace DogAI.Core.Images;

/// <summary>
/// The picture engine as the rest of DOG.ai sees it. The real one is <c>ImageServer</c> (DogAI.Engine), which runs
/// stable-diffusion.cpp's sd-server on 127.0.0.1; tests use a fake. Nothing outside DogAI.Engine touches the process.
/// The app hands out <see cref="CardCoordinator"/> under this interface, which shares the graphics card with the
/// chat model; ask for <see cref="IImageEngine"/>, never for the engine itself, to draw.
/// </summary>
public interface IImageEngine
{
    /// <summary>The checkpoint pictures are drawn with, or null when none is chosen (or it was unloaded).</summary>
    ImageModel? Model { get; }

    /// <summary>True while a picture is being drawn. The idle lock counts it as busy.</summary>
    bool IsBusy { get; }

    /// <summary>True while the engine program is running, so it may be holding graphics memory.</summary>
    bool IsRunning { get; }

    /// <summary>Why the engine last failed to start, in plain words. Cleared when it starts.</summary>
    string? Error { get; }

    /// <summary>Raised when the engine starts, stops, loads, unloads, fails, or starts or ends a picture. May come from a background thread.</summary>
    event Action? Changed;

    /// <summary>
    /// Makes <paramref name="model"/> the checkpoint to draw with. The engine starts with it at once; the file itself is
    /// read into graphics memory with the first picture (a few seconds). Choosing the model already in use does nothing.
    /// </summary>
    Task LoadAsync(ImageModel model, CancellationToken ct = default);

    /// <summary>Gives the graphics memory back. A picture being drawn is cancelled first. The next picture needs <see cref="LoadAsync"/> again.</summary>
    Task UnloadAsync(CancellationToken ct = default);

    /// <summary>
    /// Gives the graphics memory back but keeps the checkpoint chosen: the next picture loads it again by itself
    /// (a few seconds). Used when the chat model needs the room.
    /// </summary>
    Task ReleaseAsync(CancellationToken ct = default);

    /// <summary>
    /// Draws the picture(s) <paramref name="request"/> describes with the loaded checkpoint. Cancelling
    /// <paramref name="ct"/> stops the engine mid-picture and throws <see cref="OperationCanceledException"/>.
    /// </summary>
    /// <exception cref="ImageEngineException">No checkpoint is loaded, the engine is missing or failed, or it refused the request.</exception>
    Task<ImageResult> DrawAsync(ImageRequest request, IProgress<ImageProgress>? progress = null, CancellationToken ct = default);
}

/// <summary>A picture engine problem, said in plain words.</summary>
public sealed class ImageEngineException(string message) : Exception(message);
