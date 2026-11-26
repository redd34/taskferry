//! Pull-based stream cursor backing `IAsyncEnumerable<T>`: .NET asks for one
//! item at a time, so a slow consumer applies backpressure for free.
//!
//! Each pull resolves an `FfiOutcome<T>`: ok = item, `CODE_END_OF_STREAM` = done,
//! `CODE_CANCELLED` = cancelled, anything else = stream error.

use std::ffi::c_void;
use std::pin::Pin;
use std::sync::Arc;

use futures::{Stream, StreamExt};
use tokio::sync::Mutex;

use crate::panic::guarded_drop;
use crate::memory::{IntoFfi, RustError, RustResult, CODE_CANCELLED};
use crate::runtime::CancellationToken;
use crate::task_bridge::{spawn_task, CompletionCallback, CompletionGuard};

type BoxStream<T> = Pin<Box<dyn Stream<Item = RustResult<T>> + Send>>;

/// Item-type-erased cursor operations, so the exported functions are not generic.
trait Cursor: Send + Sync {
    fn spawn_next(self: Arc<Self>, state: *mut c_void, callback: CompletionCallback);
    fn cancel(&self);
}

struct TypedCursor<T> {
    /// `None` once finished, cancelled or freed (the stream is dropped eagerly).
    stream: Mutex<Option<BoxStream<T>>>,
    token: CancellationToken,
}

impl<T: IntoFfi + Send + 'static> Cursor for TypedCursor<T> {
    fn spawn_next(self: Arc<Self>, state: *mut c_void, callback: CompletionCallback) {
        // SAFETY: .NET passes a fresh GCHandle and its static callback per pull.
        let guard = unsafe { CompletionGuard::<T::Ffi>::new(state, callback) };
        let work = async move {
            let mut slot = self.stream.lock().await;
            let item = match slot.as_mut() {
                // Dropped by `cancel` (not by reaching the end): report cancellation.
                None if self.token.is_cancelled() => Some(Err(RustError::cancelled())),
                None => None,
                Some(stream) => tokio::select! {
                    biased;
                    _ = self.token.cancelled() => Some(Err(RustError::cancelled())),
                    item = stream.next() => item,
                },
            };
            match item {
                Some(Ok(value)) => Ok(value),
                Some(Err(e)) => {
                    if e.code == CODE_CANCELLED {
                        *slot = None;
                    }
                    Err(e)
                }
                None => {
                    *slot = None;
                    Err(RustError::end_of_stream())
                }
            }
        };
        // SAFETY: null token; cancellation is handled by the cursor's own token above.
        unsafe { spawn_task::<T, _>(guard, std::ptr::null(), work) };
    }

    fn cancel(&self) {
        self.token.cancel();
        // No pull in flight: drop the stream right now. Otherwise the in-flight
        // pull observes the token and drops it.
        if let Ok(mut slot) = self.stream.try_lock() {
            *slot = None;
        }
    }
}

/// Opaque handle given to .NET (thin pointer to a fat `Arc<dyn Cursor>`).
pub struct FfiStreamCursor(Arc<dyn Cursor>);

impl FfiStreamCursor {
    /// Boxes `stream` behind an opaque pointer; release with `taskferry_stream_free`.
    // Returns an untyped pointer on purpose: it is an FFI handle value.
    #[allow(clippy::new_ret_no_self)]
    pub fn new<T, E, S>(stream: S) -> *const c_void
    where
        T: IntoFfi + Send + 'static,
        E: Into<RustError>,
        S: Stream<Item = Result<T, E>> + Send + 'static,
    {
        let stream: BoxStream<T> = Box::pin(stream.map(|r| r.map_err(Into::into)));
        let cursor = TypedCursor { stream: Mutex::new(Some(stream)), token: CancellationToken::new() };
        Box::into_raw(Box::new(FfiStreamCursor(Arc::new(cursor)))) as *const c_void
    }
}

/// Pulls the next item; `callback(state, &FfiOutcome<T>)` fires exactly once.
///
/// # Safety
/// `cursor` must be a live pointer from [`FfiStreamCursor::new`].
#[no_mangle]
pub unsafe extern "C" fn taskferry_stream_next(
    cursor: *const FfiStreamCursor,
    state: *mut c_void,
    callback: CompletionCallback,
) {
    // SAFETY: the .NET SafeHandle ref-count keeps the cursor alive during this call;
    // the spawned task holds its own Arc clone afterwards.
    unsafe { &*cursor }.0.clone().spawn_next(state, callback);
}

/// # Safety
/// `cursor` must be a live pointer from [`FfiStreamCursor::new`].
#[no_mangle]
pub unsafe extern "C" fn taskferry_stream_cancel(cursor: *const FfiStreamCursor) {
    // SAFETY: as in `taskferry_stream_next`.
    let cursor = unsafe { &*cursor };
    guarded_drop(|| cursor.0.cancel());
}

/// Drops the stream immediately (aborting upstream work) and frees the cursor.
///
/// # Safety
/// `cursor` must come from [`FfiStreamCursor::new`] and not be used again.
#[no_mangle]
pub unsafe extern "C" fn taskferry_stream_free(cursor: *const FfiStreamCursor) {
    // SAFETY: SafeHandle.ReleaseHandle runs exactly once, after all other users.
    let cursor = unsafe { Box::from_raw(cursor as *mut FfiStreamCursor) };
    guarded_drop(move || {
        cursor.0.cancel();
        drop(cursor);
    });
}
