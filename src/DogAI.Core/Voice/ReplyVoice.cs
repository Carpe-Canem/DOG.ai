using DogAI.Core.Characters;
using DogAI.Core.Chat;
using DogAI.Core.Settings;
using DogAI.Core.Stories;

namespace DogAI.Core.Voice;

/// <summary>
/// Connects chats to <see cref="ReadAloud"/>: chooses the voice for a reply (the character's, a group member's,
/// the story's narrator, or the default) and, when "read replies aloud" is on, reads each new reply as it is written.
/// Only replies are read: never drafts ("Write mine", rewrites), story choices, or "thinking".
/// </summary>
public sealed class ReplyVoice : IDisposable
{
    private readonly ChatService _chat;
    private readonly ReadAloud _reader;
    private readonly IChatStore _chats;
    private readonly ICharacterStore _characters;
    private readonly IStoryStore _stories;
    private readonly ISettingsStore _settings;
    private ActiveReply? _following;
    private ActiveReply? _lastFinished;
    private string? _followingKey;

    public ReplyVoice(ChatService chat, ReadAloud reader, IChatStore chats, ICharacterStore characters, IStoryStore stories, ISettingsStore settings)
    {
        _chat = chat;
        _reader = reader;
        _chats = chats;
        _characters = characters;
        _stories = stories;
        _settings = settings;
        _chat.Changed += OnChatChanged;
    }

    /// <summary>Read new replies aloud as they are written. Loaded from settings by <see cref="LoadAsync"/>.</summary>
    public bool AutoRead { get; private set; }

    /// <summary>Raised when <see cref="AutoRead"/> changes.</summary>
    public event Action? Changed;

    public async Task LoadAsync(CancellationToken ct = default) =>
        AutoRead = (await _settings.LoadAsync(ct).ConfigureAwait(false)).ReadRepliesAloud;

    public async Task SetAutoReadAsync(bool on)
    {
        AutoRead = on;
        var settings = await _settings.LoadAsync().ConfigureAwait(false);
        await _settings.SaveAsync(settings with { ReadRepliesAloud = on }).ConfigureAwait(false);
        if (!on && _followingKey is not null && _reader.Has(_followingKey)) _reader.Stop();
        Changed?.Invoke();
    }

    /// <summary>The key <see cref="ReadAloud"/> uses for a saved message.</summary>
    public static string KeyFor(long messageId) => "msg:" + messageId;

    /// <summary>True while this saved message is being read because its Read aloud button was pressed.</summary>
    public bool IsReading(ChatMessage message) => _reader.Has(KeyFor(message.Id));

    /// <summary>Reads one saved reply aloud, or stops if it is the one being read.</summary>
    public async Task ToggleAsync(ChatMessage message, CancellationToken ct = default)
    {
        if (IsReading(message))
        {
            _reader.Stop();
            return;
        }
        var voice = await VoiceForAsync(message.ConversationId, message.SpeakerId, ct).ConfigureAwait(false);
        var cast = await CastForAsync(message.ConversationId, ct).ConfigureAwait(false);
        _reader.Speak(KeyFor(message.Id), message.Content, voice, cast);
    }

    /// <summary>The voice for a reply in this conversation. Null means the default voice.</summary>
    /// <param name="speakerId">Group chats: the character who wrote the reply.</param>
    public async Task<VoiceId?> VoiceForAsync(long conversationId, long? speakerId, CancellationToken ct = default)
    {
        var conversation = await _chats.GetAsync(conversationId, ct).ConfigureAwait(false);
        if (conversation is null) return null;
        if (conversation.Mode == Story.Mode)
            return VoiceId.Parse((await _stories.GetAsync(conversationId, ct).ConfigureAwait(false))?.NarratorVoice);
        var characterId = speakerId ?? conversation.CharacterId;
        if (characterId is null) return null;
        return VoiceId.Parse((await _characters.GetAsync(characterId.Value, ct).ConfigureAwait(false))?.Voice);
    }

    /// <summary>
    /// Stories: the character cards in the story, so their spoken lines are read in their own voices. Null when the
    /// conversation is not a story or no card in it has a voice of its own, so the narrator reads everything.
    /// </summary>
    public async Task<VoiceCast?> CastForAsync(long conversationId, CancellationToken ct = default)
    {
        var conversation = await _chats.GetAsync(conversationId, ct).ConfigureAwait(false);
        if (conversation?.Mode != Story.Mode) return null;
        var story = await _stories.GetAsync(conversationId, ct).ConfigureAwait(false);
        if (story is null || story.Characters.Count == 0) return null;
        var cast = new List<StoryCastMember>();
        foreach (var link in story.Characters.DistinctBy(l => l.CharacterId))
            if (await _characters.GetAsync(link.CharacterId, ct).ConfigureAwait(false) is { } character) cast.Add(new StoryCastMember(character, link.Main));
        // The same people, in the same order, as the story page colours: cards with their voices, then Cast box names.
        var voices = cast.Select(m => VoiceId.Parse(m.Character.Voice)).ToList();
        return voices.Any(v => v is not null) ? new VoiceCast(StoryPrompt.People(story, cast), voices) : null;
    }

    private void OnChatChanged()
    {
        var active = _chat.Active;
        // The reply being followed has finished (or another took its place): read the rest of it.
        if (_following is not null && !ReferenceEquals(active, _following))
        {
            if (_followingKey is not null) _reader.End(_followingKey, ReadableText(_following));
            _lastFinished = _following;
            _following = null;
            _followingKey = null;
        }
        if (active is not { Kind: ReplyKind.Reply }) return;

        if (_following is null)
        {
            _following = active;
            if (!AutoRead)
            {
                // A new reply makes whatever was being read out of date.
                _reader.Stop();
                return;
            }
            // Group members answering one after another wait their turn instead of talking over each other.
            var queue = active.SpeakerId is not null && _lastFinished is { } last && last.ConversationId == active.ConversationId;
            _followingKey = "live:" + Guid.NewGuid().ToString("N");
            _reader.Begin(_followingKey, VoiceForAsync(active.ConversationId, active.SpeakerId), queue, CastForAsync(active.ConversationId));
        }
        if (_followingKey is not null) _reader.Feed(_followingKey, ReadableText(active));
    }

    /// <summary>A continued reply is read from where the continuation started.</summary>
    private static string ReadableText(ActiveReply reply) =>
        reply.StartLength > 0 && reply.Content.Length >= reply.StartLength ? reply.Content[reply.StartLength..] : reply.Content;

    public void Dispose() => _chat.Changed -= OnChatChanged;
}
