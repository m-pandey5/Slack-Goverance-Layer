using System.Collections.Concurrent;

namespace Compass.Api.SRE;

public sealed class InMemoryCircuitBreaker : ICircuitBreaker
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan OpenDuration = TimeSpan.FromMinutes(10);
    private const int Threshold = 3;

    private sealed class AgentState
    {
        public readonly Queue<DateTimeOffset> Failures = new();
        public DateTimeOffset? OpenedAt;
    }

    private readonly ConcurrentDictionary<string, AgentState> _states = new(StringComparer.Ordinal);
    private readonly ILogger<InMemoryCircuitBreaker> _logger;

    public InMemoryCircuitBreaker(ILogger<InMemoryCircuitBreaker> logger)
    {
        _logger = logger;
    }

    public bool IsOpen(string agentId)
    {
        if (!_states.TryGetValue(agentId, out var state))
        {
            return false;
        }

        lock (state)
        {
            if (state.OpenedAt is null)
            {
                return false;
            }

            if (DateTimeOffset.UtcNow - state.OpenedAt.Value > OpenDuration)
            {
                state.OpenedAt = null;
                state.Failures.Clear();
                return false;
            }

            return true;
        }
    }

    public void RecordFailure(string agentId)
    {
        var state = _states.GetOrAdd(agentId, _ => new AgentState());
        var now = DateTimeOffset.UtcNow;

        lock (state)
        {
            state.Failures.Enqueue(now);
            PurgeOldFailures(state, now);

            if (state.Failures.Count >= Threshold && state.OpenedAt is null)
            {
                state.OpenedAt = now;
                _logger.LogWarning(
                    "[circuit-breaker] OPEN agent={AgentId} failures={Count} window=5m block=10m",
                    agentId,
                    state.Failures.Count);
            }
        }
    }

    public void RecordSuccess(string agentId)
    {
        if (!_states.TryGetValue(agentId, out var state))
        {
            return;
        }

        lock (state)
        {
            if (state.OpenedAt is not null &&
                DateTimeOffset.UtcNow - state.OpenedAt.Value > OpenDuration)
            {
                state.OpenedAt = null;
                state.Failures.Clear();
            }
        }
    }

    public CircuitBreakerStatus GetStatus(string agentId)
    {
        if (!_states.TryGetValue(agentId, out var state))
        {
            return new CircuitBreakerStatus { IsOpen = false, FailureCount = 0 };
        }

        lock (state)
        {
            PurgeOldFailures(state, DateTimeOffset.UtcNow);
            var isOpen = state.OpenedAt.HasValue &&
                         DateTimeOffset.UtcNow - state.OpenedAt.Value <= OpenDuration;
            return new CircuitBreakerStatus
            {
                IsOpen = isOpen,
                FailureCount = state.Failures.Count,
                OpenedAt = state.OpenedAt,
                ResetAt = state.OpenedAt.HasValue ? state.OpenedAt.Value + OpenDuration : null
            };
        }
    }

    public IReadOnlyDictionary<string, CircuitBreakerStatus> GetAllStatuses()
    {
        return _states.ToDictionary(
            kvp => kvp.Key,
            kvp => GetStatus(kvp.Key),
            StringComparer.Ordinal);
    }

    private static void PurgeOldFailures(AgentState state, DateTimeOffset now)
    {
        while (state.Failures.Count > 0 && now - state.Failures.Peek() > Window)
        {
            state.Failures.Dequeue();
        }
    }
}
