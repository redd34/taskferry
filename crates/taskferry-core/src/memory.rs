//! `#[repr(C)]` types that cross the FFI boundary, and the Rust -> FFI conversions.

use std::ffi::c_void;
use std::mem::{ManuallyDrop, MaybeUninit};
use std::sync::atomic::{AtomicI64, Ordering};

/// Type-erased destructor carried by every [`FfiOwnedSlice`].
pub type DropCallback<T> = unsafe extern "C" fn(*mut T, usize, usize);

/// Number of Rust-owned buffers currently held by .NET (leak diagnostics).
static LIVE_BUFFERS: AtomicI64 = AtomicI64::new(0);

#[no_mangle]
pub extern "C" fn taskferry_live_buffers() -> i64 {
    LIVE_BUFFERS.load(Ordering::SeqCst)
}

/// Borrows the object behind an exported handle for no longer than `_scope`.
/// The explicit lifetime stops a shim from inferring `&'static T`, which would
/// let e.g. a returned stream keep borrowing the object after .NET frees it.
///
/// # Safety
/// `handle` must be `Arc::into_raw(Arc<T>)` and stay alive for `'a`.
#[allow(clippy::needless_lifetimes)] // explicit on purpose: the lifetime is the point
pub unsafe fn borrow_handle<'a, T>(_scope: &'a (), handle: *const c_void) -> &'a T {
    // SAFETY: caller contract.
    unsafe { &*(handle as *const T) }
}

// ---------------------------------------------------------------- errors

pub const CODE_ERROR: i32 = 1;
pub const CODE_PANIC: i32 = 2;
pub const CODE_CANCELLED: i32 = 3;
pub const CODE_END_OF_STREAM: i32 = 4;

/// Error type for exported functions. Deliberately does not implement
/// `Display`/`Error`, so the blanket `From<E: Display>` below lets `?` convert
/// any error (or `&str`/`String`) into it.
#[derive(Debug)]
pub struct RustError {
    pub code: i32,
    pub message: String,
}

impl RustError {
    pub fn new(message: impl Into<String>) -> Self {
        Self { code: CODE_ERROR, message: message.into() }
    }
    pub fn panic(message: impl Into<String>) -> Self {
        Self { code: CODE_PANIC, message: message.into() }
    }
    pub fn cancelled() -> Self {
        Self { code: CODE_CANCELLED, message: String::new() }
    }
    pub fn end_of_stream() -> Self {
        Self { code: CODE_END_OF_STREAM, message: String::new() }
    }
}

impl<E: std::fmt::Display> From<E> for RustError {
    fn from(e: E) -> Self {
        Self::new(e.to_string())
    }
}

pub type RustResult<T> = Result<T, RustError>;

// ---------------------------------------------------------------- slices

/// Borrowed view of .NET-pinned memory (C# -> Rust).
#[repr(C)]
pub struct FfiReadOnlySlice<T> {
    pub ptr: *const T,
    pub len: usize,
}

impl<T> Clone for FfiReadOnlySlice<T> {
    fn clone(&self) -> Self {
        *self
    }
}
impl<T> Copy for FfiReadOnlySlice<T> {}

impl<T> FfiReadOnlySlice<T> {
    /// # Safety
    /// `ptr` must point to `len` initialized, aligned `T`s that stay valid and
    /// unmodified for the caller-chosen lifetime `'a` (the .NET side keeps the
    /// memory pinned until the call or its completion callback finishes).
    pub unsafe fn as_slice<'a>(self) -> &'a [T] {
        if self.len == 0 {
            // .NET hands out a null pointer for empty spans.
            &[]
        } else {
            // SAFETY: guaranteed by the caller contract above.
            unsafe { std::slice::from_raw_parts(self.ptr, self.len) }
        }
    }
}

impl FfiReadOnlySlice<u8> {
    /// # Safety
    /// Same contract as [`FfiReadOnlySlice::as_slice`].
    pub unsafe fn as_str<'a>(self) -> RustResult<&'a str> {
        // SAFETY: forwarded caller contract.
        Ok(std::str::from_utf8(unsafe { self.as_slice() })?)
    }
}

/// Name used by the public API docs for a borrowed input slice.
pub type FfiSlice<T> = FfiReadOnlySlice<T>;

/// A `Vec<T>` whose ownership moved to .NET (Rust -> C#). .NET must call
/// `drop_fn(ptr, len, cap)` exactly once.
#[repr(C)]
pub struct FfiOwnedSlice<T> {
    pub ptr: *mut T,
    pub len: usize,
    pub cap: usize,
    pub drop_fn: DropCallback<T>,
}

pub type FfiBytes = FfiOwnedSlice<u8>;
pub type FfiString = FfiOwnedSlice<u8>;

impl<T> FfiOwnedSlice<T> {
    /// Null slice that owns nothing; `drop_fn` is a no-op for it.
    pub fn empty() -> Self {
        Self { ptr: std::ptr::null_mut(), len: 0, cap: 0, drop_fn: drop_vec::<T> }
    }
}

impl<T> From<Vec<T>> for FfiOwnedSlice<T> {
    fn from(v: Vec<T>) -> Self {
        let mut v = ManuallyDrop::new(v);
        LIVE_BUFFERS.fetch_add(1, Ordering::SeqCst);
        Self { ptr: v.as_mut_ptr(), len: v.len(), cap: v.capacity(), drop_fn: drop_vec::<T> }
    }
}

unsafe extern "C" fn drop_vec<T>(ptr: *mut T, len: usize, cap: usize) {
    if ptr.is_null() {
        return;
    }
    LIVE_BUFFERS.fetch_sub(1, Ordering::SeqCst);
    // SAFETY: (ptr, len, cap) came from `Vec::<T>` in `From<Vec<T>>` above and
    // the .NET side calls this exactly once (guarded by Interlocked.Exchange).
    drop(unsafe { Vec::from_raw_parts(ptr, len, cap) });
}

// ---------------------------------------------------------------- outcome

#[repr(C)]
pub struct FfiError {
    /// 0 = none, otherwise one of the `CODE_*` constants.
    pub code: i32,
    /// UTF-8 message, owned by the receiver.
    pub message: FfiString,
}

impl FfiError {
    pub fn none() -> Self {
        Self { code: 0, message: FfiOwnedSlice::empty() }
    }
}

impl From<RustError> for FfiError {
    fn from(e: RustError) -> Self {
        let message = if e.message.is_empty() {
            FfiOwnedSlice::empty()
        } else {
            e.message.into_bytes().into()
        };
        Self { code: e.code, message }
    }
}

/// Tagged union for `Result<T, E>`. `value` is only initialized when `is_ok == 1`
/// (zeroed otherwise); `err` is only meaningful when `is_ok == 0`.
#[repr(C)]
pub struct FfiOutcome<T> {
    pub is_ok: u8,
    pub value: MaybeUninit<T>,
    pub err: FfiError,
}

impl<T> FfiOutcome<T> {
    pub fn ok(value: T) -> Self {
        Self { is_ok: 1, value: MaybeUninit::new(value), err: FfiError::none() }
    }
    pub fn err(e: RustError) -> Self {
        Self { is_ok: 0, value: MaybeUninit::zeroed(), err: e.into() }
    }
    pub fn cancelled() -> Self {
        Self::err(RustError::cancelled())
    }
    pub fn from_result<R: IntoFfi<Ffi = T>>(r: RustResult<R>) -> Self {
        match r {
            Ok(v) => Self::ok(v.into_ffi()),
            Err(e) => Self::err(e),
        }
    }
}

// ---------------------------------------------------------------- conversions

/// Rust return type -> its C-ABI representation.
pub trait IntoFfi {
    type Ffi;
    fn into_ffi(self) -> Self::Ffi;
}

/// Element types allowed inside slices crossing the boundary.
pub trait FfiPrimitive: Copy + Send + 'static {}

macro_rules! primitives {
    ($($t:ty),*) => {$(
        impl FfiPrimitive for $t {}
        impl IntoFfi for $t {
            type Ffi = $t;
            fn into_ffi(self) -> $t { self }
        }
    )*};
}
primitives!(u8, i8, u16, i16, u32, i32, u64, i64, usize, isize, f32, f64);

impl IntoFfi for bool {
    type Ffi = u8;
    fn into_ffi(self) -> u8 {
        self as u8
    }
}

impl IntoFfi for () {
    type Ffi = u8;
    fn into_ffi(self) -> u8 {
        0
    }
}

impl IntoFfi for String {
    type Ffi = FfiString;
    fn into_ffi(self) -> FfiString {
        self.into_bytes().into()
    }
}

impl<T: FfiPrimitive> IntoFfi for Vec<T> {
    type Ffi = FfiOwnedSlice<T>;
    fn into_ffi(self) -> FfiOwnedSlice<T> {
        self.into()
    }
}

/// Opaque handles (`Arc<T>` raw pointers, stream cursors).
impl IntoFfi for *const c_void {
    type Ffi = *const c_void;
    fn into_ffi(self) -> *const c_void {
        self
    }
}

#[cfg(all(test, target_pointer_width = "64"))]
mod tests {
    use super::*;
    use std::mem::{offset_of, size_of};

    /// Must match `LayoutParity` in dotnet/Taskferry.Tests/ZeroCopyMemoryTests.cs.
    #[test]
    fn layout_matches_csharp() {
        assert_eq!(size_of::<FfiReadOnlySlice<u8>>(), 16);
        assert_eq!(size_of::<FfiOwnedSlice<u32>>(), 32);
        assert_eq!(size_of::<FfiError>(), 40);
        assert_eq!(offset_of!(FfiError, message), 8);
        assert_eq!(size_of::<FfiOutcome<u8>>(), 48);
        assert_eq!(offset_of!(FfiOutcome<u8>, value), 1);
        assert_eq!(offset_of!(FfiOutcome<u8>, err), 8);
        assert_eq!(size_of::<FfiOutcome<FfiOwnedSlice<u8>>>(), 80);
        assert_eq!(offset_of!(FfiOutcome<FfiOwnedSlice<u8>>, value), 8);
        assert_eq!(offset_of!(FfiOutcome<FfiOwnedSlice<u8>>, err), 40);
    }
}
