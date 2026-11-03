namespace Gamertab.Services;

public class ActivitySignal
{
    private class State
    {
        public int Listeners { get; set; }
        public TaskCompletionSource Source { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly object gate = new();
    private readonly Dictionary<string, State> states = [];

    public ActivityWatch Watch(string userId)
    {
        lock (gate)
        {
            if (!states.TryGetValue(userId, out var state)) states[userId] = state = new();
            state.Listeners++;
            return new ActivityWatch(state.Source.Task, () =>
            {
                lock (gate)
                    if (--state.Listeners == 0) states.Remove(userId);
            });
        }
    }

    public void Publish(string userId)
    {
        lock (gate)
        {
            if (!states.TryGetValue(userId, out var state)) return;
            state.Source.TrySetResult();
            state.Source = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}

public sealed class ActivityWatch(Task changed, Action release) : IDisposable
{
    private Action? dispose = release;
    public Task Changed => changed;
    public void Dispose() => Interlocked.Exchange(ref dispose, null)?.Invoke();
}
