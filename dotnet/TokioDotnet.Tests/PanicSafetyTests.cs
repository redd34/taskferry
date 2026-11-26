using TokioDotnet.Runtime;
using TokioDotnetExample;
using Xunit;

namespace TokioDotnet.Tests;

public class PanicSafetyTests
{
    [Fact]
    public async Task AsyncPanic_BecomesRustFfiException_AndRuntimeSurvives()
    {
        using var ops = StressOps.Create();
        var error = await Assert.ThrowsAsync<RustFfiException>(() => ops.PanicAsync());
        Assert.True(error.IsPanic);
        Assert.Contains("boom", error.Message);

        // The Tokio worker that panicked is still usable.
        Assert.Equal(7ul, await ops.DelayAsync(7));
        Assert.Equal(3, ops.Add(1, 2));
        Assert.Equal(0, RustTaskBridge.LiveHandles);
    }

    [Fact]
    public void SyncPanic_BecomesRustFfiException()
    {
        using var ops = StressOps.Create();
        var error = Assert.Throws<RustFfiException>(() => ops.PanicSync());
        Assert.True(error.IsPanic);
        Assert.Contains("sync boom", error.Message);
        Assert.Equal(3, ops.Add(1, 2));
    }

    [Fact]
    public async Task RustError_BecomesRustFfiException()
    {
        using var ops = StressOps.Create();
        long buffersBefore = NativeMethods.Library.LiveBuffers;
        var error = await Assert.ThrowsAsync<RustFfiException>(() => ops.FailAsync("nope ✗"));
        Assert.Equal(RustErrorCode.Error, error.Code);
        Assert.Equal("nope ✗", error.Message);
        Assert.Equal(buffersBefore, NativeMethods.Library.LiveBuffers); // message buffer was freed
    }

    [Fact]
    public void ConstructorError_Throws()
    {
        var error = Assert.Throws<RustFfiException>(() => TokenizerEngine.Create("not json"u8));
        Assert.Equal(RustErrorCode.Error, error.Code);
    }

    [Fact]
    public async Task DisposedHandle_ThrowsWithoutLeaking()
    {
        var ops = StressOps.Create();
        ops.Dispose();
        using var cts = new CancellationTokenSource();
        Assert.Throws<ObjectDisposedException>(() => ops.Add(1, 2));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => ops.TransformAsync(new byte[8], cts.Token));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
        {
            await foreach (uint _ in ops.CountStream(1)) { }
        });
        Assert.Equal(0, RustTaskBridge.LiveHandles);
    }
}
