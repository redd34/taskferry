using System.Runtime.InteropServices;
using System.Text;

namespace Taskferry.Runtime;

/// <summary>Mirror of the Rust <c>CODE_*</c> constants.</summary>
public enum RustErrorCode
{
    None = 0,
    Error = 1,
    Panic = 2,
    Cancelled = 3,
    EndOfStream = 4,
}

/// <summary>Mirror of Rust <c>FfiError</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct FfiError
{
    public int Code;
    public FfiOwnedSlice Message;

    /// <summary>Decodes and frees the message. Must be called at most once.</summary>
    public readonly Exception ToException(CancellationToken cancellationToken = default)
    {
        // SAFETY: Message is a Rust-owned UTF-8 buffer valid until Free().
        string message = Message.Ptr == null ? "" : Encoding.UTF8.GetString((byte*)Message.Ptr, checked((int)Message.Len));
        Message.Free();
        return Code == (int)RustErrorCode.Cancelled
            ? new OperationCanceledException(cancellationToken)
            : new RustFfiException(message, (RustErrorCode)Code);
    }
}

/// <summary>A Rust error or a caught Rust panic.</summary>
public sealed class RustFfiException : Exception
{
    public RustFfiException(string message, RustErrorCode code)
        : base(code == RustErrorCode.Panic ? $"Rust panic: {message}" : message)
    {
        Code = code;
    }

    public RustErrorCode Code { get; }

    public bool IsPanic => Code == RustErrorCode.Panic;
}
