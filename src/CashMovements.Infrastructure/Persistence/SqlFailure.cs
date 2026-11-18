namespace CashMovements.Infrastructure.Persistence;

public static class SqlFailure
{
    public static bool IsTemporary(Exception exception)
    {
        for (Exception? cause = exception; cause is not null; cause = cause.InnerException)
        {
            if (cause is not SqlException sqlException)
                continue;

            foreach (SqlError error in sqlException.Errors)
            {
                // SQL timeout or deadlock, followed by the Windows network and wait errors.
                if (error.Number is -2 or 1205 or 1222 or 53 or 64 or 121 or 233 or 258
                    || IsConnectionError((SocketError)error.Number))
                    return true;
            }

            // Some SqlClient transports wrap the socket error instead of exposing its number.
            if (sqlException.GetBaseException() is SocketException socketException && IsConnectionError(socketException.SocketErrorCode))
                return true;
        }

        return false;
    }

    private static bool IsConnectionError(SocketError error)
    {
        return error is SocketError.NetworkUnreachable or SocketError.ConnectionAborted or SocketError.ConnectionReset
            or SocketError.TimedOut or SocketError.ConnectionRefused or SocketError.HostUnreachable;
    }
}
