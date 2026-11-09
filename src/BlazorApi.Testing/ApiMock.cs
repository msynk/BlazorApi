using System.Collections.Concurrent;
using BlazorApi.Runtime;

namespace BlazorApi.Testing;

/// <summary>A recorded call to a mocked operation.</summary>
public sealed class ApiMockCall
{
    /// <summary>Creates a call record.</summary>
    public ApiMockCall(string operationId, IReadOnlyDictionary<string, object?> arguments)
    {
        OperationId = operationId;
        Arguments = arguments;
    }

    /// <summary>The operation identifier.</summary>
    public string OperationId { get; }

    /// <summary>The arguments by parameter name (the body is named <c>body</c>).</summary>
    public IReadOnlyDictionary<string, object?> Arguments { get; }

    /// <summary>Gets an argument.</summary>
    public T Argument<T>(string name) => Arguments.TryGetValue(name, out var value) ? (T)value! : throw new KeyNotFoundException($"'{OperationId}' has no argument '{name}'.");

    /// <inheritdoc />
    public override string ToString() =>
        OperationId + "(" + string.Join(", ", Arguments.Select(a => a.Key + ": " + (a.Value ?? "null"))) + ")";
}

/// <summary>Thrown when a mock verification fails.</summary>
public sealed class ApiMockVerificationException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ApiMockVerificationException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// The base of generated client mocks: records every call and answers with configured results, delays or exceptions.
/// Operations without a setup fail with HTTP 501 so a missing setup is visible in the test.
/// </summary>
public abstract class ApiMock
{
    private readonly ConcurrentQueue<ApiMockCall> _calls = new();
    private readonly ConcurrentDictionary<string, Func<ApiMockCall, CancellationToken, Task<object>>> _setups = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TimeSpan> _delays = new(StringComparer.Ordinal);

    /// <summary>Every call in order.</summary>
    public IReadOnlyList<ApiMockCall> Calls => _calls.ToArray();

    /// <summary>The calls to one operation in order.</summary>
    public IReadOnlyList<ApiMockCall> CallsTo(string operationId) => _calls.Where(c => c.OperationId == operationId).ToArray();

    /// <summary>Verifies how often an operation was called.</summary>
    /// <exception cref="ApiMockVerificationException">The count differs.</exception>
    public void Verify(string operationId, int times = 1)
    {
        var count = CallsTo(operationId).Count;
        if (count != times)
        {
            throw new ApiMockVerificationException(
                $"Expected {times} call(s) to '{operationId}' but found {count}. Calls: {string.Join("; ", Calls)}");
        }
    }

    /// <summary>Verifies that an operation was called with arguments matching a predicate.</summary>
    public void Verify(string operationId, Func<ApiMockCall, bool> predicate)
    {
        if (!CallsTo(operationId).Any(predicate))
        {
            throw new ApiMockVerificationException($"No call to '{operationId}' matched. Calls: {string.Join("; ", CallsTo(operationId))}");
        }
    }

    /// <summary>Verifies that nothing was called besides the verified operations.</summary>
    public void VerifyNoOtherCalls(params string[] operationIds)
    {
        var others = Calls.Where(c => !operationIds.Contains(c.OperationId, StringComparer.Ordinal)).ToList();
        if (others.Count > 0)
        {
            throw new ApiMockVerificationException("Unexpected calls: " + string.Join("; ", others));
        }
    }

    /// <summary>Clears calls and setups.</summary>
    public void Reset()
    {
        while (_calls.TryDequeue(out _))
        {
        }

        _setups.Clear();
        _delays.Clear();
    }

    /// <summary>Answers an operation with a result factory.</summary>
    public void Setup<T>(string operationId, Func<ApiMockCall, ApiResult<T>> respond) =>
        _setups[operationId] = (call, _) => Task.FromResult<object>(respond(call));

    /// <summary>Answers an operation without a body type with a result factory.</summary>
    public void Setup(string operationId, Func<ApiMockCall, ApiResult> respond) =>
        _setups[operationId] = (call, _) => Task.FromResult<object>(respond(call));

    /// <summary>Answers an operation asynchronously.</summary>
    public void SetupAsync<T>(string operationId, Func<ApiMockCall, CancellationToken, Task<ApiResult<T>>> respond) =>
        _setups[operationId] = async (call, token) => await respond(call, token).ConfigureAwait(false);

    /// <summary>Makes an operation fail with an HTTP error.</summary>
    public void SetupError(string operationId, int statusCode, object? content = null) =>
        _setups[operationId] = (_, _) => Task.FromResult<object>(ApiError.Http(statusCode, content, operationId: operationId));

    /// <summary>Makes an operation fail with a network error.</summary>
    public void SetupNetworkFailure(string operationId, Exception? exception = null) =>
        _setups[operationId] = (_, _) => Task.FromResult<object>(ApiError.Network(exception ?? new HttpRequestException("Simulated network failure."), operationId));

    /// <summary>Makes an operation time out.</summary>
    public void SetupTimeout(string operationId) =>
        _setups[operationId] = (_, _) => Task.FromResult<object>(ApiError.Timeout(operationId));

    /// <summary>Makes an operation throw (for exceptions a real client would not convert to results).</summary>
    public void SetupException(string operationId, Exception exception) =>
        _setups[operationId] = (_, _) => Task.FromException<object>(exception);

    /// <summary>Delays every answer of an operation (cancellation is honored).</summary>
    public void SetupDelay(string operationId, TimeSpan delay) => _delays[operationId] = delay;

    /// <summary>Records a call and produces its result. Called by generated mocks.</summary>
    protected async Task<ApiResult<T>> InvokeAsync<T>(string operationId, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        var outcome = await ProduceAsync(operationId, arguments, cancellationToken).ConfigureAwait(false);
        return outcome switch
        {
            ApiResult<T> typed => typed,
            ApiError error => ApiResults.Failure<T>(error),
            ApiResult { Error: { } error } => ApiResults.Failure<T>(error),
            _ => throw new InvalidOperationException($"The setup of '{operationId}' returned {outcome.GetType().Name}, not ApiResult<{typeof(T).Name}>."),
        };
    }

    /// <summary>Records a call to an operation without a body type and produces its result. Called by generated mocks.</summary>
    protected async Task<ApiResult> InvokeAsync(string operationId, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        var outcome = await ProduceAsync(operationId, arguments, cancellationToken).ConfigureAwait(false);
        return outcome switch
        {
            ApiResult result => result,
            ApiError error => ApiResults.Failure(error),
            _ => throw new InvalidOperationException($"The setup of '{operationId}' returned {outcome.GetType().Name}, not ApiResult."),
        };
    }

    private async Task<object> ProduceAsync(string operationId, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var call = new ApiMockCall(operationId, arguments);
        _calls.Enqueue(call);
        if (_delays.TryGetValue(operationId, out var delay))
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        if (_setups.TryGetValue(operationId, out var setup))
        {
            return await setup(call, cancellationToken).ConfigureAwait(false);
        }

        return ApiError.Http(501, body: $"No mock response is set up for '{operationId}'.", operationId: operationId);
    }
}
