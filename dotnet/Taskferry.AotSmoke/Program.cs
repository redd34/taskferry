// NativeAOT smoke test: exercises sync, async, zero-copy and streaming bindings.
using TaskferryExample;

using var ops = StressOps.Create();

if (ops.Add(40, 2) != 42) return Fail("sync call");
if (ops.Greet("AOT") != "Hello, AOT!") return Fail("string round-trip");

byte[] input = [1, 2, 3];
using (var owner = await ops.TransformAsync(input))
{
    if (!owner.Memory.Span.SequenceEqual<byte>([0xFE, 0xFD, 0xFC])) return Fail("async zero-copy");
}

uint sum = 0;
await foreach (uint i in ops.CountStream(100)) sum += i;
if (sum != 4950) return Fail("stream");

try
{
    await ops.PanicAsync();
    return Fail("panic was swallowed");
}
catch (Taskferry.Runtime.RustFfiException e) when (e.IsPanic && e.Message.Contains("boom"))
{
}

Console.WriteLine("AOT OK");
return 0;

static int Fail(string what)
{
    Console.Error.WriteLine($"AOT smoke test failed: {what}");
    return 1;
}
