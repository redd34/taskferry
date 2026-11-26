using System.Buffers;
using TokioDotnet.Runtime;
using TokioDotnetExample;
using Xunit;

namespace TokioDotnet.Tests;

public class ZeroCopyMemoryTests
{
    /// <summary>Must match <c>layout_matches_csharp</c> in tokio-dotnet-core/src/memory.rs.</summary>
    [Fact]
    public unsafe void LayoutParity()
    {
        Assert.Equal(8, sizeof(nint));
        Assert.Equal(16, sizeof(FfiReadOnlySlice));
        Assert.Equal(32, sizeof(FfiOwnedSlice));
        Assert.Equal(40, sizeof(FfiError));
        Assert.Equal(48, sizeof(FfiOutcome<byte>));
        Assert.Equal(80, sizeof(FfiOutcome<FfiOwnedSlice>));

        FfiOutcome<byte> small = default;
        Assert.Equal(1, (byte*)&small.Value - (byte*)&small);
        Assert.Equal(8, (byte*)&small.Err - (byte*)&small);
        Assert.Equal(8, (byte*)&small.Err.Message - (byte*)&small.Err);
        FfiOutcome<FfiOwnedSlice> large = default;
        Assert.Equal(8, (byte*)&large.Value - (byte*)&large);
        Assert.Equal(40, (byte*)&large.Err - (byte*)&large);
    }

    /// <summary>Phase 1 gate: a hand-wired export driven directly through the runtime, no generated code.</summary>
    [Fact]
    public async Task HandWired_AsyncTransform_ReturnsZeroCopyMemoryOwner()
    {
        byte[] input = [0x00, 0x0F, 0xF0, 0xFF];
        using IMemoryOwner<byte> owner = await Start(input);
        Assert.Equal(new byte[] { 0xFF, 0xF0, 0x0F, 0x00 }, owner.Memory.ToArray());

        static unsafe Task<IMemoryOwner<byte>> Start(byte[] input)
        {
            var context = new RustTaskContext<FfiOwnedSlice, IMemoryOwner<byte>, OwnedSliceConverter<byte>>(NativeMethods.Library, default);
            var transform = (delegate* unmanaged[Cdecl]<FfiReadOnlySlice, nint, nint, delegate* unmanaged[Cdecl]<nint, void*, void>, void>)
                NativeMethods.Library.GetExport("tokio_dotnet_example_transform_async");
            // SAFETY: the context keeps `input` pinned until the completion callback.
            transform(context.Pin<byte>(input), context.CancelToken, context.AllocState(), RustTaskBridge.Callback);
            return context.Task;
        }
    }

    [Fact]
    public unsafe void Input_IsPassedWithoutCopy()
    {
        using var ops = StressOps.Create();
        byte[] data = new byte[1024];
        fixed (byte* p = data)
        {
            Assert.Equal((nuint)p, ops.SliceAddr(data));
        }
    }

    [Fact]
    public async Task Output_IsTheRustBuffer_AndDisposeFreesIt()
    {
        using var ops = StressOps.Create();
        byte[] data = new byte[1 << 20];
        Random.Shared.NextBytes(data);
        long before = NativeMethods.Library.LiveBuffers;

        IMemoryOwner<byte> sync = ops.Transform(data);
        Assert.Equal(ops.LastBufferAddr(), AddressOf(sync));
        IMemoryOwner<byte> async = await ops.TransformAsync(data);
        Assert.Equal(ops.LastBufferAddr(), AddressOf(async));
        Assert.Equal(before + 2, NativeMethods.Library.LiveBuffers);

        for (int i = 0; i < data.Length; i++) Assert.Equal((byte)(data[i] ^ 0xFF), sync.Memory.Span[i]);
        Assert.True(sync.Memory.Span.SequenceEqual(async.Memory.Span));

        sync.Dispose();
        Assert.Equal(before + 1, NativeMethods.Library.LiveBuffers);
        sync.Dispose(); // idempotent: must not double-free
        async.Dispose();
        Assert.Equal(before, NativeMethods.Library.LiveBuffers);
        Assert.Throws<ObjectDisposedException>(() => sync.Memory.Span.Length);
    }

    [Fact]
    public void EmptyBuffers_RoundTrip()
    {
        using var ops = StressOps.Create();
        using IMemoryOwner<byte> owner = ops.Transform(ReadOnlySpan<byte>.Empty);
        Assert.Equal(0, owner.Memory.Length);
        Assert.Equal("Hello, !", ops.Greet(""));
    }

    private static unsafe nuint AddressOf(IMemoryOwner<byte> owner)
    {
        fixed (byte* p = owner.Memory.Span) return (nuint)p;
    }
}
