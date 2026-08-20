using FolderBackuper.Infrastructure.Localization;

namespace FolderBackuper.Tests;

/// <summary>
/// Asserts which message a service returned.
/// </summary>
/// <remarks>
/// These assertions used to match a substring of an English sentence, which passed for the wrong reason
/// whenever two messages shared a phrase and broke whenever wording was tightened. Comparing the code
/// instead is exact, and it is the same code the interface resolves, so a test cannot pass against text
/// no page would ever show.
/// </remarks>
internal static class MessageAssert
{
    /// <summary>Asserts that the message is exactly the given code.</summary>
    internal static void Is<TEnum>(TEnum expected, UiMessage? actual) where TEnum : struct, Enum
    {
        Assert.NotNull(actual);
        Assert.Equal(UiMessage.KeyFor(expected), actual.Key);
    }

    /// <summary>Asserts that no message was recorded.</summary>
    internal static void IsNone(UiMessage? actual) => Assert.Null(actual);

    /// <summary>The message rendered in the current language, for tests that assert on wording.</summary>
    internal static string Text(UiMessage? message) => MessageText.Resolve(message);
}
