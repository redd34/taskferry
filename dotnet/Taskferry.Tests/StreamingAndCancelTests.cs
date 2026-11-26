using System.Diagnostics;
using Taskferry.Runtime;
using TaskferryExample;
using Xunit;

namespace Taskferry.Tests;

public class StreamingAndCancelTests
{
    [Fact]
    public async Task Stream_Yields10kItemsInOrder_ThenDropsRustStream()
    {
        using var ops = StressOps.Create();
        uint expected = 0;
        await foreach (uint item in ops.CountStream(10_000))
        {
            Assert.Equal(expected++, item);
        }
        Assert.Equal(10_000u, expected);
        Assert.Equal(1ul, ops.StreamDrops());
        Assert.Equal(0, RustTaskBridge.LiveHandles);
    }

    /// <summary>Phase 3 gate.</summary>
    [Fact]
    public async Task EarlyBreak_DropsRustStreamWithin50ms()
    {
        using var ops = StressOps.Create();
        var sinceBreak = new Stopwatch();
        await foreach (uint item in ops.CountStream(10_000))
        {
            if (item == 5)
            {
                sinceBreak.Start();
                break;
            }
        }
        ulong drops = ops.StreamDrops();
        sinceBreak.Stop();

        Assert.Equal(1ul, drops);
        Assert.True(sinceBreak.ElapsedMilliseconds < 50, $"drop took {sinceBreak.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task TokenizerStream_YieldsTokenIds()
    {
        using var engine = TokenizerEngine.Create(TestVocab.TokenizerJson);
        var ids = new List<uint>();
        await foreach (uint id in engine.StreamTokens("w0 w1 w999")) ids.Add(id);
        Assert.Equal(new uint[] { 1, 2, 1000 }, ids);
    }

    [Fact]
    public async Task StreamError_SurfacesAfterPrecedingItems()
    {
        using var ops = StressOps.Create();
        var items = new List<uint>();
        var error = await Assert.ThrowsAsync<RustFfiException>(async () =>
        {
            await foreach (uint item in ops.FailingStream()) items.Add(item);
        });
        Assert.Equal("stream failed", error.Message);
        Assert.Equal(new uint[] { 0, 1 }, items);
    }

    [Fact]
    public async Task StreamCancellation_ThrowsAndDropsRustStream()
    {
        using var ops = StressOps.Create();
        using var cts = new CancellationTokenSource();
        int seen = 0;
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            // One item per hour after the third: only cancellation can end the pending pull.
            await foreach (ulong tick in ops.TickStream(5, cts.Token))
            {
                if (++seen == 3) cts.Cancel();
            }
        });
        Assert.Equal(cts.Token, error.CancellationToken);
        Assert.Equal(3, seen);
        Assert.Equal(1ul, ops.StreamDrops());
        Assert.Equal(0, RustTaskBridge.LiveHandles);
    }

    [Fact]
    public async Task TaskCancellation_AbortsRustFutureImmediately()
    {
        using var ops = StressOps.Create();
        using var cts = new CancellationTokenSource();
        Task<ulong> task = ops.DelayAsync(60_000, cts.Token);
        await Task.Delay(50);
        Assert.False(task.IsCompleted);

        var elapsed = Stopwatch.StartNew();
        cts.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.True(elapsed.ElapsedMilliseconds < 2_000, $"cancellation took {elapsed.ElapsedMilliseconds}ms");
        Assert.Equal(cts.Token, error.CancellationToken);
        Assert.True(task.IsCanceled);
        // The Rust future was dropped mid-sleep (before the callback fired), not left running.
        Assert.Equal(1ul, ops.AbortedCount());
        Assert.Equal(0, RustTaskBridge.LiveHandles);
    }

    [Fact]
    public async Task PreCancelledToken_CancelsWithoutRunning()
    {
        using var ops = StressOps.Create();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ops.DelayAsync(60_000, new CancellationToken(canceled: true)));
    }

    [Fact]
    public async Task CancellableCall_ThatCompletes_ReturnsNormally()
    {
        using var ops = StressOps.Create();
        using var cts = new CancellationTokenSource();
        Assert.Equal(1ul, await ops.DelayAsync(1, cts.Token));
        Assert.Equal(9u, await ops.EchoAsync(9, cts.Token));
        cts.Cancel(); // after completion: must be a harmless no-op
        Assert.Equal(0ul, ops.AbortedCount());
    }
}
