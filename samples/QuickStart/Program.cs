// Calls the Rust `TokenizerEngine` from crates/taskferry-example (a Hugging Face tokenizer).
using System.Buffers;
using System.Text;
using TaskferryExample;

// A tiny word-level tokenizer definition, in the tokenizer.json format.
byte[] tokenizerJson = Encoding.UTF8.GetBytes("""
    {"version":"1.0","truncation":null,"padding":null,"added_tokens":[],"normalizer":null,
     "pre_tokenizer":{"type":"Whitespace"},"post_processor":null,"decoder":null,
     "model":{"type":"WordLevel","vocab":{"[UNK]":0,"hello":1,"async":2,"rust":3,"from":4,"dotnet":5},"unk_token":"[UNK]"}}
    """);

using var engine = TokenizerEngine.Create(tokenizerJson);

// async fn -> Task<IMemoryOwner<uint>>: runs on Tokio, the result is Rust's buffer (no copy).
byte[] text = Encoding.UTF8.GetBytes("hello async rust from dotnet");
using (IMemoryOwner<uint> ids = await engine.EncodeAsync(text))
{
    Console.WriteLine($"EncodeAsync:  [{string.Join(", ", ids.Memory.ToArray())}]");
}

// impl Stream -> IAsyncEnumerable<uint>: one item is pulled from Rust per iteration.
Console.Write("StreamTokens:");
await foreach (uint id in engine.StreamTokens("hello rust"))
{
    Console.Write($" {id}");
}
Console.WriteLine();

// Rust errors (and panics) arrive as exceptions.
try
{
    TokenizerEngine.Create("not a tokenizer"u8);
}
catch (Taskferry.Runtime.RustFfiException e)
{
    Console.WriteLine($"Rust error:   {e.Message}");
}
