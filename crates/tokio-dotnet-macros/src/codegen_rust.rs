//! Generates the `extern "C"` shims. ABI summary (mirrored by the C# generator):
//!
//! * `free_<Struct>(this)`
//! * sync / ctor / stream-open: `<Struct>_<fn>([this,] args.., out: *mut FfiOutcome<T>)`
//! * async: `<Struct>_<fn>([this,] args.., cancel, state, callback)`
//!
//! `this` is `Arc::into_raw(Arc<Struct>)`.

use proc_macro2::TokenStream;
use quote::{format_ident, quote};
use syn::Ident;

use crate::parser::{ImplExport, Kind, Method, Ty};

pub fn free_symbol(ty: &Ident) -> Ident {
    format_ident!("free_{}", ty)
}

pub fn method_symbol(ty: &Ident, method: &Ident) -> Ident {
    format_ident!("{}_{}", ty, method)
}

pub fn struct_shims(name: &Ident) -> TokenStream {
    let free = free_symbol(name);
    quote! {
        // Handles are shared across .NET threads and Tokio workers.
        const _: fn() = || {
            fn assert_shareable<T: ::core::marker::Send + ::core::marker::Sync + 'static>() {}
            assert_shareable::<#name>();
        };

        #[doc(hidden)]
        #[no_mangle]
        #[allow(non_snake_case, clippy::missing_safety_doc)]
        pub unsafe extern "C" fn #free(this: *const ::core::ffi::c_void) {
            // SAFETY: `this` came from `Arc::into_raw` in a constructor shim and
            // SafeHandle.ReleaseHandle releases .NET's single reference exactly once.
            // In-flight async calls hold their own clones.
            let this = unsafe { ::std::sync::Arc::from_raw(this as *const #name) };
            ::tokio_dotnet_core::panic::guarded_drop(move || drop(this));
        }
    }
}

pub fn impl_shims(export: &ImplExport) -> TokenStream {
    export.methods.iter().map(|m| method_shim(&export.name, m)).collect()
}

fn method_shim(ty: &Ident, m: &Method) -> TokenStream {
    let core = quote!(::tokio_dotnet_core);
    let void = quote!(::core::ffi::c_void);
    let symbol = method_symbol(ty, &m.name);
    let name = &m.name;

    // Per parameter: C-ABI declaration, a conversion that runs synchronously in
    // the shim (`pre`), and a fallible unwrap that runs inside the caught body (`inner`).
    //
    // SAFETY (all `as_slice` / `as_str` below): .NET keeps the memory pinned for
    // the whole synchronous call, and for borrowed async parameters until the
    // completion callback has run - which `Bridged` only fires after dropping the
    // future. Owned parameters (`String`, `Vec<T>`) are copied before the shim returns.
    let mut ffi_params = Vec::new();
    let mut pre = Vec::new();
    let mut inner = Vec::new();
    let mut args = Vec::new();
    for p in &m.params {
        let n = &p.name;
        match &p.ty {
            Ty::Prim(t) => {
                let t = format_ident!("{}", t);
                ffi_params.push(quote!(#n: #t));
            }
            Ty::Bool => {
                ffi_params.push(quote!(#n: u8));
                pre.push(quote!(let #n = #n != 0;));
            }
            Ty::Slice(t) => {
                let t = format_ident!("{}", t);
                ffi_params.push(quote!(#n: #core::FfiReadOnlySlice<#t>));
                pre.push(quote!(let #n = unsafe { #n.as_slice() };));
            }
            Ty::Vec(t) => {
                let t = format_ident!("{}", t);
                ffi_params.push(quote!(#n: #core::FfiReadOnlySlice<#t>));
                pre.push(quote!(let #n = unsafe { #n.as_slice() }.to_vec();));
            }
            Ty::Str => {
                ffi_params.push(quote!(#n: #core::FfiReadOnlySlice<u8>));
                pre.push(quote!(let #n = unsafe { #n.as_str() };));
                inner.push(quote!(let #n = #n?;));
            }
            Ty::String => {
                ffi_params.push(quote!(#n: #core::FfiReadOnlySlice<u8>));
                pre.push(quote!(let #n = unsafe { #n.as_str() }.map(::std::borrow::ToOwned::to_owned);));
                inner.push(quote!(let #n = #n?;));
            }
            Ty::Unit | Ty::Handle => unreachable!("rejected by the parser"),
        }
        args.push(quote!(#n));
    }

    let this_param = m.has_self.then(|| quote!(__this: *const #void,));
    let call = if m.has_self {
        quote!(#ty::#name(&*__this, #(#args),*))
    } else {
        quote!(#ty::#name(#(#args),*))
    };
    let call = if m.kind == Kind::Async { quote!(#call.await) } else { call };

    // `r_ty` is the Rust success type; its `IntoFfi::Ffi` is what crosses the boundary.
    let ret_syn = &m.ret_syn;
    let (r_ty, result) = match m.kind {
        Kind::Ctor => {
            let into_handle = quote!(::std::sync::Arc::into_raw(::std::sync::Arc::new(v)) as *const #void);
            let result = if m.fallible {
                quote!(#call.map_err(::core::convert::Into::into).map(|v| #into_handle))
            } else {
                quote!({ let v = #call; ::core::result::Result::Ok(#into_handle) })
            };
            (quote!(*const #void), result)
        }
        Kind::Stream => (
            quote!(*const #void),
            quote!(::core::result::Result::Ok(#core::stream_bridge::FfiStreamCursor::new(#call))),
        ),
        Kind::Sync | Kind::Async => {
            let result = if m.fallible {
                quote!(#call.map_err(::core::convert::Into::into))
            } else {
                quote!(::core::result::Result::Ok(#call))
            };
            (quote!(#ret_syn), result)
        }
    };
    let ffi_ret = quote!(<#r_ty as #core::IntoFfi>::Ffi);
    let body = quote! {
        #(#inner)*
        let __result: #core::RustResult<#r_ty> = #result;
        __result
    };
    let attrs = quote! {
        #[doc(hidden)]
        #[no_mangle]
        #[allow(non_snake_case, clippy::missing_safety_doc)]
    };

    if m.kind == Kind::Async {
        let this_clone = m.has_self.then(|| quote! {
            // SAFETY: .NET holds a SafeHandle reference for the duration of this
            // synchronous shim, so the Arc is alive; taking our own strong count
            // here keeps the state alive until the Tokio task finishes even if
            // .NET disposes the handle right after this call returns.
            let __this = unsafe {
                ::std::sync::Arc::increment_strong_count(__this as *const #ty);
                ::std::sync::Arc::from_raw(__this as *const #ty)
            };
        });
        quote! {
            #attrs
            pub unsafe extern "C" fn #symbol(
                #this_param
                #(#ffi_params,)*
                __cancel: *const #core::runtime::CancellationToken,
                __state: *mut #void,
                __callback: #core::CompletionCallback,
            ) {
                // SAFETY: .NET passes a fresh GCHandle and its static completion callback.
                let __guard = unsafe { #core::CompletionGuard::<#ffi_ret>::new(__state, __callback) };
                #this_clone
                #(#pre)*
                // SAFETY: `__cancel` is null or a token kept alive by .NET during this call.
                unsafe { #core::spawn_task::<#r_ty, _>(__guard, __cancel, async move { #body }) };
            }
        }
    } else {
        let this_borrow = m.has_self.then(|| quote! {
            // SAFETY: .NET holds a SafeHandle reference (DangerousAddRef) for the
            // duration of this synchronous call, so the Arc allocation is alive.
            // `borrow_handle` ties the borrow to `__scope`, so nothing returned from
            // the call (e.g. a stream) can keep borrowing the object.
            let __scope = ();
            let __this = unsafe { #core::memory::borrow_handle::<#ty>(&__scope, __this) };
        });
        quote! {
            #attrs
            pub unsafe extern "C" fn #symbol(
                #this_param
                #(#ffi_params,)*
                __out: *mut #core::FfiOutcome<#ffi_ret>,
            ) {
                let __outcome = #core::panic::catch_sync::<#r_ty, _>(|| {
                    #this_borrow
                    #(#pre)*
                    #body
                });
                // SAFETY: `__out` points to caller stack storage for one outcome.
                unsafe { __out.write(__outcome) };
            }
        }
    }
}
