//! Real-world showcase: Hugging Face `tokenizers` exposed to .NET.

use futures::{stream, Stream};
use tokenizers::Tokenizer;
use tokio_dotnet_core::RustResult;
use tokio_dotnet_macros::dotnet_export;

/// A Hugging Face tokenizer owned on the Rust heap.
#[dotnet_export]
pub struct TokenizerEngine {
    tokenizer: Tokenizer,
}

impl TokenizerEngine {
    fn ids(&self, text: &str) -> RustResult<Vec<u32>> {
        Ok(self.tokenizer.encode(text, false)?.get_ids().to_vec())
    }
}

#[dotnet_export]
impl TokenizerEngine {
    /// Loads a tokenizer from the contents of a `tokenizer.json` file.
    pub fn new(vocab_json: &[u8]) -> RustResult<Self> {
        Ok(Self { tokenizer: Tokenizer::from_bytes(vocab_json)? })
    }

    /// Encodes UTF-8 `text` on Tokio; never blocks a .NET ThreadPool thread.
    /// The input is pinned zero-copy and the ids are returned zero-copy.
    pub async fn encode_async(&self, text: &[u8]) -> RustResult<Vec<u32>> {
        self.ids(std::str::from_utf8(text)?)
    }

    /// Synchronous encode.
    pub fn encode(&self, text: &str) -> RustResult<Vec<u32>> {
        self.ids(text)
    }

    /// Decodes token ids back to text.
    pub fn decode(&self, ids: &[u32]) -> RustResult<String> {
        Ok(self.tokenizer.decode(ids, true)?)
    }

    /// Streams the token ids of `prompt` one at a time, with backpressure.
    pub fn stream_tokens(&self, prompt: String) -> impl Stream<Item = RustResult<u32>> {
        stream::iter(match self.ids(&prompt) {
            Ok(ids) => ids.into_iter().map(Ok).collect(),
            Err(e) => vec![Err(e)],
        })
    }
}
