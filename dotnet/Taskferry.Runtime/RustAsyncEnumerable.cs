using System.Threading.Tasks.Sources;

namespace Taskferry.Runtime;

/// <summary>
/// Pull-based cursor over a Rust <c>Stream</c>. Each <see cref="MoveNextAsync"/> asks Rust
/// for exactly one item (backpressure); <see cref="DisposeAsync"/> drops the Rust stream
/// immediately. No <see cref="Task"/> is allocated per item (reusable <see cref="IValueTaskSource{T}"/>).
/// Like any enumerator, it must not be used concurrently.
/// </summary>
public sealed unsafe class RustAsyncEnumerator<TNative, T, TConv> : RustCallContext, IAsyncEnumerator<T>, IValueTaskSource<bool>
    where TNative : unmanaged
    where TConv : IFfiConverter<TNative, T>
{
    private readonly Cursor _cursor;
    private readonly CancellationToken _cancellationToken;
    private readonly CancellationTokenRegistration _registration;
    // RunContinuationsAsynchronously: never run .NET continuations on a Tokio worker.
    private ManualResetValueTaskSourceCore<bool> _source = new() { RunContinuationsAsynchronously = true };

    /// <param name="cursor">Pointer returned by a generated stream-open shim; ownership is taken.</param>
    public RustAsyncEnumerator(RustLibrary library, nint cursor, CancellationToken cancellationToken)
    {
        _cursor = new Cursor(library, cursor);
        _cancellationToken = cancellationToken;
        _registration = cancellationToken.UnsafeRegister(static s => ((Cursor)s!).Cancel(), _cursor);
    }

    public T Current { get; private set; } = default!;

    public ValueTask<bool> MoveNextAsync()
    {
        _source.Reset();
        _cursor.Next(this);
        return new ValueTask<bool>(this, _source.Version);
    }

    protected internal override void Complete(void* outcome)
    {
        // SAFETY: Rust passes a pointer to a live FfiOutcome<TNative> for the
        // duration of the callback; we copy it out before returning.
        FfiOutcome<TNative> result = *(FfiOutcome<TNative>*)outcome;
        bool hasItem = result.IsOk != 0;
        Exception? error = null;
        try
        {
            if (hasItem) Current = TConv.Convert(result.Value);
            else if (result.Err.Code != (int)RustErrorCode.EndOfStream) error = result.Err.ToException(_cancellationToken);
        }
        catch (Exception e)
        {
            // Nothing may escape into Rust.
            error = e;
        }
        if (error is null) _source.SetResult(hasItem);
        else _source.SetException(error);
    }

    public ValueTask DisposeAsync()
    {
        _registration.Dispose();
        _cursor.Dispose();
        return default;
    }

    bool IValueTaskSource<bool>.GetResult(short token) => _source.GetResult(token);
    ValueTaskSourceStatus IValueTaskSource<bool>.GetStatus(short token) => _source.GetStatus(token);
    void IValueTaskSource<bool>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        => _source.OnCompleted(continuation, state, token, flags);

    /// <summary>Owns the Rust <c>FfiStreamCursor</c>.</summary>
    private sealed class Cursor(RustLibrary library, nint cursor) : RustSafeHandle(cursor)
    {
        public void Next(RustCallContext context)
        {
            bool addedRef = false;
            try
            {
                DangerousAddRef(ref addedRef); // throws ObjectDisposedException after DisposeAsync
                // SAFETY: the ref taken above keeps the cursor alive for this call;
                // Rust fires the callback exactly once for the state allocated here.
                library.StreamNext(handle, context.AllocState(), RustTaskBridge.Callback);
            }
            finally
            {
                if (addedRef) DangerousRelease();
            }
        }

        public void Cancel()
        {
            bool addedRef = false;
            try
            {
                DangerousAddRef(ref addedRef);
                // SAFETY: the ref taken above keeps the cursor alive for this call.
                library.StreamCancel(handle);
            }
            catch (ObjectDisposedException)
            {
                // Already disposed: the stream is gone, nothing to cancel.
            }
            finally
            {
                if (addedRef) DangerousRelease();
            }
        }

        protected override bool ReleaseHandle()
        {
            // SAFETY: runs exactly once, after every AddRef'd user has finished.
            library.StreamFree(handle);
            return true;
        }
    }
}
