namespace LakeCode.Core.Conversations.Interactions;

/// <summary>Serializes presentation of questions and approvals without holding the conversation state lock.</summary>
internal sealed class InteractionGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public async ValueTask<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(_semaphore);
    }

    private sealed class Lease(SemaphoreSlim semaphore) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) semaphore.Release();
        }
    }
}
