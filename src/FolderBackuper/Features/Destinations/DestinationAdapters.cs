using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using FolderBackuper.Infrastructure.Security;

using FolderBackuper.Infrastructure.Localization;
namespace FolderBackuper.Features.Destinations;

public interface IDestinationAdapter
{
    DestinationType Type { get; }
    Task<DestinationOperationResult> TestAsync(DestinationAccessConfiguration configuration, CancellationToken cancellationToken);
    Task<long?> GetAvailableBytesAsync(DestinationAccessConfiguration configuration, CancellationToken cancellationToken);
    Task<T> ExecuteAsync<T>(DestinationAccessConfiguration configuration, Func<Task<T>> action);
}

public abstract class DestinationAdapterBase : IDestinationAdapter
{
    public abstract DestinationType Type { get; }
    public abstract Task<T> ExecuteAsync<T>(DestinationAccessConfiguration configuration, Func<Task<T>> action);

    public async Task<DestinationOperationResult> TestAsync(DestinationAccessConfiguration configuration, CancellationToken cancellationToken)
    {
        try
        {
            return await ExecuteAsync(configuration, async () =>
            {
                var bytes = RandomNumberGenerator.GetBytes(4096);
                var path = Path.Combine(configuration.RootPath, $".folder-backuper-access-{Guid.NewGuid():N}.tmp");
                var created = false;
                try
                {
                    await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                        FileOptions.Asynchronous | FileOptions.WriteThrough))
                    {
                        created = true;
                        await stream.WriteAsync(bytes, cancellationToken);
                        stream.Flush(flushToDisk: true);
                    }

                    var actual = await File.ReadAllBytesAsync(path, cancellationToken);
                    var bytesMatch = CryptographicOperations.FixedTimeEquals(bytes, actual);
                    try { File.Delete(path); }
                    catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                    { return new(false, DestinationAccessResult.CleanupFailed, UiMessage.For(DestinationMessage.TestFileNotCleanedUp), cleanup.HResult & 0xFFFF); }
                    created = false;
                    if (!bytesMatch) return new(false, DestinationAccessResult.Failed, UiMessage.For(DestinationMessage.TestBytesNotPreserved));
                }
                catch
                {
                    if (created)
                    {
                        try { File.Delete(path); }
                        catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                        { return new(false, DestinationAccessResult.CleanupFailed, UiMessage.For(DestinationMessage.TestFileNotCleanedUp), cleanup.HResult & 0xFFFF); }
                    }
                    throw;
                }
                return DestinationOperationResult.Success(DestinationMessage.TestSucceeded,
                    await GetAvailableBytesCoreAsync(configuration.RootPath));
            });
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException)
        {
            return MapFailure(exception);
        }
    }

    public async Task<long?> GetAvailableBytesAsync(DestinationAccessConfiguration configuration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try { return await ExecuteAsync(configuration, () => GetAvailableBytesCoreAsync(configuration.RootPath)); }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException) { return null; }
    }

    private static Task<long?> GetAvailableBytesCoreAsync(string path)
    {
        if (!GetDiskFreeSpaceExW(path, out var available, out _, out _)) return Task.FromResult<long?>(null);
        return Task.FromResult<long?>((long)Math.Min(available, long.MaxValue));
    }

    private static DestinationOperationResult MapFailure(Exception exception)
    {
        var code = exception is Win32Exception win32 ? win32.NativeErrorCode : exception.HResult & 0xFFFF;
        var result = code switch
        {
            5 => DestinationAccessResult.AccessDenied,
            86 or 1326 or 1909 => DestinationAccessResult.AuthenticationFailed,
            3 or 123 or 161 => DestinationAccessResult.InvalidPath,
            53 or 64 or 67 or 121 or 1231 => DestinationAccessResult.Unavailable,
            _ => DestinationAccessResult.Failed
        };
        var message = result switch
        {
            DestinationAccessResult.AccessDenied => DestinationMessage.AccessDenied,
            DestinationAccessResult.AuthenticationFailed => DestinationMessage.CredentialsRejected,
            DestinationAccessResult.InvalidPath => DestinationMessage.PathInvalid,
            DestinationAccessResult.Unavailable => DestinationMessage.Unavailable,
            _ => DestinationMessage.AccessTestFailed
        };
        return new(false, result, UiMessage.For(message), code);
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceExW(string directory, out ulong available, out ulong total, out ulong free);
}

public sealed class LocalDestinationAdapter : DestinationAdapterBase
{
    public override DestinationType Type => DestinationType.Local;
    public override Task<T> ExecuteAsync<T>(DestinationAccessConfiguration configuration, Func<Task<T>> action) => action();
}

public sealed class SmbDestinationAdapter(INetworkImpersonator impersonator) : DestinationAdapterBase
{
    public override DestinationType Type => DestinationType.Smb;

    public override Task<T> ExecuteAsync<T>(DestinationAccessConfiguration configuration, Func<Task<T>> action) =>
        impersonator.RunAsync(
            configuration.Username ?? throw new InvalidOperationException("An SMB username is required."),
            configuration.Password ?? throw new InvalidOperationException("An SMB password is required."),
            action);
}
