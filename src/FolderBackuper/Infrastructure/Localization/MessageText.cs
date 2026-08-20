using System.Globalization;
using System.Resources;
using FolderBackuper.Infrastructure.Formatting;
using FolderBackuper.Resources;

namespace FolderBackuper.Infrastructure.Localization;

/// <summary>
/// Renders a <see cref="UiMessage"/> in the language currently being read.
/// </summary>
/// <remarks>
/// Lives beside the message type rather than in the components layer because notification email is
/// rendered by a background worker that no browser is attached to and needs the same resolution.
/// </remarks>
public static class MessageText
{
    private static readonly ResourceManager Resources = MessageStrings.ResourceManager;

    /// <summary>Renders a message, or the empty string when there is none.</summary>
    public static string Resolve(UiMessage? message) => message is null ? string.Empty : ResolveCore(message);

    private static string ResolveCore(UiMessage message)
    {
        var template = Resources.GetString(message.Key, CultureInfo.CurrentUICulture);
        if (template is null)
        {
            // A key with no entry is a defect the resource completeness tests exist to catch. Surfacing
            // the key is loud without failing the operation that produced the message, because a backup
            // must never be reported as broken because a sentence is missing.
            return message.Key;
        }

        if (message.Arguments.Count == 0)
        {
            return template;
        }

        var arguments = new object?[message.Arguments.Count];
        for (var index = 0; index < message.Arguments.Count; index++)
        {
            arguments[index] = Render(message.Arguments[index]);
        }

        return string.Format(CultureInfo.CurrentCulture, template, arguments);
    }

    private static string Render(UiMessageArgument argument) => argument.Kind switch
    {
        UiMessageArgumentKind.Message => argument.Message is null ? string.Empty : ResolveCore(argument.Message),
        UiMessageArgumentKind.Number => argument.Value.ToString("N0", CultureInfo.CurrentCulture),
        UiMessageArgumentKind.Bytes => DisplayFormat.Bytes(argument.Value),
        _ => argument.Text ?? string.Empty
    };
}
