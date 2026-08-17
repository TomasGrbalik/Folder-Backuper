namespace FolderBackuper.Infrastructure.Security;

public interface INetworkImpersonator
{
    Task<T> RunAsync<T>(string username, string password, Func<Task<T>> action);
}
