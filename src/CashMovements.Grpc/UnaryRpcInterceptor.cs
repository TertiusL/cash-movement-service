namespace CashMovements.Grpc;

public sealed class UnaryRpcInterceptor : Interceptor
{
    private const string OutcomeLog = "{Operation} finished with {Outcome}. Trace {TraceId}";

    private readonly ILogger<UnaryRpcInterceptor> _logger;
    private readonly IsTemporaryStoreFailure _isTemporaryStoreFailure;

    public UnaryRpcInterceptor(ILogger<UnaryRpcInterceptor> logger, IsTemporaryStoreFailure isTemporary)
    {
        _logger = logger;
        _isTemporaryStoreFailure = isTemporary;
    }

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(TRequest request,
        ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
    {
        string traceId = Activity.Current?.TraceId.ToString() ?? context.GetHttpContext().TraceIdentifier;
        context.ResponseTrailers.Add("trace-id", traceId);
        try
        {
            TResponse response = await continuation(request, context);
            string outcome = response is SubmitMovementReply submission ? submission.Disposition.ToString() : "OK";
            _logger.LogInformation(OutcomeLog, context.Method, outcome, traceId);
            return response;
        }
        catch (BusinessRpcException exception)
        {
            LogLevel level = exception.Reason == LedgerError.Conflict ? LogLevel.Warning : LogLevel.Information;
            _logger.Log(level, OutcomeLog, context.Method, exception.Reason, traceId);
            throw;
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            StatusCode status = context.Deadline <= DateTime.UtcNow ? StatusCode.DeadlineExceeded : StatusCode.Cancelled;
            _logger.LogDebug(OutcomeLog, context.Method, status, traceId);
            throw new RpcException(new Status(status, "The call ended before completion."));
        }
        catch (Exception exception) when (_isTemporaryStoreFailure(exception))
        {
            _logger.LogWarning(exception, OutcomeLog, context.Method, StatusCode.Unavailable, traceId);
            throw new RpcException(new Status(StatusCode.Unavailable, "The database is temporarily unavailable."));
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, OutcomeLog, context.Method, StatusCode.Internal, traceId);
            throw new RpcException(new Status(StatusCode.Internal, "The operation could not be completed."));
        }
    }
}
