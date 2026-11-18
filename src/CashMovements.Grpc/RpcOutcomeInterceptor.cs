namespace CashMovements.Grpc;

public sealed class RpcOutcomeInterceptor : Interceptor
{
    private const string OutcomeLog = "{Operation} finished with {Outcome}. Trace {TraceId}";

    private readonly ILogger<RpcOutcomeInterceptor> _logger;
    private readonly IsTemporaryStoreFailure _isTemporaryStoreFailure;

    public RpcOutcomeInterceptor(ILogger<RpcOutcomeInterceptor> logger, IsTemporaryStoreFailure isTemporary)
    {
        _logger = logger;
        _isTemporaryStoreFailure = isTemporary;
    }

    public override Task<TResponse> UnaryServerHandler<TRequest, TResponse>(TRequest request,
        ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
    {
        return ObserveCallAsync(context, () => continuation(request, context));
    }

    public override Task ServerStreamingServerHandler<TRequest, TResponse>(TRequest request,
        IServerStreamWriter<TResponse> responseStream, ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        return ObserveCallAsync(context, async () =>
        {
            await continuation(request, responseStream, context);
            return StatusCode.OK;
        });
    }

    private async Task<TResult> ObserveCallAsync<TResult>(ServerCallContext context, Func<Task<TResult>> execute)
    {
        string traceId = Activity.Current?.TraceId.ToString() ?? context.GetHttpContext().TraceIdentifier;
        context.ResponseTrailers.Add("trace-id", traceId);
        try
        {
            TResult response = await execute();
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
        catch (Exception exception) when (context.CancellationToken.IsCancellationRequested &&
            exception is OperationCanceledException or IOException or RpcException { StatusCode: StatusCode.Cancelled or StatusCode.DeadlineExceeded })
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
