using DogAI.Core;

namespace DogAI.Engine.Images;

/// <summary>
/// Finds the picture engine (stable-diffusion.cpp's sd-server). Only two places count: an explicit override for tests,
/// then DOG.ai's own copy in the engine folder, installed from Models → Images. Nothing on PATH or from winget is used,
/// so the program that sees picture prompts is always one DOG.ai installed and checked.
/// </summary>
public sealed class SdServerLocator(AppPaths paths)
{
    public const string OverrideVariable = "DOGAI_SD_SERVER";
    public const string FileName = "sd-server.exe";

    public string? Find() => Candidates().FirstOrDefault(File.Exists);

    public const string MissingMessage = "The picture engine (stable-diffusion.cpp) is not installed yet. Get it in Models → Images.";

    private IEnumerable<string> Candidates()
    {
        if (Environment.GetEnvironmentVariable(OverrideVariable) is { Length: > 0 } explicitPath)
            yield return explicitPath;
        yield return Path.Combine(paths.ImageEngineDirectory, FileName);
    }
}
