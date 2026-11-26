using System.Buffers;
using System.Text;
using Taskferry.Runtime;
using TaskferryExample;
using Xunit;

namespace Taskferry.Tests;

/// <summary>A WordLevel tokenizer where word <c>w{i}</c> has id <c>i + 1</c> (0 is [UNK]).</summary>
internal static class TestVocab
{
    public const int Words = 1000;

    public static readonly byte[] TokenizerJson = Encoding.UTF8.GetBytes(
        """{"version":"1.0","truncation":null,"padding":null,"added_tokens":[],"normalizer":null,"pre_tokenizer":{"type":"Whitespace"},"post_processor":null,"decoder":null,"model":{"type":"WordLevel","vocab":{"[UNK]":0,"""
        + string.Join(",", Enumerable.Range(0, Words).Select(i => $"\"w{i}\":{i + 1}"))
        + """},"unk_token":"[UNK]"}}""");

    /// <summary>Random text plus the token ids it must encode to.</summary>
    public static (byte[] Text, uint[] Ids) RandomPayload(Random random)
    {
        int[] words = Enumerable.Range(0, random.Next(1, 64)).Select(_ => random.Next(Words)).ToArray();
        return (Encoding.UTF8.GetBytes(string.Join(' ', words.Select(w => $"w{w}"))), words.Select(w => (uint)w + 1).ToArray());
    }
}

public class ConcurrencyAndGcTests
{
    [Fact]
    public void SyncCalls_RoundTrip()
    {
        using var ops = StressOps.Create();
        Assert.Equal(5, ops.Add(2, 3));
        Assert.Equal(7, ops.Echo(7));
        Assert.True(ops.Negate(false));
        Assert.False(ops.Negate(true));
        Assert.Equal("Hello, wörld ✓!", ops.Greet("wörld ✓"));

        using var engine = TokenizerEngine.Create(TestVocab.TokenizerJson);
        using IMemoryOwner<uint> ids = engine.Encode("w0 w41 nope");
        Assert.Equal(new uint[] { 1, 42, 0 }, ids.Memory.ToArray());
        Assert.Equal("w0 w41", engine.Decode([1, 42]));
    }

    [Fact]
    public async Task GcCompactionTorture_10kConcurrentEncodes()
    {
        using var engine = TokenizerEngine.Create(TestVocab.TokenizerJson);
        long buffersBefore = NativeMethods.Library.LiveBuffers;
        var random = new Random(1234);

        using var stop = new CancellationTokenSource();
        var gcThread = new Thread(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
            }
        });
        gcThread.Start();

        try
        {
            var calls = new (Task<IMemoryOwner<uint>> Task, uint[] Expected)[10_000];
            for (int i = 0; i < calls.Length; i++)
            {
                // Fresh, unpinned heap arrays: the GC is free to move them unless the bridge pins correctly.
                (byte[] text, uint[] expected) = TestVocab.RandomPayload(random);
                calls[i] = (engine.EncodeAsync(text), expected);
            }

            foreach (var (task, expected) in calls)
            {
                using IMemoryOwner<uint> owner = await task;
                Assert.True(owner.Memory.Span.SequenceEqual(expected), "corrupted token ids");
            }
        }
        finally
        {
            stop.Cancel();
            gcThread.Join();
        }

        Assert.Equal(0, RustTaskBridge.LiveHandles);
        Assert.Equal(buffersBefore, NativeMethods.Library.LiveBuffers);
    }

    [Fact]
    public async Task MidFlightDispose_500TasksStillComplete()
    {
        var engine = TokenizerEngine.Create(TestVocab.TokenizerJson);
        var random = new Random(99);
        var calls = new (Task<IMemoryOwner<uint>> Task, uint[] Expected)[500];
        for (int i = 0; i < calls.Length; i++)
        {
            (byte[] text, uint[] expected) = TestVocab.RandomPayload(random);
            calls[i] = (engine.EncodeAsync(text), expected);
        }

        // Rust holds an Arc clone per in-flight call, so this must not free the engine under them.
        engine.Dispose();

        foreach (var (task, expected) in calls)
        {
            using IMemoryOwner<uint> owner = await task;
            Assert.True(owner.Memory.Span.SequenceEqual(expected));
        }
        Assert.Throws<ObjectDisposedException>(() => engine.Encode("w1"));
        Assert.Equal(0, RustTaskBridge.LiveHandles);
    }

    [Fact]
    public async Task Continuations_DoNotRunOnTokioWorkers()
    {
        using var ops = StressOps.Create();
        // ConfigureAwait(false): skip xUnit's SynchronizationContext so the continuation runs
        // wherever the TaskCompletionSource schedules it. An inlined continuation would run on
        // the Tokio worker that invoked the callback, which is not a ThreadPool thread.
#pragma warning disable xUnit1030 // deliberate: the test is about where the continuation runs
        await ops.DelayAsync(1).ConfigureAwait(false);
#pragma warning restore xUnit1030
        Assert.True(Thread.CurrentThread.IsThreadPoolThread);
    }
}
