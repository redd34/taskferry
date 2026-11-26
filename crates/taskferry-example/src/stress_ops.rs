//! Deliberate panics, delays, large buffers and cancellations for the .NET torture tests.

use std::sync::atomic::{AtomicU64, AtomicUsize, Ordering};
use std::sync::Arc;
use std::time::Duration;

use futures::{stream, Stream, StreamExt};
use taskferry_core::{RustError, RustResult};
use taskferry_macros::dotnet_export;

/// Increments `counter` when dropped while still armed.
struct DropCounter {
    counter: Arc<AtomicU64>,
    armed: bool,
}

impl Drop for DropCounter {
    fn drop(&mut self) {
        if self.armed {
            self.counter.fetch_add(1, Ordering::SeqCst);
        }
    }
}

/// Stress and diagnostics operations used by the verification suite.
#[dotnet_export]
#[derive(Default)]
pub struct StressOps {
    stream_drops: Arc<AtomicU64>,
    aborted: Arc<AtomicU64>,
    last_buffer: AtomicUsize,
}

impl StressOps {
    fn xor(&self, data: &[u8]) -> Vec<u8> {
        let out: Vec<u8> = data.iter().map(|b| b ^ 0xFF).collect();
        self.last_buffer.store(out.as_ptr() as usize, Ordering::SeqCst);
        out
    }

    fn drop_counter(&self) -> DropCounter {
        DropCounter { counter: self.stream_drops.clone(), armed: true }
    }
}

#[dotnet_export]
impl StressOps {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn add(&self, a: i32, b: i32) -> i32 {
        a.wrapping_add(b)
    }

    /// Regression: a parameter named like `SafeHandle.handle` must not shadow the object pointer.
    pub fn echo(&self, handle: i32) -> i32 {
        handle
    }

    /// Regression: a parameter named like the generated `cancellationToken` must not clash.
    pub async fn echo_async(&self, cancellation_token: u32) -> u32 {
        cancellation_token
    }

    pub fn negate(&self, value: bool) -> bool {
        !value
    }

    pub fn greet(&self, name: &str) -> String {
        format!("Hello, {name}!")
    }

    /// Always panics synchronously.
    pub fn panic_sync(&self) -> u32 {
        panic!("sync boom")
    }

    /// Panics mid-await on a Tokio worker.
    pub async fn panic_async(&self) -> RustResult<u32> {
        tokio::time::sleep(Duration::from_millis(5)).await;
        panic!("boom")
    }

    /// Fails with the given message.
    pub async fn fail_async(&self, message: String) -> RustResult<u32> {
        Err(RustError::new(message))
    }

    /// Sleeps for `millis`, then returns it. Cancellation is counted by `aborted_count`.
    pub async fn delay_async(&self, millis: u64) -> u64 {
        let mut aborted = DropCounter { counter: self.aborted.clone(), armed: true };
        tokio::time::sleep(Duration::from_millis(millis)).await;
        aborted.armed = false;
        millis
    }

    /// Number of `delay_async` futures dropped before completing.
    pub fn aborted_count(&self) -> u64 {
        self.aborted.load(Ordering::SeqCst)
    }

    /// Address Rust sees for `data` (zero-copy input verification).
    pub fn slice_addr(&self, data: &[u8]) -> usize {
        data.as_ptr() as usize
    }

    /// Returns `data` with every byte XOR 0xFF.
    pub fn transform(&self, data: &[u8]) -> Vec<u8> {
        self.xor(data)
    }

    /// Async variant of `transform`, executed on Tokio.
    pub async fn transform_async(&self, data: &[u8]) -> Vec<u8> {
        tokio::task::yield_now().await;
        self.xor(data)
    }

    /// Address of the buffer most recently returned by `transform*` (zero-copy output verification).
    pub fn last_buffer_addr(&self) -> usize {
        self.last_buffer.load(Ordering::SeqCst)
    }

    /// Yields `0..count`.
    pub fn count_stream(&self, count: u32) -> impl Stream<Item = RustResult<u32>> {
        let guard = self.drop_counter();
        stream::iter(0..count).map(move |i| {
            let _ = &guard;
            Ok(i)
        })
    }

    /// Infinite stream yielding one counter value every `interval_ms`.
    pub fn tick_stream(&self, interval_ms: u64) -> impl Stream<Item = RustResult<u64>> {
        let guard = self.drop_counter();
        stream::unfold(0u64, move |i| async move {
            tokio::time::sleep(Duration::from_millis(interval_ms)).await;
            Some((Ok(i), i + 1))
        })
        .map(move |item| {
            let _ = &guard;
            item
        })
    }

    /// Yields 0, 1 and then an error.
    pub fn failing_stream(&self) -> impl Stream<Item = RustResult<u32>> {
        stream::iter(vec![Ok(0), Ok(1), Err(RustError::new("stream failed"))])
    }

    /// Number of `count_stream` / `tick_stream` streams dropped so far.
    pub fn stream_drops(&self) -> u64 {
        self.stream_drops.load(Ordering::SeqCst)
    }
}
