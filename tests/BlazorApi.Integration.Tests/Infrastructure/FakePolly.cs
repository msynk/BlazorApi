// Stand-ins for the Polly exception types the runtime recognizes by name, so the tests need no Polly dependency.
#pragma warning disable CA1032, CA1064, RCS1194 // Test doubles need no standard exception constructors.

namespace Polly
{
    public class ExecutionRejectedException : Exception
    {
        public ExecutionRejectedException()
            : base("Rejected by the resilience pipeline.")
        {
        }
    }
}

namespace Polly.Timeout
{
    public sealed class TimeoutRejectedException : ExecutionRejectedException
    {
    }
}

namespace Polly.CircuitBreaker
{
    public sealed class BrokenCircuitException : ExecutionRejectedException
    {
    }
}
