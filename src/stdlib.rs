//! Embedded, navigable standard-library sources and native capability signatures.

use crate::hir::control::Type;

pub const SOURCES: &[(&str, &str)] = &[
    ("fs", include_str!("../stdlib/fs.msync")),
    ("path", include_str!("../stdlib/path.msync")),
    ("bool", include_str!("../stdlib/bool.msync")),
    ("string", include_str!("../stdlib/string.msync")),
    ("ranges", include_str!("../stdlib/ranges.msync")),
];

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum NativeParam {
    Path,
    RawPath,
    String,
    Bool,
    Scope,
    AnyScalar,
}
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum NativeResultType {
    Void,
    Scalar(Type),
    Range(Type),
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum NativeEffect {
    Pure,
    Read,
    Write,
}
#[derive(Clone, Debug)]
pub struct Signature {
    pub parameters: &'static [NativeParam],
    pub result: NativeResultType,
    pub effect: NativeEffect,
}
impl Signature {
    pub fn accepts(&self, arguments: &[Type]) -> bool {
        self.parameters.len() == arguments.len()
            && self
                .parameters
                .iter()
                .zip(arguments)
                .all(|(parameter, argument)| match parameter {
                    NativeParam::Path | NativeParam::RawPath => {
                        Type::join([Type::String, Type::Path])
                            .unwrap()
                            .accepts(argument)
                    }
                    NativeParam::String => *argument == Type::String,
                    NativeParam::Bool => *argument == Type::Bool,
                    NativeParam::Scope => *argument == Type::Space,
                    NativeParam::AnyScalar => true,
                })
    }
}

pub fn signature(name: &str) -> Option<Signature> {
    use NativeEffect::{Pure, Read, Write};
    use NativeParam::{Bool as B, Path as P, RawPath as R, String as S};
    use NativeResultType::{Range, Scalar, Void};
    let (parameters, result, effect): (&'static [NativeParam], _, _) = match name
        .strip_prefix("fs.")
    {
        Some(
            "exists" | "is_regular_file" | "is_directory" | "is_symlink" | "is_block_file"
            | "is_character_file" | "is_fifo" | "is_socket" | "is_other" | "is_empty",
        ) => (&[P], Scalar(Type::Bool), Read),
        Some("status" | "symlink_status") => (&[P], Scalar(Type::String), Read),
        Some("absolute" | "canonical" | "weakly_canonical" | "read_symlink") => {
            (&[P], Scalar(Type::Path), Read)
        }
        Some("temp_directory_path") => (&[], Scalar(Type::Path), Read),
        Some("equivalent") => (&[P, P], Scalar(Type::Bool), Read),
        Some("components") => (&[P], Range(Type::Path), Pure),
        Some("permissions") => (&[P], Range(Type::String), Read),
        Some("is_readonly") => (&[P], Scalar(Type::Bool), Read),
        Some("set_permissions") => (&[P, S], Void, Write),
        Some("set_readonly") => (&[P, B], Void, Write),
        Some("delete") => (&[P], Void, Write),
        Some("create_directory" | "create_directories" | "remove" | "remove_all") => {
            (&[P], Scalar(Type::Bool), Write)
        }
        Some("rename" | "create_hard_link" | "copy_symlink") => (&[P, P], Void, Write),
        Some("create_symlink" | "create_directory_symlink") => (&[R, P], Void, Write),
        Some("copy_file") => (&[P, P], Scalar(Type::Bool), Write),
        Some("directory_iterator" | "recursive_directory_iterator") => {
            (&[P], Range(Type::Path), Read)
        }
        Some(
            "root_name" | "root_directory" | "root_path" | "relative_path" | "parent_path"
            | "filename" | "stem" | "extension" | "lexically_normal" | "remove_filename"
            | "make_preferred",
        ) => (&[P], Scalar(Type::Path), Pure),
        Some(
            "lexically_relative"
            | "lexically_proximate"
            | "replace_filename"
            | "replace_extension"
            | "append"
            | "concat",
        ) => (&[P, P], Scalar(Type::Path), Pure),
        Some("string" | "generic_string") => (&[P], Scalar(Type::String), Pure),
        Some(
            "path_empty" | "has_root_name" | "has_root_directory" | "has_root_path"
            | "has_relative_path" | "has_parent_path" | "has_filename" | "has_stem"
            | "has_extension" | "is_absolute" | "is_relative",
        ) => (&[P], Scalar(Type::Bool), Pure),
        _ => match name.strip_prefix("string.") {
            Some("contains" | "starts_with" | "ends_with") => (&[S, S], Scalar(Type::Bool), Pure),
            Some("concat") => (&[S, S], Scalar(Type::String), Pure),
            Some("trim" | "trim_start" | "trim_end" | "to_lowercase" | "to_uppercase") => {
                (&[S], Scalar(Type::String), Pure)
            }
            Some("replace") => (&[S, S, S], Scalar(Type::String), Pure),
            Some("split") => (&[S, S], Range(Type::String), Pure),
            _ => return None,
        },
    };
    Some(Signature {
        parameters,
        result,
        effect,
    })
}
