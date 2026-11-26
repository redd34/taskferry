//! Parses `#[dotnet_export]` items into a small, purely syntactic model.

use syn::spanned::Spanned;
use syn::{
    Attribute, Error, Expr, ExprLit, FnArg, GenericArgument, Ident, ImplItem, ImplItemFn, ItemImpl,
    ItemStruct, Lit, Meta, Pat, PathArguments, PathSegment, Result, ReturnType, Type,
    TypeParamBound, Visibility,
};

const PRIMITIVES: &[&str] =
    &["u8", "i8", "u16", "i16", "u32", "i32", "u64", "i64", "usize", "isize", "f32", "f64"];

#[derive(Clone, PartialEq)]
pub enum Ty {
    Prim(String),
    Bool,
    Unit,
    /// `&str` (borrowed from .NET-pinned UTF-8)
    Str,
    String,
    /// `&[T]` (borrowed from .NET-pinned memory)
    Slice(String),
    Vec(String),
    Handle,
}

impl Ty {
    /// Type name used in the JSON schema.
    pub fn schema(&self) -> String {
        match self {
            Ty::Prim(p) => p.clone(),
            Ty::Bool => "bool".into(),
            Ty::Unit => "unit".into(),
            Ty::Str => "str".into(),
            Ty::String => "string".into(),
            Ty::Slice(p) => format!("slice<{p}>"),
            Ty::Vec(p) => format!("vec<{p}>"),
            Ty::Handle => "self".into(),
        }
    }
}

#[derive(Clone, Copy, PartialEq)]
pub enum Kind {
    Ctor,
    Sync,
    Async,
    Stream,
}

pub struct Param {
    pub name: Ident,
    pub ty: Ty,
}

pub struct Method {
    pub name: Ident,
    pub kind: Kind,
    pub has_self: bool,
    pub params: Vec<Param>,
    pub ret: Ty,
    /// The success type as written (inner `T` of `RustResult<T>` / stream item).
    pub ret_syn: Type,
    /// Whether the method (or stream item) is a `Result`.
    pub fallible: bool,
    pub doc: String,
}

pub struct StructExport {
    pub name: Ident,
    pub doc: String,
}

pub struct ImplExport {
    pub name: Ident,
    pub methods: Vec<Method>,
}

pub fn parse_struct(item: &ItemStruct) -> Result<StructExport> {
    if !item.generics.params.is_empty() {
        return Err(Error::new_spanned(&item.generics, "#[dotnet_export] structs cannot be generic"));
    }
    Ok(StructExport { name: item.ident.clone(), doc: doc(&item.attrs) })
}

pub fn parse_impl(item: &ItemImpl) -> Result<ImplExport> {
    if item.trait_.is_some() || !item.generics.params.is_empty() {
        return Err(Error::new_spanned(item, "#[dotnet_export] needs a non-generic inherent impl"));
    }
    let name = match last_segment(&item.self_ty) {
        Some(seg) if seg.arguments.is_none() => seg.ident.clone(),
        _ => return Err(Error::new_spanned(&item.self_ty, "unsupported impl target")),
    };
    let mut methods = Vec::new();
    for it in &item.items {
        if let ImplItem::Fn(f) = it {
            if matches!(f.vis, Visibility::Public(_)) {
                methods.push(parse_method(f, &name)?);
            }
        }
    }
    Ok(ImplExport { name, methods })
}

fn parse_method(f: &ImplItemFn, self_name: &Ident) -> Result<Method> {
    let sig = &f.sig;
    if !sig.generics.params.is_empty() {
        return Err(Error::new_spanned(&sig.generics, "exported methods cannot be generic"));
    }

    let mut has_self = false;
    let mut params = Vec::new();
    for arg in &sig.inputs {
        match arg {
            FnArg::Receiver(r) => {
                if r.reference.is_none() || r.mutability.is_some() {
                    return Err(Error::new_spanned(
                        r,
                        "exported methods must take `&self` (state is shared through an Arc; use interior mutability)",
                    ));
                }
                has_self = true;
            }
            FnArg::Typed(p) => {
                let Pat::Ident(id) = &*p.pat else {
                    return Err(Error::new_spanned(&p.pat, "parameters must be plain identifiers"));
                };
                if id.ident.to_string().starts_with("__") {
                    return Err(Error::new_spanned(&id.ident, "parameter names starting with `__` are reserved for the generated shim"));
                }
                params.push(Param { name: id.ident.clone(), ty: param_ty(&p.ty)? });
            }
        }
    }

    let (mut kind, out) = match &sig.output {
        ReturnType::Default => (Kind::Sync, None),
        ReturnType::Type(_, t) => match stream_item(t) {
            Some(item) => (Kind::Stream, Some(item)),
            None => (Kind::Sync, Some(&**t)),
        },
    };
    let (fallible, inner) = match out.map(|t| (result_inner(t), t)) {
        None => (false, None),
        Some((Some(inner), _)) => (true, Some(inner)),
        Some((None, t)) => (false, Some(t)),
    };
    let ret = match inner {
        None => Ty::Unit,
        Some(t) => ret_ty(t, self_name)?,
    };
    let ret_syn = inner.cloned().unwrap_or_else(|| syn::parse_quote!(()));

    if sig.asyncness.is_some() {
        if kind == Kind::Stream {
            return Err(Error::new_spanned(sig, "stream methods must not be `async fn`"));
        }
        kind = Kind::Async;
    }
    if ret == Ty::Handle {
        if has_self || kind != Kind::Sync {
            return Err(Error::new_spanned(&sig.output, "only synchronous constructors may return Self"));
        }
        kind = Kind::Ctor;
    }
    if kind == Kind::Stream {
        if !fallible {
            return Err(Error::new_spanned(&sig.output, "stream items must be `RustResult<T>`"));
        }
        if params.iter().any(|p| matches!(p.ty, Ty::Slice(_) | Ty::Str)) {
            return Err(Error::new_spanned(
                &sig.inputs,
                "stream methods outlive the call: take `String` / `Vec<T>` instead of borrowed parameters",
            ));
        }
    }

    Ok(Method { name: sig.ident.clone(), kind, has_self, params, ret, ret_syn, fallible, doc: doc(&f.attrs) })
}

fn last_segment(ty: &Type) -> Option<&PathSegment> {
    match ty {
        Type::Path(p) if p.qself.is_none() => p.path.segments.last(),
        _ => None,
    }
}

fn first_type_arg(seg: &PathSegment) -> Option<&Type> {
    let PathArguments::AngleBracketed(args) = &seg.arguments else { return None };
    args.args.iter().find_map(|a| match a {
        GenericArgument::Type(t) => Some(t),
        _ => None,
    })
}

fn prim(ty: &Type) -> Option<String> {
    let seg = last_segment(ty)?;
    let name = seg.ident.to_string();
    (seg.arguments.is_none() && PRIMITIVES.contains(&name.as_str())).then_some(name)
}

/// Types valid both as owned parameters and as return values.
fn owned_ty(ty: &Type) -> Option<Ty> {
    if let Some(p) = prim(ty) {
        return Some(Ty::Prim(p));
    }
    let seg = last_segment(ty)?;
    match seg.ident.to_string().as_str() {
        "bool" => Some(Ty::Bool),
        "String" => Some(Ty::String),
        "Vec" => prim(first_type_arg(seg)?).map(Ty::Vec),
        _ => None,
    }
}

fn param_ty(ty: &Type) -> Result<Ty> {
    let parsed = match ty {
        Type::Reference(r) if r.mutability.is_none() => match &*r.elem {
            Type::Slice(s) => prim(&s.elem).map(Ty::Slice),
            elem => last_segment(elem).filter(|s| s.ident == "str").map(|_| Ty::Str),
        },
        _ => owned_ty(ty),
    };
    parsed.ok_or_else(|| {
        Error::new(
            ty.span(),
            "unsupported parameter type (expected a primitive, bool, &[T], &str, String or Vec<T>)",
        )
    })
}

fn ret_ty(ty: &Type, self_name: &Ident) -> Result<Ty> {
    if matches!(ty, Type::Tuple(t) if t.elems.is_empty()) {
        return Ok(Ty::Unit);
    }
    if last_segment(ty).is_some_and(|s| s.ident == "Self" || s.ident == *self_name) {
        return Ok(Ty::Handle);
    }
    owned_ty(ty).ok_or_else(|| {
        Error::new(
            ty.span(),
            "unsupported return type (expected a primitive, bool, (), String, Vec<T> or Self)",
        )
    })
}

/// `RustResult<T>` / `Result<T, E>` -> `T`
fn result_inner(ty: &Type) -> Option<&Type> {
    let seg = last_segment(ty)?;
    (seg.ident == "RustResult" || seg.ident == "Result").then(|| first_type_arg(seg)).flatten()
}

/// `impl Stream<Item = I> + ...` -> `I`
fn stream_item(ty: &Type) -> Option<&Type> {
    let Type::ImplTrait(it) = ty else { return None };
    it.bounds.iter().find_map(|bound| {
        let TypeParamBound::Trait(t) = bound else { return None };
        let seg = t.path.segments.last()?;
        if seg.ident != "Stream" {
            return None;
        }
        let PathArguments::AngleBracketed(args) = &seg.arguments else { return None };
        args.args.iter().find_map(|a| match a {
            GenericArgument::AssocType(at) if at.ident == "Item" => Some(&at.ty),
            _ => None,
        })
    })
}

fn doc(attrs: &[Attribute]) -> String {
    attrs
        .iter()
        .filter(|a| a.path().is_ident("doc"))
        .filter_map(|a| match &a.meta {
            Meta::NameValue(nv) => match &nv.value {
                Expr::Lit(ExprLit { lit: Lit::Str(s), .. }) => Some(s.value().trim().to_string()),
                _ => None,
            },
            _ => None,
        })
        .collect::<Vec<_>>()
        .join("\n")
}
