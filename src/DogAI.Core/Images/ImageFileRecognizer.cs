using System.Text.Json;
using System.Text.RegularExpressions;

namespace DogAI.Core.Images;

/// <summary>
/// The header of a .safetensors file: every tensor's name and shape, and the free-text metadata the maker wrote. The file
/// starts with 8 bytes giving the header's length, then the header as JSON; the weights follow and are never read here.
/// </summary>
public sealed record SafetensorsHeader(IReadOnlyDictionary<string, long[]> Tensors, IReadOnlyDictionary<string, string> Metadata)
{
    /// <summary>Headers above this are refused. A 6.9 GB SDXL checkpoint's header is 1.4 MB.</summary>
    public const long MaxHeaderBytes = 64L << 20;

    /// <summary>Reads the header from the start of <paramref name="stream"/>. Throws <see cref="InvalidDataException"/> when it is not one.</summary>
    public static SafetensorsHeader Read(Stream stream)
    {
        Span<byte> size = stackalloc byte[8];
        stream.ReadExactly(size);
        var length = BitConverter.ToInt64(size);
        if (length is <= 1 or > MaxHeaderBytes) throw new InvalidDataException("This is not a safetensors file.");
        var json = new byte[length];
        stream.ReadExactly(json);
        return Parse(json);
    }

    public static SafetensorsHeader Parse(ReadOnlySpan<byte> json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { MaxDepth = 16 }); }
        catch (JsonException) { throw new InvalidDataException("This is not a safetensors file."); }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("This is not a safetensors file.");
            var tensors = new Dictionary<string, long[]>(StringComparer.Ordinal);
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                if (entry.Name == "__metadata__")
                {
                    if (entry.Value.ValueKind != JsonValueKind.Object) continue;
                    foreach (var m in entry.Value.EnumerateObject())
                        metadata[m.Name] = m.Value.ValueKind == JsonValueKind.String ? m.Value.GetString() ?? "" : m.Value.GetRawText();
                    continue;
                }
                var shape = entry.Value.ValueKind == JsonValueKind.Object && entry.Value.TryGetProperty("shape", out var s) && s.ValueKind == JsonValueKind.Array
                    ? s.EnumerateArray().Select(d => d.TryGetInt64(out var n) ? n : -1).ToArray()
                    : [];
                tensors[entry.Name] = shape;
            }
            return new SafetensorsHeader(tensors, metadata);
        }
    }
}

/// <summary>What an image model file is, as far as DOG.ai can use it.</summary>
public enum ImageFileKind
{
    /// <summary>An SDXL checkpoint: DOG.ai can draw with it.</summary>
    Checkpoint,

    /// <summary>An SDXL LoRA.</summary>
    Lora,

    /// <summary>A separate VAE.</summary>
    Vae,

    /// <summary>A picture enlarger (ESRGAN kind). Not used by DOG.ai yet.</summary>
    Upscaler,

    /// <summary>A model file DOG.ai cannot use (another family, or not recognised). Shown, never hidden.</summary>
    Unusable,

    /// <summary>An old pickle format (.ckpt, .pt, .pth, .bin) that can carry program code. Never opened.</summary>
    Refused,
}

/// <summary>What a LoRA's own metadata says about it.</summary>
/// <param name="Triggers">Words found in every one of its training pictures, so probably what calls it up.</param>
/// <param name="CommonTags">The most frequent other training tags.</param>
/// <param name="MadeFor">The family it was trained on, when its metadata or name says.</param>
/// <param name="LyCoris">A LyCORIS kind (LoHa, LoKr) rather than a plain LoRA.</param>
public sealed record LoraFacts(IReadOnlyList<string> Triggers, IReadOnlyList<string> CommonTags, ImageFamily? MadeFor, bool LyCoris);

/// <summary>What DOG.ai made of one file. <see cref="Note"/> says it in plain words.</summary>
public sealed record ImageFileVerdict(ImageFileKind Kind, string Note, ImageFamily? Family = null, bool VPrediction = false, LoraFacts? Lora = null);

/// <summary>
/// Tells SDXL checkpoints, SDXL LoRAs, VAEs and upscalers apart by the tensor names in a file's header, and guesses the
/// checkpoint family from names (every SDXL checkpoint has the same tensors). Pure: the file reading is in DogAI.Engine.
/// Metadata is untrusted text from whoever made the file: only short, specific fields are used, never shown wholesale.
/// </summary>
public static class ImageFileRecognizer
{
    /// <summary>Old formats that are Python pickles: loading one can run code hidden in it.</summary>
    public static readonly IReadOnlySet<string> RefusedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".ckpt", ".pt", ".pth", ".bin" };

    public const string RefusedNote =
        "Old .ckpt-style file: refused, because this format can carry program code that runs when it is opened. Look for a .safetensors version of it.";

    /// <summary>True for files the image models folder lists at all: .safetensors, and the refused old formats (shown as refused).</summary>
    public static bool IsModelFile(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        return ext.Equals(".safetensors", StringComparison.OrdinalIgnoreCase) || RefusedExtensions.Contains(ext);
    }

    public static ImageFileVerdict Classify(SafetensorsHeader header, string fileName)
    {
        var keys = header.Tensors.Keys;
        var meta = header.Metadata;
        var names = NameSources(fileName, meta);

        if (keys.Any(k => k.StartsWith("model.diffusion_model.", StringComparison.Ordinal)))
        {
            if (keys.Any(k => k.Contains("double_blocks", StringComparison.Ordinal))) return Unusable("A Flux checkpoint. DOG.ai draws with SDXL checkpoints only for now.");
            if (keys.Any(k => k.Contains("joint_blocks", StringComparison.Ordinal))) return Unusable("A Stable Diffusion 3 checkpoint. DOG.ai draws with SDXL checkpoints only for now.");
            if (keys.Any(k => k.StartsWith("conditioner.embedders.1.", StringComparison.Ordinal)))
            {
                if (header.Tensors.TryGetValue("model.diffusion_model.input_blocks.0.0.weight", out var first) && first.Length > 1 && first[1] != 4)
                    return Unusable("An SDXL inpainting or editing checkpoint. DOG.ai draws pictures from a prompt only for now.");
                var family = GuessFamily(names);
                var vpred = IsVPrediction(header, names);
                return new ImageFileVerdict(ImageFileKind.Checkpoint,
                    $"SDXL checkpoint{(family is { } f ? $", {FamilyNames.Title(f)}" : "")}{(vpred ? ", v-prediction" : "")}",
                    family ?? ImageFamily.Sdxl, vpred);
            }
            if (keys.Any(k => k.StartsWith("cond_stage_model.", StringComparison.Ordinal)))
                return Unusable("A Stable Diffusion 1.5 or 2 checkpoint. DOG.ai draws with SDXL checkpoints only.");
            return Unusable("A checkpoint of a kind DOG.ai does not know. It draws with SDXL checkpoints only.");
        }

        if (keys.Any(IsLoraKey))
        {
            if (keys.Any(k => k.Contains("double_blocks", StringComparison.Ordinal) || k.Contains("single_blocks", StringComparison.Ordinal) ||
                              k.Contains("single_transformer_blocks", StringComparison.Ordinal)))
                return Unusable("A Flux LoRA. DOG.ai uses SDXL LoRAs only for now.");
            var arch = meta.GetValueOrDefault("modelspec.architecture") ?? "";
            var baseVersion = meta.GetValueOrDefault("ss_base_model_version") ?? "";
            var sdxl = keys.Any(k => k.StartsWith("lora_te2_", StringComparison.Ordinal)) ||
                       arch.Contains("stable-diffusion-xl", StringComparison.OrdinalIgnoreCase) ||
                       baseVersion.StartsWith("sdxl", StringComparison.OrdinalIgnoreCase) ||
                       keys.Any(k => k.Contains("transformer_blocks_1", StringComparison.Ordinal) || k.Contains("transformer_blocks.1.", StringComparison.Ordinal));
            if (!sdxl) return Unusable("A LoRA for Stable Diffusion 1.5 or another family. DOG.ai uses SDXL LoRAs only.");
            var lycoris = keys.Any(k => k.Contains(".hada_", StringComparison.Ordinal) || k.Contains(".lokr_", StringComparison.Ordinal));
            var madeFor = GuessFamily(names);
            var facts = new LoraFacts(Triggers(meta), CommonTags(meta), madeFor, lycoris);
            return new ImageFileVerdict(ImageFileKind.Lora,
                $"SDXL {(lycoris ? "LyCORIS" : "LoRA")}{(madeFor is { } m ? $", made for {FamilyNames.Title(m)}" : "")}",
                madeFor, Lora: facts);
        }

        var vaeKey = keys.Contains("decoder.conv_in.weight") ? "decoder.conv_in.weight"
            : keys.Contains("first_stage_model.decoder.conv_in.weight") ? "first_stage_model.decoder.conv_in.weight" : null;
        if (vaeKey is not null && keys.All(k => k.StartsWith("encoder.", StringComparison.Ordinal) || k.StartsWith("decoder.", StringComparison.Ordinal) ||
                                               k.StartsWith("quant_conv.", StringComparison.Ordinal) || k.StartsWith("post_quant_conv.", StringComparison.Ordinal) ||
                                               k.StartsWith("first_stage_model.", StringComparison.Ordinal)))
        {
            var channels = header.Tensors[vaeKey] is { Length: > 1 } shape ? shape[1] : 4;
            return channels == 4
                ? new ImageFileVerdict(ImageFileKind.Vae, "VAE (the part that turns the drawing into a picture)")
                : Unusable("A VAE for Flux or Stable Diffusion 3. DOG.ai uses SDXL VAEs only.");
        }

        if (keys.Contains("conv_first.weight") || (keys.Contains("model.0.weight") && keys.Any(k => k.StartsWith("model.1.sub.", StringComparison.Ordinal))))
            return new ImageFileVerdict(ImageFileKind.Upscaler, "Upscaler (ESRGAN kind). DOG.ai does not use upscaler files yet.");

        if (keys.Any(k => k.StartsWith("double_blocks.", StringComparison.Ordinal))) return Unusable("A Flux model. DOG.ai draws with SDXL checkpoints only for now.");
        if (keys.Any(k => k.StartsWith("down_blocks.", StringComparison.Ordinal) || k.StartsWith("text_model.", StringComparison.Ordinal)))
            return Unusable("One part of a model in the diffusers layout. DOG.ai needs single-file SDXL checkpoints.");
        return Unusable("DOG.ai cannot tell what this file is.");
    }

    private static ImageFileVerdict Unusable(string note) => new(ImageFileKind.Unusable, note);

    private static bool IsLoraKey(string key) =>
        key.StartsWith("lora_", StringComparison.Ordinal) || key.Contains(".lora_down.", StringComparison.Ordinal) ||
        key.Contains(".lora_A.", StringComparison.Ordinal) || key.Contains(".hada_", StringComparison.Ordinal) || key.Contains(".lokr_", StringComparison.Ordinal);

    /// <summary>The file name and the few metadata fields that name the model, the base it was made from, or its architecture.</summary>
    private static List<string> NameSources(string fileName, IReadOnlyDictionary<string, string> meta)
    {
        var names = new List<string> { Path.GetFileNameWithoutExtension(fileName) };
        foreach (var key in new[] { "modelspec.title", "ss_output_name", "ss_sd_model_name" })
            if (meta.TryGetValue(key, out var value) && value.Length < 300) names.Add(value);
        if (meta.TryGetValue("unified_model_info", out var info) && info.Length < 100_000)
        {
            try
            {
                using var doc = JsonDocument.Parse(info);
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("name", out var name) &&
                    name.ValueKind == JsonValueKind.String && name.GetString() is { Length: < 300 } n)
                    names.Add(n);
            }
            catch (JsonException) { }
        }
        return names;
    }

    /// <summary>
    /// Splits a name into lower-case words at separators, case changes and digit boundaries:
    /// "illustrious_novaFurryXL_ilV180A" → illustrious, nova, furry, xl, il, v, 180, a.
    /// </summary>
    public static IReadOnlyList<string> Words(string name) =>
        Regex.Matches(name, "[A-Z]+(?![a-z])|[A-Z]?[a-z]+|[0-9]+", RegexOptions.None, TimeSpan.FromSeconds(1))
            .Select(m => m.Value.ToLowerInvariant()).ToList();

    /// <summary>The family a name points to, or null. NoobAI comes first: it is built on Illustrious and its files often say both.</summary>
    public static ImageFamily? GuessFamily(IEnumerable<string> names)
    {
        var words = names.SelectMany(Words).ToHashSet();
        bool Any(params string[] options) => options.Any(words.Contains);
        if (words.Any(w => w.StartsWith("noob", StringComparison.Ordinal))) return ImageFamily.NoobAI;
        if (Any("pony", "pdxl")) return ImageFamily.Pony;
        if (Any("illustrious", "illust", "ilxl", "il")) return ImageFamily.Illustrious;
        return null;
    }

    private static bool IsVPrediction(SafetensorsHeader header, IEnumerable<string> names)
    {
        if (header.Tensors.ContainsKey("v_pred")) return true;
        var declared = header.Metadata.GetValueOrDefault("modelspec.prediction_type") ?? "";
        if (declared.Equals("v", StringComparison.OrdinalIgnoreCase) || declared.StartsWith("v_", StringComparison.OrdinalIgnoreCase) ||
            declared.StartsWith("v-", StringComparison.OrdinalIgnoreCase)) return true;
        return names.Any(n => Regex.IsMatch(n, "v[-_ ]?pred", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)));
    }

    /// <summary>Tags in nearly every training picture that say nothing about the subject (counts, plain kinds of figure).</summary>
    private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    {
        "1girl", "1boy", "2girls", "2boys", "3girls", "3boys", "multiple girls", "multiple boys", "solo", "solo focus",
        "furry", "furry female", "furry male", "anthro", "female", "male", "girl", "boy", "looking at viewer",
        "simple background", "white background", "highres", "absurdres", "masterpiece", "best quality", "realistic",
    };

    /// <summary>How often a tag must appear to count as a trigger: in nearly every training picture.</summary>
    private const double TriggerShare = 0.95;

    /// <summary>Training tags with how many pictures carried each, and how many pictures there were, per training folder.</summary>
    private static List<(Dictionary<string, int> Tags, int Pictures)> TagFolders(IReadOnlyDictionary<string, string> meta)
    {
        var result = new List<(Dictionary<string, int>, int)>();
        if (!meta.TryGetValue("ss_tag_frequency", out var json) || json.Length > 20_000_000) return result;
        var pictures = new Dictionary<string, int>(StringComparer.Ordinal);
        if (meta.TryGetValue("ss_dataset_dirs", out var dirs) && dirs.Length < 1_000_000)
        {
            try
            {
                using var d = JsonDocument.Parse(dirs);
                if (d.RootElement.ValueKind == JsonValueKind.Object)
                    foreach (var dir in d.RootElement.EnumerateObject())
                        if (dir.Value.ValueKind == JsonValueKind.Object && dir.Value.TryGetProperty("img_count", out var c) && c.TryGetInt32(out var n) && n > 0)
                            pictures[dir.Name] = n;
            }
            catch (JsonException) { }
        }
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;
            foreach (var folder in doc.RootElement.EnumerateObject())
            {
                if (folder.Value.ValueKind != JsonValueKind.Object) continue;
                var tags = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var tag in folder.Value.EnumerateObject())
                {
                    if (!tag.Value.TryGetInt32(out var count) || CleanTag(tag.Name) is not { } clean) continue;
                    tags[clean] = Math.Max(tags.GetValueOrDefault(clean), count);
                }
                if (tags.Count == 0) continue;
                var total = pictures.TryGetValue(folder.Name, out var p) ? p : tags.Values.Max();
                result.Add((tags, total));
            }
        }
        catch (JsonException) { }
        return result;
    }

    /// <summary>"(yellow highlights:1.2)" → "yellow highlights". Null for empty or overlong tags.</summary>
    private static string? CleanTag(string raw)
    {
        var tag = raw.Trim().Trim('(', ')', '[', ']', '{', '}').Trim();
        var colon = tag.LastIndexOf(':');
        if (colon > 0 && double.TryParse(tag[(colon + 1)..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
            tag = tag[..colon].Trim();
        return tag.Length is 0 or > 80 ? null : tag;
    }

    /// <summary>The trigger words: those the maker declared, else training tags found in nearly every picture (minus counts and plain kinds of figure).</summary>
    public static IReadOnlyList<string> Triggers(IReadOnlyDictionary<string, string> meta)
    {
        var found = new List<string>();
        if (meta.TryGetValue("modelspec.trigger_phrase", out var phrase) && phrase.Length < 1000)
            found.AddRange(phrase.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        foreach (var (tags, pictures) in TagFolders(meta))
            found.AddRange(tags.Where(t => t.Value >= pictures * TriggerShare && !Generic.Contains(t.Key))
                .OrderByDescending(t => t.Value).Select(t => t.Key));
        return found.Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
    }

    /// <summary>The most frequent training tags that are not triggers, most frequent first.</summary>
    public static IReadOnlyList<string> CommonTags(IReadOnlyDictionary<string, string> meta, int count = 10)
    {
        var triggers = Triggers(meta).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return TagFolders(meta)
            .SelectMany(f => f.Tags)
            .GroupBy(t => t.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Tag: g.Key, Count: g.Sum(t => t.Value)))
            .Where(t => !triggers.Contains(t.Tag))
            .OrderByDescending(t => t.Count).ThenBy(t => t.Tag, StringComparer.Ordinal)
            .Take(count).Select(t => t.Tag).ToList();
    }
}
