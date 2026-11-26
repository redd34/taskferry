using System.Buffers;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using TokioDotnetExample;

namespace TokioDotnet.Tests;

/// <summary>
/// 1 MB buffer round-trips: tokio-dotnet zero-copy <see cref="Memory{T}"/> versus the
/// copy-in / copy-out semantics of classic <c>byte[]</c> P/Invoke marshalling.
/// </summary>
[MemoryDiagnoser]
[Config(typeof(InProcessConfig))]
public class FfiBenchmarks
{
    // In-process: the generated benchmark project would not have the native library next to it.
    private sealed class InProcessConfig : ManualConfig
    {
        public InProcessConfig() => AddJob(Job.ShortRun.WithToolchain(InProcessEmitToolchain.Instance));
    }

    private readonly byte[] _input = new byte[1 << 20];
    private StressOps _ops = null!;

    [GlobalSetup]
    public void Setup()
    {
        new Random(42).NextBytes(_input);
        _ops = StressOps.Create();
    }

    [GlobalCleanup]
    public void Cleanup() => _ops.Dispose();

    /// <summary>What a marshalled <c>byte[] Transform(byte[])</c> does: copy to native, call, copy back.</summary>
    [Benchmark(Baseline = true)]
    public unsafe byte[] MarshalledByteArray()
    {
        nint native = Marshal.AllocHGlobal(_input.Length);
        try
        {
            Marshal.Copy(_input, 0, native, _input.Length);
            using IMemoryOwner<byte> owner = _ops.Transform(new ReadOnlySpan<byte>((void*)native, _input.Length));
            return owner.Memory.ToArray();
        }
        finally
        {
            Marshal.FreeHGlobal(native);
        }
    }

    [Benchmark]
    public byte ZeroCopySync()
    {
        using IMemoryOwner<byte> owner = _ops.Transform(_input);
        return owner.Memory.Span[0];
    }

    [Benchmark]
    public async Task<byte> ZeroCopyAsync()
    {
        using IMemoryOwner<byte> owner = await _ops.TransformAsync(_input);
        return owner.Memory.Span[0];
    }
}
