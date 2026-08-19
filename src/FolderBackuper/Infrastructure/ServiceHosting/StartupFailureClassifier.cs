using System.Net.Sockets;
using Microsoft.Data.Sqlite;

namespace FolderBackuper.Infrastructure.ServiceHosting;

public static class StartupFailureClassifier
{
    public static StartupFailure Classify(Exception exception)
    {
        foreach (var candidate in Unwrap(exception))
        {
            if (candidate is StartupFailureException typed)
            {
                return typed.Failure;
            }

            if (candidate is SocketException socket
                && socket.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
            {
                return StartupFailure.PortBinding;
            }

            if (candidate is SqliteException)
            {
                return StartupFailure.Migration;
            }

            if (candidate is UnauthorizedAccessException)
            {
                return StartupFailure.AccessControl;
            }
        }

        return StartupFailure.Unexpected;
    }

    private static IEnumerable<Exception> Unwrap(Exception exception)
    {
        var current = exception;
        while (current is not null)
        {
            yield return current;

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions.SelectMany(Unwrap))
                {
                    yield return inner;
                }

                yield break;
            }

            current = current.InnerException;
        }
    }
}
