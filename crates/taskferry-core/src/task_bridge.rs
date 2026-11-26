//! Bridges a Rust future to a .NET `TaskCompletionSource` via a single-fire callback.

use std::ffi::c_void;
use std::future::Future;
use std::marker::PhantomData;
use std::pin::Pin;
use std::sync::atomic::{AtomicBool, Ordering};
use std::task::{Context, Poll};

use crate::memory::{FfiOutcome, IntoFfi, RustError, RustResult};
use crate::panic::catch_async;
use crate::runtime::{runtime, CancellationToken};

/// `.NET [UnmanagedCallersOnly]` callback: `(GCHandle state, FfiOutcome<T>* outcome)`.
/// The outcome is type-erased so one non-generic C# callback serves every `T`;
/// the pointee is only valid for the duration of the call.
pub type CompletionCallback = unsafe extern "C" fn(state: *mut c_void, outcome: *const c_void);

/// Guarantees the .NET callback fires exactly once: on `complete`, or with a
/// cancelled outcome if dropped first (task abort, runtime shutdown), so .NET
/// never leaks its `GCHandle` or hangs a `Task`.
pub struct CompletionGuard<T> {
    state: *mut c_void,
    callback: CompletionCallback,
    completed: AtomicBool,
    _outcome: PhantomData<fn(T)>,
}

// SAFETY: `state` is an opaque GCHandle value (not dereferenced by Rust) and the
// callback is a thread-agnostic static function; .NET allows both on any thread.
unsafe impl<T> Send for CompletionGuard<T> {}

impl<T> CompletionGuard<T> {
    /// # Safety
    /// `callback(state, &FfiOutcome<T>)` must be sound to call once from any thread.
    pub unsafe fn new(state: *mut c_void, callback: CompletionCallback) -> Self {
        Self { state, callback, completed: AtomicBool::new(false), _outcome: PhantomData }
    }

    pub fn complete(self, outcome: FfiOutcome<T>) {
        self.fire(outcome);
    }

    fn fire(&self, outcome: FfiOutcome<T>) {
        if !self.completed.swap(true, Ordering::SeqCst) {
            // SAFETY: contract of `new`; the swap above makes this single-fire.
            unsafe { (self.callback)(self.state, &outcome as *const FfiOutcome<T> as *const c_void) }
        }
    }
}

impl<T> Drop for CompletionGuard<T> {
    fn drop(&mut self) {
        if !self.completed.load(Ordering::SeqCst) {
            self.fire(FfiOutcome::cancelled());
        }
    }
}

/// Future + guard with a fixed drop order: the future (which may borrow
/// .NET-pinned memory) is always dropped *before* the callback unpins it.
struct Bridged<R: IntoFfi, F> {
    fut: Option<Pin<Box<F>>>,
    guard: Option<CompletionGuard<R::Ffi>>,
}

impl<R: IntoFfi, F: Future<Output = RustResult<R>>> Future for Bridged<R, F> {
    type Output = ();

    fn poll(mut self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<()> {
        let this = &mut *self;
        let Some(fut) = this.fut.as_mut() else { return Poll::Ready(()) };
        let result = match fut.as_mut().poll(cx) {
            Poll::Pending => return Poll::Pending,
            Poll::Ready(r) => r,
        };
        this.fut = None;
        if let Some(guard) = this.guard.take() {
            guard.complete(FfiOutcome::from_result(result));
        }
        Poll::Ready(())
    }
}

/// Spawns `fut` on the global runtime and resolves `guard` with its result.
/// Panics become `RustError::panic`; cancelling `cancel` drops the future
/// immediately and resolves with a cancelled outcome.
///
/// # Safety
/// `cancel` must be null or a live token pointer for the duration of this call
/// (it is cloned synchronously, not retained).
pub unsafe fn spawn_task<R, F>(guard: CompletionGuard<R::Ffi>, cancel: *const CancellationToken, fut: F)
where
    R: IntoFfi + Send + 'static,
    F: Future<Output = RustResult<R>> + Send + 'static,
{
    // SAFETY: caller contract.
    let cancel = unsafe { cancel.as_ref() }.cloned();
    let fut = async move {
        let work = catch_async(fut);
        match cancel {
            Some(token) => tokio::select! {
                biased;
                _ = token.cancelled() => Err(RustError::cancelled()),
                r = work => r,
            },
            None => work.await,
        }
    };
    runtime().spawn(Bridged::<R, _> { fut: Some(Box::pin(fut)), guard: Some(guard) });
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::mpsc;

    unsafe extern "C" fn record(state: *mut c_void, outcome: *const c_void) {
        // SAFETY: test passes a `Sender` pointer and an `FfiOutcome<u32>`.
        let (tx, o) = unsafe { (&*(state as *const mpsc::Sender<(u8, i32, u32)>), &*(outcome as *const FfiOutcome<u32>)) };
        tx.send((o.is_ok, o.err.code, unsafe { o.value.assume_init() })).unwrap();
    }

    fn guard(tx: &mpsc::Sender<(u8, i32, u32)>) -> CompletionGuard<u32> {
        unsafe { CompletionGuard::new(tx as *const _ as *mut c_void, record) }
    }

    #[test]
    fn dropped_guard_fires_cancelled_once() {
        let (tx, rx) = mpsc::channel();
        drop(guard(&tx));
        assert_eq!(rx.try_recv().unwrap(), (0, crate::memory::CODE_CANCELLED, 0));
        assert!(rx.try_recv().is_err());
    }

    #[test]
    fn spawn_resolves_ok_panic_and_cancel() {
        let (tx, rx) = mpsc::channel();
        unsafe { spawn_task::<u32, _>(guard(&tx), std::ptr::null(), async { Ok(7) }) };
        assert_eq!(rx.recv().unwrap(), (1, 0, 7));

        unsafe { spawn_task::<u32, _>(guard(&tx), std::ptr::null(), async { panic!("boom") }) };
        assert_eq!(rx.recv().unwrap(), (0, crate::memory::CODE_PANIC, 0));

        let token = CancellationToken::new();
        unsafe { spawn_task::<u32, _>(guard(&tx), &token, std::future::pending()) };
        token.cancel();
        assert_eq!(rx.recv().unwrap(), (0, crate::memory::CODE_CANCELLED, 0));
        assert!(rx.try_recv().is_err());
    }
}
