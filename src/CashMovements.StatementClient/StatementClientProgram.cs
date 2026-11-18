namespace CashMovements.StatementClient;

public static class StatementClientProgram
{
    public static async Task<int> Main(string[] args)
    {
        using CancellationTokenSource cancellation = new();

        void CancelExport(object? sender, ConsoleCancelEventArgs keyPress)
        {
            keyPress.Cancel = true;
            cancellation.Cancel();
        }

        Console.CancelKeyPress += CancelExport;
        try
        {
            return await RunAsync(args, Console.Error, cancellation.Token);
        }
        finally
        {
            Console.CancelKeyPress -= CancelExport;
        }
    }

    public static async Task<int> RunAsync(string[] args, TextWriter progress, CancellationToken cancellationToken)
    {
        StatementClientOptions options;
        try
        {
            options = StatementClientOptions.Parse(args);
        }
        catch (ArgumentException exception)
        {
            await progress.WriteLineAsync(exception.Message);
            await progress.WriteLineAsync(StatementClientOptions.Usage);
            return ClientExitCodes.InvalidArguments;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using GrpcChannel channel = GrpcChannel.ForAddress(options.Endpoint);
            LedgerClient client = new(channel);
            using AsyncServerStreamingCall<StatementFrame> call = client.ExportStatement(
                options.Request, cancellationToken: cancellationToken);
            await StatementCsvExporter.ExportAsync(call.ResponseStream, options.OutputPath, progress, cancellationToken);
            return ClientExitCodes.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await progress.WriteLineAsync("Export cancelled.");
            return ClientExitCodes.Cancelled;
        }
        catch (RpcException exception) when (
            exception.StatusCode == StatusCode.Cancelled && cancellationToken.IsCancellationRequested)
        {
            await progress.WriteLineAsync("Export cancelled.");
            return ClientExitCodes.Cancelled;
        }
        catch (RpcException exception)
        {
            string reason = ReadBusinessReason(exception);
            await progress.WriteLineAsync($"Export failed ({exception.StatusCode}{reason}): {exception.Status.Detail}");
            return ClientExitCodes.ExportFailure;
        }
        catch (Exception exception)
        {
            await progress.WriteLineAsync($"Export failed: {exception.Message}");
            return ClientExitCodes.ExportFailure;
        }
    }

    private static string ReadBusinessReason(RpcException exception)
    {
        Metadata.Entry? trailer = exception.Trailers.Get("cash-error-bin");
        if (trailer is null)
        {
            return string.Empty;
        }

        try
        {
            return $", {BusinessError.Parser.ParseFrom(trailer.ValueBytes).Reason}";
        }
        catch (InvalidProtocolBufferException)
        {
            return string.Empty;
        }
    }
}
