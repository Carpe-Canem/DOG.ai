namespace DogAI.Core.Models;

/// <summary>The context sizes DOG.ai offers (the defaults block and the model picker), in tokens. One list for both.</summary>
public static class ContextSizes
{
    public static IReadOnlyList<int> Offered { get; } = [4096, 8192, 16384, 32768, 65536, 131072];
}
