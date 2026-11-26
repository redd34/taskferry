using Microsoft.Win32.SafeHandles;

namespace Taskferry.Runtime;

/// <summary>
/// Base class for handles to Rust-owned objects. The ref-counting of
/// <see cref="System.Runtime.InteropServices.SafeHandle"/> guarantees the Rust
/// object is not freed while a native call using it is in progress.
/// </summary>
public abstract class RustSafeHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    protected RustSafeHandle(nint handle) : base(ownsHandle: true) => SetHandle(handle);
}
