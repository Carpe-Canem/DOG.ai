using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace DogAI.Core.Settings;

/// <summary>
/// Unsaved typing in an editor (the character editor, story setup), held while the owner is on another screen (M14,
/// owner's decision 2026-11-15: going to Settings and back must not lose it). An editor that is left with changes
/// <see cref="Hold"/>s its form under its address; opening that address again <see cref="Take"/>s it back. Save, Cancel
/// and Discard <see cref="Forget"/> it. Memory only: never written anywhere, gone after a restart or a lock.
/// </summary>
public sealed class UnsavedForms
{
    private readonly Dictionary<string, object> _held = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    public int Count
    {
        get
        {
            lock (_gate) return _held.Count;
        }
    }

    public bool Holds(string address)
    {
        lock (_gate) return _held.ContainsKey(Key(address));
    }

    public void Hold(string address, object form)
    {
        lock (_gate) _held[Key(address)] = form;
    }

    /// <summary>The form held for <paramref name="address"/>, removed from here (the editor holds it again if it is left unsaved).</summary>
    public T? Take<T>(string address) where T : class
    {
        lock (_gate)
        {
            var key = Key(address);
            if (!_held.TryGetValue(key, out var form) || form is not T typed) return null;
            _held.Remove(key);
            return typed;
        }
    }

    public void Forget(string address)
    {
        lock (_gate) _held.Remove(Key(address));
    }

    /// <summary>The address as a key: no leading slash, no fragment. The query stays (<c>story/new?character=3</c>).</summary>
    public static string Key(string address)
    {
        var hash = address.IndexOf('#');
        return (hash >= 0 ? address[..hash] : address).TrimStart('/');
    }

    /// <summary>
    /// A fingerprint of a form's parts, to tell whether anything was changed since it was loaded: the parts as JSON,
    /// with pictures (byte arrays, at any depth) counted by a hash of their bytes so a large picture is not copied.
    /// </summary>
    public static string Print(params object?[] parts)
    {
        var text = new StringBuilder();
        foreach (var part in parts)
        {
            text.Append(part is byte[] bytes ? Hash(bytes) : JsonSerializer.Serialize(part, part?.GetType() ?? typeof(object), Options));
            text.Append('\u001f');
        }
        return text.ToString();
    }

    private static string Hash(byte[] bytes) => "#" + Convert.ToHexString(SHA256.HashData(bytes));

    private static readonly JsonSerializerOptions Options = new()
    {
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { PicturesByHash } },
    };

    /// <summary>Byte arrays inside a part are written as a hash of their bytes.</summary>
    private static void PicturesByHash(JsonTypeInfo info)
    {
        if (info.Kind != JsonTypeInfoKind.Object) return;
        foreach (var property in info.Properties.Where(p => p.PropertyType == typeof(byte[])))
            property.CustomConverter = HashConverter.Instance;
    }

    private sealed class HashConverter : JsonConverter<byte[]>
    {
        public static readonly HashConverter Instance = new();
        public override byte[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, byte[] value, JsonSerializerOptions options) => writer.WriteStringValue(Hash(value));
    }
}
