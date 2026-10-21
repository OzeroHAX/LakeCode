namespace LakeCode.Core.Conversations.Interactions;

/// <summary>One cancellable wait; its registration is removed by the service before the handle is disposed.</summary>
internal sealed class PendingInteraction<T> : IDisposable
{
    private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenRegistration _cancellation;
    public Guid Id { get; } = Guid.NewGuid();
    public Task<T> Task => _completion.Task;
    public bool IsCompleted => Task.IsCompleted;

    public PendingInteraction(CancellationToken cancellationToken) =>
        _cancellation = cancellationToken.Register(() => _completion.TrySetCanceled(cancellationToken));

    public bool Resolve(T value) => _completion.TrySetResult(value);
    public bool Cancel() => _completion.TrySetCanceled();
    public bool Fail(Exception error) => _completion.TrySetException(error);
    public void Dispose() => _cancellation.Dispose();
}
