using DogAI.Core.Characters;
using DogAI.Core.Chat;
using DogAI.Core.Stories;

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

    // ---- Added for the chat pages (M23 step 2). Rules for new text: never the word "fog" (the Search picture looks for
    // it) and no word starting "bis" (the Ctrl+K picture types it), so the pictures of Finding your way keep their hits.

    /// <summary>A second version of the coastal walk's reply, shown as "2 of 2".</summary>
    public const string CoastalWalkAgain = "A gentle route with an early lunch:\n\n1. **10:00** Walk south along the sea wall " +
        "to the old lime kilns.\n2. **11:30** Picnic on the grass above the beach; there is shade under the pines.\n" +
        "3. **13:00** Follow the river path inland and loop back to town.\n\nThe whole loop is flat apart from one short climb.";

    /// <summary>Biscuit's lore: facts the model is given only when a chat mentions them.</summary>
    /// <summary>Biscuit's Looks: the words of his portrait's prompt (assets\pictures\biscuit-portrait.json).</summary>
    public const string BiscuitLooks = "anthro, golden retriever, male, blue apron";

    public static readonly (string Name, string[] Keys, string Text)[] BiscuitLore =
    [
        ("The Crumb & Collar", ["bakery", "shop", "crumb"],
            "The Crumb & Collar is a narrow blue-fronted bakery on Saltmere's harbour. It opens at half past six and is closed on Mondays."),
        ("Seagulls", ["seagull", "gull", "gulls"],
            "A seagull once stole a whole loaf from Biscuit's window. He has never forgiven them and keeps a broom by the door."),
        ("Harbour seeded loaf", ["seeded", "loaf", "sourdough"],
            "The harbour seeded loaf is Biscuit's own recipe: sunflower and pumpkin seeds with a spoonful of honey. It sells out by ten."),
    ];

    /// <summary>A one-to-one chat with Mara, with a picture on her last reply and a summary of the start.</summary>
    public static readonly (ChatRole Role, string Text)[] MaraChat =
    [
        (ChatRole.User, "Morning, Captain. When does the harbour look its best from your office?"),
        (ChatRole.Assistant, "*Mara glances at the window without getting up.* \"Sunrise. The boats go out at half five and the light " +
            "comes in low over the water. After seven it's just a car park with seagulls.\""),
        (ChatRole.User, "Could I come up tomorrow and watch them go out?"),
        (ChatRole.Assistant, "*She slides a spare key across the desk.* \"Six o'clock, not a minute later. Bring two coffees, and don't " +
            "touch the barometer.\" *She turns back to her charts, but you catch the start of a smile.*"),
    ];

    public const string MaraSummary = "Sam asked Captain Mara when the harbour looks its best. She said sunrise, when the boats go out at half five.";

    /// <summary>A General question with a picture (the harbour picture, as a chat picture) and a reply that thought first.</summary>
    public const string PhotoTitle = "Where is this harbour?";
    public const string PhotoQuestion = "Where could this be, and what time of day is it?";
    public const string PhotoThinking = "The sun is right on the horizon and the sky is orange and purple, so it is sunrise or sunset. " +
        "The water is calm and the sailing boat is still moored, which fits early morning better. There is a lighthouse at the end of a " +
        "stone pier and houses on the hill behind.";
    public const string PhotoReply = "It looks like a small harbour at **sunrise**:\n\n- The sun sits right on the horizon, so it is dawn " +
        "or dusk; the calm water and the boat still at its mooring suggest early morning.\n- A lighthouse stands at the end of a stone pier, " +
        "with a sailing boat tied up beside it.\n- Houses climb the hill behind the harbour, as in a small coastal town.\n\n" +
        "It is a drawing rather than a photograph, so it may not be a real place.";

    // ---- Documents: a collection with one made-up file, and a chat that cites it.

    public const string CollectionName = "Saltmere visitor notes";
    public const string CollectionNotes = "Ferry times, opening hours and walks, for the week in Saltmere.";
    public const string DocumentName = "saltmere-guide.md";

    public const string DocumentText = """
        ## Ferries to Gull Island

        Ferries leave Saltmere pier for Gull Island at 08:00, 10:00, 12:00 and 14:00 every day. The trip takes about an hour,
        and the boat stops at the island's only jetty on the south side. Return ferries leave Gull Island at 11:00, 13:00, 15:00
        and 17:30. On Saturdays from June to August there is one more return ferry at 19:00, which is often full: board early.
        In rough weather the harbour master may cancel the ferries; a notice goes up on the pier gate by seven in the morning.

        ## Tickets

        Tickets are sold at the harbour office beside the pier, which opens at half past seven. A day return costs 8 pounds for
        adults and 4 pounds for children; dogs travel free. Bicycles are welcome on every ferry except the last one of the day.
        Keep your ticket: it is checked again on the island before you board the ferry back.

        ## The Crumb & Collar

        The bakery on the harbour front opens at half past six, every day except Monday. Its harbour seeded loaf sells out by ten,
        and its cinnamon rolls by noon. There are two small tables outside, sheltered from the wind by the harbour wall.

        ## Walks

        The cliff path north of the town is four miles each way and well marked, with a bench at the lookout above the cove.
        On Gull Island a short path climbs from the jetty to the north cliffs, where seals rest on the rocks at low tide.
        The path is steep in places; good shoes are better than sandals.

        ## Tides

        Tide times are posted on the board outside the harbour office. The cove below Gull Island's north cliffs can only be
        reached for about two hours either side of low tide. Never cross the flat stones to the caves on a rising tide.
        """;

    public const string FerryTitle = "Ferry times to the island";
    public const string FerryQuestion = "When is the last ferry back from Gull Island on a Saturday in July?";
    public const string FerryReply = "On a Saturday in July the last ferry back leaves Gull Island at **19:00** [1]. It is often full, so " +
        "board early; the one before it leaves at 17:30 [1].\n\nKeep your ticket handy: it is checked again on the island before you " +
        "board the ferry back [2].";

    // ---- Stories: a short adventure with choices, a branch and a picture; a written story along an outline.

    public const string AdventureTitle = "The Island Ferry";
    public static readonly StoryBible AdventureBible = new()
    {
        Premise = "A day trip from Saltmere to Gull Island, where a hand-drawn map left on the ferry leads to a hidden cove.",
        Genre = "Cosy adventure",
        Tone = "Light, warm and curious",
        PointOfView = "Second person",
        Tense = "Present",
        World = "Saltmere is a small harbour town with a bakery, a harbour office and a pier. Gull Island lies an hour away by ferry, " +
            "with heather on top, seals on the rocks and a cove that can only be reached at low tide.",
    };

    public const string AdventureOpening = "The ten o'clock ferry rocks gently against the pier as the last passengers step aboard. " +
        "You find a seat on the open deck, where the air smells of salt and diesel.\n\nOn the bench beside you lies a folded sheet " +
        "of paper, held down by a smooth grey pebble. Unfolded, it is a hand-drawn map of Gull Island: a dotted path from the jetty, " +
        "a cross near the north cliffs, and two words underlined twice: *low tide only*.";

    public static readonly string[] AdventureFirstChoices = ["Ask the ferry captain about the map", "Keep the map and say nothing", "Hand it in at the ticket window"];

    /// <summary>The path not taken at the first choice, kept on the branch map.</summary>
    public const string AdventureOtherPath = "Keep the map and say nothing";
    public const string AdventureOtherPassage = "You fold the map along its creases and slip it into your jacket. Nobody seems to " +
        "be looking for it. The ferry's engine deepens, the pier slides away, and Saltmere shrinks to a row of coloured houses behind you.";

    public const string AdventureAsk = "Ask the ferry captain about the map";
    public const string AdventureSecond = "Captain Ollie squints at the paper and laughs. \"That's old Wren's map. She drew it for " +
        "her grandchildren years ago.\" He taps the cross with a thick finger. \"The cove is real, mind. But you'll only get down to " +
        "it at low tide, and that's one o'clock today. Miss it and you'll be waiting till evening.\"\n\nHe hands the map back with a wink.";

    public const string AdventureWalk = "Walk to the north cliffs as soon as the ferry lands";
    public const string AdventureThird = "Gull Island's path climbs steeply from the jetty through heather and sea pinks. Larks rise " +
        "out of the grass ahead of you. At the top the land simply stops, and below you lies the cove from the map: a curve of pale " +
        "sand, and a line of flat stones leading out to a dark cave in the rocks.\n\nThe tide is still pulling back. Water runs off the " +
        "stones in thin silver lines, and a seal lifts its head to look at you.";

    public static readonly (string Text, bool Risky)[] AdventureLastChoices =
    [
        ("Climb down and cross the stones to the cave", true),
        ("Wait on the cliff until the tide is fully out", false),
        ("Sit on the bench, eat lunch and watch the seal", false),
    ];

    public static StoryState AdventureState(int step) => step switch
    {
        0 => new() { Location = "Saltmere pier, on the ferry", Inventory = ["Day return ticket", "Wren's map"] },
        1 => new() { Location = "The ferry to Gull Island", Inventory = ["Day return ticket", "Wren's map"], Facts = ["Low tide is at one o'clock"] },
        _ => new()
        {
            Location = "North cliffs, Gull Island",
            Inventory = ["Day return ticket", "Wren's map", "Packed lunch"],
            Facts = ["Low tide is at one o'clock", "The map was drawn by Wren for her grandchildren"],
        },
    };

    public const string WrittenTitle = "The Lighthouse Cat";
    public static readonly StoryBible WrittenBible = new()
    {
        Premise = "When the lighthouse keeper falls ill one winter night, Pepper, his small black cat, keeps the lamp burning until morning.",
        Genre = "Gentle tale",
        Tone = "Warm, quiet, a little brave",
        PointOfView = "Third person",
        Tense = "Past",
        World = "A lighthouse on a rocky point outside Saltmere, with a winding iron stair of ninety-nine steps and a lamp that must be wound every two hours.",
    };

    public static readonly (string Title, string Plan)[] WrittenOutline =
    [
        ("The keeper's cold", "Old Tom the keeper catches a cold and cannot climb the stairs. Pepper watches him worry about the lamp."),
        ("Ninety-nine steps", "Pepper climbs the stair alone in the storm and finds a way to wind the lamp."),
        ("Morning light", "The storm passes, the fishing boats come home safely, and Tom finds Pepper asleep beside the lamp."),
    ];

    public const string WrittenFirst = "The wind found every gap in the lighthouse that night. Old Tom sat by the stove with a blanket " +
        "around his shoulders, coughing into a handkerchief, and every so often he looked up at the ceiling as if he could see through " +
        "it to the lamp.\n\nPepper sat on the windowsill and watched him. She knew that look. It was the look he gave the sea when the " +
        "boats were late.";

    public const string WrittenDirection = "Tom tries to stand and cannot; Pepper decides";
    public const string WrittenSecond = "Twice Tom pushed himself up from the chair, and twice his legs folded under him. \"Every two " +
        "hours,\" he muttered, \"or the light goes out.\" Then his eyes closed, and his breathing slowed.\n\nPepper jumped down from the " +
        "windowsill. She looked at Tom, and at the door to the stair, and at the clock on the wall.";

    public const string WrittenThird = "The first thirty steps were easy. By the sixtieth the stair shook with every gust, and rain " +
        "hammered on the narrow windows like thrown gravel. Pepper kept her eyes on the next step and nothing else.\n\nAt the top the " +
        "lamp room glowed gold. The great brass handle of the winding gear stood level with her nose.";
}
