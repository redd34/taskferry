using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace TokioDotnet.Runtime;

/// <summary>Mirror of Rust <c>FfiOutcome&lt;T&gt;</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct FfiOutcome<T> where T : unmanaged
{
    public byte IsOk;
    public T Value;
    public FfiError Err;

    /// <summary>Returns the value or throws the marshaled error. Call at most once.</summary>
    public readonly T Unwrap() => IsOk != 0 ? Value : throw Err.ToException();
}

/// <summary>Converts a C-ABI value into its managed representation (statically dispatched).</summary>
public interface IFfiConverter<TNative, T> where TNative : unmanaged
{
    static abstract T Convert(TNative value);
}

public readonly struct IdentityConverter<T> : IFfiConverter<T, T> where T : unmanaged
{
    public static T Convert(T value) => value;
}

public readonly struct BoolConverter : IFfiConverter<byte, bool>
{
    public static bool Convert(byte value) => value != 0;
}

/// <summary>Decodes a Rust <c>String</c> and immediately frees it.</summary>
public readonly unsafe struct StringConverter : IFfiConverter<FfiOwnedSlice, string>
{
    public static string Convert(FfiOwnedSlice value)
    {
        try
        {
            // SAFETY: Rust-owned UTF-8 buffer, valid until Free() below.
            return Encoding.UTF8.GetString((byte*)value.Ptr, checked((int)value.Len));
        }
        finally
        {
            value.Free();
        }
    }
}

/// <summary>Wraps a Rust <c>Vec&lt;T&gt;</c> as zero-copy <see cref="IMemoryOwner{T}"/>.</summary>
public readonly struct OwnedSliceConverter<T> : IFfiConverter<FfiOwnedSlice, IMemoryOwner<T>> where T : unmanaged
{
    public static IMemoryOwner<T> Convert(FfiOwnedSlice value) => new RustMemoryManager<T>(value);
}

/// <summary>An object Rust calls back exactly once through <see cref="RustTaskBridge.Callback"/>.</summary>
public abstract unsafe class RustCallContext
{
    internal static long s_liveHandles;

    /// <summary>
    /// Allocates the GCHandle passed to Rust as <c>state</c>. Every value returned
    /// MUST be handed to Rust, which guarantees exactly one callback (that frees it).
    /// </summary>
    public nint AllocState()
    {
        Interlocked.Increment(ref s_liveHandles);
        return GCHandle.ToIntPtr(GCHandle.Alloc(this, GCHandleType.Normal));
    }

    /// <summary>Handles the outcome. Runs on a Tokio worker thread and must not throw or block.</summary>
    protected internal abstract void Complete(void* outcome);
}

public static unsafe class RustTaskBridge
{
    /// <summary>GCHandles currently held by Rust for pending calls (leak diagnostics).</summary>
    public static long LiveHandles => Interlocked.Read(ref RustCallContext.s_liveHandles);

    /// <summary>The single completion callback for all tasks and stream pulls.</summary>
    public static delegate* unmanaged[Cdecl]<nint, void*, void> Callback => &OnComplete;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnComplete(nint state, void* outcome)
    {
        // SAFETY: `state` is a GCHandle from AllocState and Rust's CompletionGuard
        // invokes this exactly once per handle, so it is freed exactly once.
        GCHandle handle = GCHandle.FromIntPtr(state);
        var context = (RustCallContext)handle.Target!;
        handle.Free();
        Interlocked.Decrement(ref RustCallContext.s_liveHandles);
        context.Complete(outcome);
    }
}

/// <summary>
/// State of one async Rust call: the <see cref="TaskCompletionSource{T}"/>, the pinned
/// inputs (released only once Rust is done with them) and the cancellation bridge.
/// </summary>
public sealed unsafe class RustTaskContext<TNative, T, TConv> : RustCallContext
    where TNative : unmanaged
    where TConv : IFfiConverter<TNative, T>
{
    // RunContinuationsAsynchronously: never run .NET continuations on a Tokio worker.
    private readonly TaskCompletionSource<T> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationToken _cancellationToken;
    private readonly RustCancellationHandle? _cancel;
    private readonly CancellationTokenRegistration _registration;
    private MemoryHandle[] _pins = [];
    private int _pinCount;

    public RustTaskContext(RustLibrary library, CancellationToken cancellationToken)
    {
        _cancellationToken = cancellationToken;
        if (cancellationToken.CanBeCanceled)
        {
            _cancel = new RustCancellationHandle(library);
            _registration = cancellationToken.UnsafeRegister(static s => ((RustCancellationHandle)s!).Cancel(), _cancel);
        }
    }

    public Task<T> Task => _tcs.Task;

    /// <summary>Rust token pointer for this call, or 0 when the call cannot be cancelled.</summary>
    public nint CancelToken => _cancel?.DangerousGetHandle() ?? 0;

    /// <summary>Pins <paramref name="memory"/> until the call completes (or <see cref="Abort"/>).</summary>
    public FfiReadOnlySlice Pin<TElement>(ReadOnlyMemory<TElement> memory) where TElement : unmanaged
    {
        MemoryHandle pin = memory.Pin();
        if (_pinCount == _pins.Length) Array.Resize(ref _pins, Math.Max(2, _pins.Length * 2));
        _pins[_pinCount++] = pin;
        return new FfiReadOnlySlice(pin.Pointer, memory.Length);
    }

    /// <summary>Releases resources when the native call was never started.</summary>
    public void Abort() => Cleanup();

    private void Cleanup()
    {
        for (int i = 0; i < _pinCount; i++) _pins[i].Dispose();
        _pinCount = 0;
        _registration.Dispose();
        _cancel?.Dispose();
    }

    protected internal override void Complete(void* outcome)
    {
        // SAFETY: Rust passes a pointer to a live FfiOutcome<TNative> for the
        // duration of the callback; we copy it out before returning.
        FfiOutcome<TNative> result = *(FfiOutcome<TNative>*)outcome;
        try
        {
            // Rust has dropped the future, so the pinned inputs are no longer referenced.
            Cleanup();
            if (result.IsOk != 0)
            {
                _tcs.TrySetResult(TConv.Convert(result.Value));
            }
            else
            {
                Exception error = result.Err.ToException(_cancellationToken);
                if (error is OperationCanceledException) _tcs.TrySetCanceled(_cancellationToken);
                else _tcs.TrySetException(error);
            }
        }
        catch (Exception e)
        {
            // Nothing may escape into Rust (e.g. a user MemoryManager.Unpin that throws).
            _tcs.TrySetException(e);
        }
    }
}
