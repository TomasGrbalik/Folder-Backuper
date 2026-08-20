namespace FolderBackuper.Infrastructure.Localization;

/// <summary>What an argument substituted into a message is, and therefore how it is rendered.</summary>
public enum UiMessageArgumentKind
{
    /// <summary>Language-neutral text: a path, a job name, a provider's own error code.</summary>
    Text,

    /// <summary>Another message, rendered in the reading language and substituted in.</summary>
    Message,

    /// <summary>A count, formatted with the reading culture's group separators.</summary>
    Number,

    /// <summary>A byte count, formatted through the shared display helper.</summary>
    Bytes
}

/// <summary>
/// One argument substituted into a message. Kept as a discriminated value rather than an
/// <see cref="object"/> so that an argument survives being written to the database and read back in a
/// different language: a nested message is stored as its own code, and a byte count is stored as a
/// number rather than as the text some earlier language formatted it into.
/// </summary>
public sealed record UiMessageArgument(UiMessageArgumentKind Kind, string? Text, UiMessage? Message, long Value)
{
    public static UiMessageArgument FromText(string? text) => new(UiMessageArgumentKind.Text, text, null, 0);

    public static UiMessageArgument FromMessage(UiMessage message) => new(UiMessageArgumentKind.Message, null, message, 0);

    public static UiMessageArgument FromNumber(long value) => new(UiMessageArgumentKind.Number, null, null, value);

    public static UiMessageArgument FromBytes(long value) => new(UiMessageArgumentKind.Bytes, null, null, value);
}

/// <summary>
/// A reference to a piece of user-facing text, as a resource key and its arguments rather than as a
/// finished sentence.
/// </summary>
/// <remarks>
/// Services below the presentation layer return these instead of English strings, so the language a
/// message is read in is decided where it is displayed rather than where it is produced. The key is
/// always derived from an enumeration member by <see cref="For{TEnum}"/>, never written as a literal, so
/// a renamed message breaks the build and a message with no resource entry fails the completeness tests.
/// </remarks>
public sealed record UiMessage(string Key, IReadOnlyList<UiMessageArgument> Arguments)
{
    private static readonly UiMessageArgument[] None = [];

    public static UiMessage For<TEnum>(TEnum code) where TEnum : struct, Enum =>
        new(KeyFor(code), None);

    public static UiMessage For<TEnum>(TEnum code, params UiMessageArgument[] arguments) where TEnum : struct, Enum =>
        new(KeyFor(code), arguments);

    /// <summary>The resource key an enumeration member maps to, as <c>TypeName_MemberName</c>.</summary>
    public static string KeyFor<TEnum>(TEnum code) where TEnum : struct, Enum =>
        KeyFor(typeof(TEnum), code.ToString()!);

    internal static string KeyFor(Type enumType, string memberName) => $"{enumType.Name}_{memberName}";
}
