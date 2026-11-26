using System.Reflection;
using System.Runtime.InteropServices;

namespace Taskferry.Runtime;

/// <summary>
/// A loaded Rust cdylib plus the <c>taskferry-core</c> exports every such library contains.
/// Calls go through unmanaged function pointers: no marshalling stubs, NativeAOT-safe.
/// </summary>
public sealed unsafe class RustLibrary
{
    private readonly nint _handle;
    internal readonly delegate* unmanaged[Cdecl]<nint> CancelTokenNew;
    internal readonly delegate* unmanaged[Cdecl]<nint, void> CancelTokenCancel;
    internal readonly delegate* unmanaged[Cdecl]<nint, void> CancelTokenFree;
    internal readonly delegate* unmanaged[Cdecl]<nint, nint, delegate* unmanaged[Cdecl]<nint, void*, void>, void> StreamNext;
    internal readonly delegate* unmanaged[Cdecl]<nint, void> StreamCancel;
    internal readonly delegate* unmanaged[Cdecl]<nint, void> StreamFree;
    private readonly delegate* unmanaged[Cdecl]<long> _liveBuffers;

    public RustLibrary(nint handle)
    {
        _handle = handle;
        CancelTokenNew = (delegate* unmanaged[Cdecl]<nint>)GetExport("taskferry_cancel_token_new");
        CancelTokenCancel = (delegate* unmanaged[Cdecl]<nint, void>)GetExport("taskferry_cancel_token_cancel");
        CancelTokenFree = (delegate* unmanaged[Cdecl]<nint, void>)GetExport("taskferry_cancel_token_free");
        StreamNext = (delegate* unmanaged[Cdecl]<nint, nint, delegate* unmanaged[Cdecl]<nint, void*, void>, void>)GetExport("taskferry_stream_next");
        StreamCancel = (delegate* unmanaged[Cdecl]<nint, void>)GetExport("taskferry_stream_cancel");
        StreamFree = (delegate* unmanaged[Cdecl]<nint, void>)GetExport("taskferry_stream_free");
        _liveBuffers = (delegate* unmanaged[Cdecl]<long>)GetExport("taskferry_live_buffers");
    }

    /// <summary>Loads <paramref name="name"/> using the standard native-library probing for <paramref name="assembly"/>.</summary>
    public static RustLibrary Load(string name, Assembly assembly) => new(NativeLibrary.Load(name, assembly, null));

    public nint GetExport(string symbol) => NativeLibrary.GetExport(_handle, symbol);

    /// <summary>Rust buffers currently owned by .NET (leak diagnostics).</summary>
    public long LiveBuffers => _liveBuffers();
}

/// <summary>
/// A Rust <c>tokio_util::sync::CancellationToken</c> owned by .NET; cancelling it
/// aborts the Rust future it was passed to.
/// </summary>
public sealed unsafe class RustCancellationHandle : RustSafeHandle
{
    private readonly RustLibrary _library;

    public RustCancellationHandle(RustLibrary library) : base(library.CancelTokenNew()) => _library = library;

    /// <summary>Requests cancellation. Safe to race with <see cref="SafeHandle.Dispose()"/>.</summary>
    public void Cancel()
    {
        bool addedRef = false;
        try
        {
            DangerousAddRef(ref addedRef);
            // SAFETY: the ref taken above keeps the Rust token alive for this call.
            _library.CancelTokenCancel(handle);
        }
        catch (ObjectDisposedException)
        {
            // The call already completed and released the token: nothing to cancel.
        }
        finally
        {
            if (addedRef) DangerousRelease();
        }
    }

    protected override bool ReleaseHandle()
    {
        // SAFETY: runs exactly once, after every AddRef'd user has finished.
        _library.CancelTokenFree(handle);
        return true;
    }
}
