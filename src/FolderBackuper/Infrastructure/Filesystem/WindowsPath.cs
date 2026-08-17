namespace FolderBackuper.Infrastructure.Filesystem;

public sealed record PathValidationResult(bool IsValid, string? Path, string? Error)
{
    public static PathValidationResult Valid(string path) => new(true, path, null);
    public static PathValidationResult Invalid(string error) => new(false, null, error);
}

public static class WindowsPath
{
    private static readonly char[] Separators = ['\\', '/'];

    public static PathValidationResult Local(string? value)
    {
        if (HasUnsupportedForm(value, out var error) || value!.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return PathValidationResult.Invalid(error ?? "A local absolute drive path is required.");
        }

        if (!Path.IsPathFullyQualified(value) || Path.GetPathRoot(value)?.Length != 3)
        {
            return PathValidationResult.Invalid("A local absolute drive path is required.");
        }

        try
        {
            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
            if (new DriveInfo(Path.GetPathRoot(path)!).DriveType == DriveType.Network)
            {
                return PathValidationResult.Invalid("Mapped network drives are not supported; use an SMB destination.");
            }

            return PathValidationResult.Valid(path);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return PathValidationResult.Invalid("The local path is invalid or unavailable.");
        }
    }

    public static PathValidationResult Unc(string? value)
    {
        if (HasUnsupportedForm(value, out var error) || !value!.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return PathValidationResult.Invalid(error ?? "A conventional UNC path is required.");
        }

        var parts = value.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[0] is "." or ".." || parts[1] is "." or "..")
        {
            return PathValidationResult.Invalid("The UNC path must include a server and share.");
        }

        try
        {
            return PathValidationResult.Valid(Path.TrimEndingDirectorySeparator(Path.GetFullPath(value)));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return PathValidationResult.Invalid("The UNC path is invalid.");
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
            return PathValidationResult.Invalid("The subfolder must be relative and cannot contain parent traversal.");
        }

        return PathValidationResult.Valid(string.Join('\\', trimmed.Split('\\')));
    }

    private static bool HasUnsupportedForm(string? value, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "A path is required.";
            return true;
        }

        if (value.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            value.StartsWith(@"\\.\", StringComparison.Ordinal) ||
            value.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            error = "Device paths are not supported.";
            return true;
        }

        if (value.Split(Separators).Any(segment => segment == ".."))
        {
            error = "Parent traversal is not supported.";
            return true;
        }

        return false;
    }
}
