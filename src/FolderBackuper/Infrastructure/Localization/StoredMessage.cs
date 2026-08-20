using System.Text.Json;
using System.Text.Json.Serialization;

namespace FolderBackuper.Infrastructure.Localization;

/// <summary>
/// Serializes a message's arguments for the columns that record run problems and error summaries.
/// </summary>
/// <remarks>
/// The permanent history stores a message code and its arguments rather than a sentence, so a run that
/// failed while the interface was English renders in Slovak once the language changes. The key travels in
/// its own column; only the arguments need encoding, and a message with none stores nothing at all.
/// </remarks>
public static class StoredMessage
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    /// <summary>Encodes arguments for storage, returning null when there are none to store.</summary>
    public static string? EncodeArguments(UiMessage? message) =>
        message is null || message.Arguments.Count == 0
            ? null
            : JsonSerializer.Serialize(message.Arguments, Options);

    /// <summary>
    /// Rebuilds a message from the stored key and arguments. Arguments that cannot be read are dropped
    /// rather than thrown, because permanent history has to remain readable even if a row is damaged.
    /// </summary>
    public static UiMessage? Decode(string? key, string? arguments)
    {
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        if (string.IsNullOrEmpty(arguments))
        {
            return new UiMessage(key, []);
        }

        try
        {
            var decoded = JsonSerializer.Deserialize<UiMessageArgument[]>(arguments, Options);
            return new UiMessage(key, decoded ?? []);
        }
        catch (JsonException)
        {
            return new UiMessage(key, []);
        }
    }
}
