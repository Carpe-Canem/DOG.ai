namespace DogAI.Core.Images;

/// <summary>
/// A recommended image model on Hugging Face: one file from <paramref name="Repo"/>, saved as <paramref name="SaveAs"/> in
/// <paramref name="Folder"/> (a subfolder of the image models folder). The saved name says the family where the original
/// does not, so the file is recognised.
/// </summary>
public sealed record ImageDownload(string Folder, string Repo, string File, string SaveAs, string Title, string Blurb, long Bytes, string Licence,
    bool Recommended = false);

/// <summary>
/// The image models DOG.ai recommends. Checked on Hugging Face on 2026-11-14 (file names, sizes, licences; none is gated).
/// Pony Diffusion V6 comes from its author's own repository. Civitai is not reachable from DOG.ai: files from there are
/// added by hand with Open folder.
/// </summary>
public static class ImageDownloads
{
    /// <summary>The use case image downloads carry, so they never become chat-model records.</summary>
    public const string UseCase = "image";

    public static IReadOnlyList<ImageDownload> All { get; } =
    [
        new(ImageFolders.Checkpoints, "OnomaAIResearch/Illustrious-XL-v2.0", "Illustrious-XL-v2.0.safetensors", "Illustrious-XL-v2.0.safetensors",
            "Illustrious XL 2.0", "The base of most anime-style checkpoints. Prompts in Danbooru tags.", 6_938_040_674, "CreativeML OpenRAIL-M", Recommended: true),
        new(ImageFolders.Checkpoints, "Laxhar/noobai-XL-1.1", "NoobAI-XL-v1.1.safetensors", "NoobAI-XL-v1.1.safetensors",
            "NoobAI XL 1.1", "Illustrious trained further on many more pictures. Prompts in Danbooru tags.", 7_105_349_958, "Fair AI Public License 1.0-SD"),
        new(ImageFolders.Checkpoints, "Laxhar/noobai-XL-Vpred-1.0", "NoobAI-XL-Vpred-v1.0.safetensors", "NoobAI-XL-Vpred-v1.0.safetensors",
            "NoobAI XL V-Pred 1.0", "A v-prediction NoobAI: richer colours and contrast. Not tried on this PC yet.", 7_105_350_110, "Fair AI Public License 1.0-SD"),
        new(ImageFolders.Checkpoints, "AstraliteHeart/pony-diffusion-v6", "v6.safetensors", "PonyDiffusion-V6-XL.safetensors",
            "Pony Diffusion V6 XL", "Cartoon, furry and anime styles. Prompts in tags after its score words.", 6_938_041_050, "Fair AI Public License 1.0-SD (modified)"),
        new(ImageFolders.Checkpoints, "stabilityai/stable-diffusion-xl-base-1.0", "sd_xl_base_1.0.safetensors", "sd_xl_base_1.0.safetensors",
            "Stable Diffusion XL 1.0", "The original SDXL: photos and general pictures. Prompts in sentences.", 6_938_078_334, "CreativeML Open RAIL++-M"),
        new(ImageFolders.Vae, "madebyollin/sdxl-vae-fp16-fix", "sdxl.vae.safetensors", "sdxl-vae-fp16-fix.safetensors",
            "SDXL VAE (fp16 fix)", "A replacement VAE for checkpoints that give black or washed-out pictures. Most do not need it.", 334_641_162, "MIT"),
        new(ImageFolders.Loras, "stabilityai/stable-diffusion-xl-base-1.0", "sd_xl_offset_example-lora_1.0.safetensors", "sd_xl_offset_example-lora_1.0.safetensors",
            "SDXL offset LoRA", "Stability AI's small example LoRA: deeper darks and brighter lights.", 49_553_604, "CreativeML Open RAIL++-M"),
    ];
}
