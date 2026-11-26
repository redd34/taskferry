using System.Buffers;
using System.Runtime.InteropServices;

namespace TokioDotnet.Runtime;

/// <summary>Mirror of Rust <c>FfiReadOnlySlice&lt;T&gt;</c>: pinned .NET memory lent to Rust.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct FfiReadOnlySlice
{
    public void* Ptr;
    public nuint Len;

    public FfiReadOnlySlice(void* ptr, int len)
    {
        Ptr = ptr;
        Len = (nuint)len;
    }
}

/// <summary>
/// Mirror of Rust <c>FfiOwnedSlice&lt;T&gt;</c>: a <c>Vec&lt;T&gt;</c> whose ownership moved to .NET.
/// The layout does not depend on <c>T</c>, so one non-generic struct serves every element type.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct FfiOwnedSlice
{
    public void* Ptr;
    public nuint Len;
    public nuint Cap;
    public delegate* unmanaged[Cdecl]<void*, nuint, nuint, void> DropFn;

    /// <summary>Returns the buffer to Rust. Must be called at most once per slice.</summary>
    public readonly void Free()
    {
        // SAFETY: (Ptr, Len, Cap, DropFn) were produced together by Rust; a null
        // Ptr means "owns nothing" and is skipped.
        if (Ptr != null) DropFn(Ptr, Len, Cap);
    }
}

/// <summary>
/// Exposes a Rust-owned buffer as <see cref="Memory{T}"/> without copying.
/// Disposing returns the buffer to Rust; the memory must not be used afterwards.
/// There is no finalizer: an owner that is never disposed leaks its buffer.
/// </summary>
public sealed unsafe class RustMemoryManager<T> : MemoryManager<T> where T : unmanaged
{
    private readonly FfiOwnedSlice _slice;
    private int _disposed;

    public RustMemoryManager(FfiOwnedSlice slice) => _slice = slice;

    public override Span<T> GetSpan()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        // SAFETY: Rust guarantees Ptr is valid for Len elements of T until DropFn runs.
        return new Span<T>(_slice.Ptr, checked((int)_slice.Len));
    }

    // Native memory never moves, so pinning is free.
    public override MemoryHandle Pin(int elementIndex = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)elementIndex, _slice.Len);
        // SAFETY: bounds checked above; pointer stays valid until Dispose.
        return new MemoryHandle((T*)_slice.Ptr + elementIndex);
    }

    public override void Unpin() { }

    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _slice.Free();
    }

    // Deliberately no finalizer (CA2015): a Span<T> does not keep this object alive,
    // so finalizing could free memory still in use. A forgotten Dispose leaks instead.
}
