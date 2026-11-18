using DogAI.Core.Characters;
using DogAI.Core.Chat;

/// <summary>
/// The guide's made-up content. Tame on purpose: these words and pictures are on GitHub for anyone to read. Change a
/// line here and the pictures that show it change at the next run of tools/guide-shots.ps1.
/// </summary>
static class Content
{
    // The models the shot list links in (tools/guide-shots.json "models"), as paths inside the models folder.
    public const string ChatModel = @"Qwen3.5-9B\Qwen3.5-9B-Q8_0.gguf";
    public const string ChatRepo = "unsloth/Qwen3.5-9B-GGUF";
    public const string SearchModel = "Qwen3-Embedding-0.6B-Q8_0.gguf";
    public const string Checkpoint = "illustrious_novaCartoonXL_v60.safetensors";

    public static Persona Sam() => new()
    {
        Name = "Sam",
        Description = "Sam is a hiker spending a week in the harbour town of Saltmere, curious about everything and always hungry.",
        IsDefault = true,
    };

    public static Character Biscuit(byte[]? image, byte[]? avatar) => new()
    {
        Name = "Biscuit",
        Description = "Biscuit is a cheerful talking golden retriever who runs the Crumb & Collar, a small bakery on the " +
            "harbour of Saltmere. He wears a flour-dusted blue apron, knows every regular by name, and cannot keep a " +
            "secret about tomorrow's bake.",
        Personality = "Warm, chatty, easily excited, proud of his sourdough, a little scared of seagulls.",
        Scenario = "Early morning at the Crumb & Collar. The first loaves are out of the oven and {{user}} is the first customer.",
        FirstMessage = "*The bell over the door jingles and Biscuit's tail starts wagging before he even looks up.* " +
            "Good morning! You're the first one in. The rye is still warm. Can I tempt you?",
        Tags = ["bakery", "dog", "cozy"],
        Image = image,
        Avatar = avatar,
    };

    public static Character Mara() => new()
    {
        Name = "Mara",
        Description = "Captain Mara Reyes is Saltmere's harbour master: weathered, dry-humoured and fond of Biscuit's " +
            "cinnamon rolls, though she would never say so.",
        Personality = "Calm, practical, teasing, notices the weather before anyone else.",
        Scenario = "The harbour office overlooks the bakery. Mara drops by most mornings.",
        FirstMessage = "*Mara taps the barometer on the wall.* Fog by noon. If you're going out on the water, go now.",
        Tags = ["harbour", "captain"],
    };

    public static readonly (string Title, bool Pinned, bool InFolder, (ChatRole Role, string Text)[] Turns)[] GeneralChats =
    [
        ("Rename photos by date", false, false,
        [
            (ChatRole.User, "How can I rename all the photos in a folder by the date they were taken, with PowerShell?"),
            (ChatRole.Assistant, "This renames every JPEG in the current folder to the date and time it was taken, read from the " +
                "file's own date:\n\n```powershell\nGet-ChildItem *.jpg | ForEach-Object {\n    $name = $_.LastWriteTime.ToString('yyyy-MM-dd HH.mm.ss')\n" +
                "    Rename-Item $_ \"$name.jpg\"\n}\n```\n\nTry it on a copy of the folder first: two photos taken in the same second would clash."),
        ]),
        ("Coastal walk this weekend", false, false,
        [
            (ChatRole.User, "Plan a gentle six-mile coastal walk with a lunch stop, for a sunny Saturday."),
            (ChatRole.Assistant, "Here is a relaxed plan:\n\n1. **9:30** Start at the harbour car park and follow the cliff path north.\n" +
                "2. **11:00** Rest at the lookout above the cove; the tide is lowest around now.\n3. **12:30** Lunch at the village café, then " +
                "take the inland lane back.\n\nBring water, sun cream and a light layer: the wind on the cliffs is cooler than it looks."),
        ]),
        ("Sourdough starter that won't rise", true, true,
        [
            (ChatRole.User, "My sourdough starter is a week old and still barely rises. What am I doing wrong?"),
            (ChatRole.Assistant, "A slow starter is common in the first weeks. Three things usually help:\n\n- **Warmth.** Keep it " +
                "around 24 to 26 °C; a cold kitchen slows everything down.\n- **Regular feeding.** Discard all but a spoonful and feed " +
                "equal weights of flour and water twice a day.\n- **Some whole grain.** A little rye or whole wheat gives the yeast more " +
                "to eat.\n\nGive it another week. Once it doubles within six hours of a feed, it is ready to bake with."),
            (ChatRole.User, "Does the kind of water matter?"),
            (ChatRole.Assistant, "It can. Heavily chlorinated tap water slows a young starter. Leave the water out overnight, or use " +
                "filtered water, and you take that out of the question."),
        ]),
    ];

    public static readonly (ChatRole Role, string Text)[] BiscuitChat =
    [
        (ChatRole.Assistant, "*The bell over the door jingles and Biscuit's tail starts wagging before he even looks up.* " +
            "Good morning! You're the first one in. The rye is still warm. Can I tempt you?"),
        (ChatRole.User, "Good morning, Biscuit! What do you recommend for a long walk along the cliffs?"),
        (ChatRole.Assistant, "*His ears perk up.* Oh, a cliff walk! Then you want something sturdy. *He lifts a round loaf " +
            "onto the counter.* \"Harbour seeded: sunflower, pumpkin, a little honey. It won't crumble in your pack.\" " +
            "*He lowers his voice.* \"And take a cinnamon roll for the top. Don't tell Mara I gave you the big one.\""),
    ];

    public static readonly (ChatRole Role, string Text, string? Speaker)[] GroupChat =
    [
        (ChatRole.Assistant, "*Mara leans on the bakery counter.* \"Fog by noon, Biscuit. Your delivery boat had better leave early.\"", "Mara"),
        (ChatRole.User, "Is the fog really that bad? I wanted to take the ferry to the island.", null),
        (ChatRole.Assistant, "*Biscuit's tail droops.* \"If Mara says fog, it's fog. She's never wrong about the weather. It's very annoying.\"", "Biscuit"),
        (ChatRole.Assistant, "*Mara almost smiles.* \"Take the ten o'clock ferry and come back on the two. You'll beat it by an hour.\"", "Mara"),
    ];
}
