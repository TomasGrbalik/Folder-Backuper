using FolderBackuper.Infrastructure.Localization;

namespace FolderBackuper.Infrastructure.Filesystem;

public sealed record PathValidationResult(bool IsValid, string? Path, UiMessage? Error)
{
    public static PathValidationResult Valid(string path) => new(true, path, null);

    public static PathValidationResult Invalid(PathMessage error) => new(false, null, UiMessage.For(error));

    public static PathValidationResult Invalid(UiMessage error) => new(false, null, error);
}

public static class WindowsPath
{
    private static readonly char[] Separators = ['\\', '/'];

    public static PathValidationResult Local(string? value)
    {
        if (HasUnsupportedForm(value, out var error) || value!.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return PathValidationResult.Invalid(error ?? PathMessage.LocalAbsoluteRequired);
        }

        if (!Path.IsPathFullyQualified(value) || Path.GetPathRoot(value)?.Length != 3)
        {
            return PathValidationResult.Invalid(PathMessage.LocalAbsoluteRequired);
        }

        try
        {
            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
            if (new DriveInfo(Path.GetPathRoot(path)!).DriveType == DriveType.Network)
            {
                return PathValidationResult.Invalid(PathMessage.MappedNetworkDriveUnsupported);
            }

            return PathValidationResult.Valid(path);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return PathValidationResult.Invalid(PathMessage.LocalPathUnavailable);
        }
    }

    public static PathValidationResult Unc(string? value)
    {
        if (HasUnsupportedForm(value, out var error) || !value!.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return PathValidationResult.Invalid(error ?? PathMessage.UncRequired);
        }

        var parts = value.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[0] is "." or ".." || parts[1] is "." or "..")
        {
            return PathValidationResult.Invalid(PathMessage.UncNeedsServerAndShare);
        }

        try
        {
            return PathValidationResult.Valid(Path.TrimEndingDirectorySeparator(Path.GetFullPath(value)));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return PathValidationResult.Invalid(PathMessage.UncInvalid);
        }
    }

    public static PathValidationResult Relative(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return PathValidationResult.Valid(string.Empty);
        }

        var trimmed = value.Trim().Replace('/', '\\');
        if (Path.IsPathRooted(trimmed) || trimmed.Split('\\').Any(x =>
                x is ".." or "." || x.Length == 0 || x.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        {
            return PathValidationResult.Invalid(PathMessage.SubfolderMustBeRelative);
        }

        return PathValidationResult.Valid(string.Join('\\', trimmed.Split('\\')));
    }

    private static bool HasUnsupportedForm(string? value, out PathMessage? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = PathMessage.Required;
            return true;
        }

        if (value.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            value.StartsWith(@"\\.\", StringComparison.Ordinal) ||
            value.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            error = PathMessage.DevicePathUnsupported;
            return true;
        }

        if (value.Split(Separators).Any(segment => segment == ".."))
        {
            error = PathMessage.ParentTraversalUnsupported;
            return true;
        }

        return false;
    }
}
