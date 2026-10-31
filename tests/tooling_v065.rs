use msync::language;
use msync::tooling::{SymbolIndex, SymbolKind};

fn index(source: &str) -> SymbolIndex {
    SymbolIndex::new(&language::parse(source).unwrap())
}

#[test]
fn literal_range_items_resolve_lexical_values_and_specialized_callables() {
    let source = "label = \"可莉\"\nboolean ready(value):\n    return value\nsymbol codex(file):\n    return ~/.codex/file\nitems := [label, ready(true), $wsl::codex(config.toml)]\n";
    let index = index(source);
    for (spelling, kind) in [
        ("[label", SymbolKind::Binding),
        ("ready(true)", SymbolKind::Boolean),
        ("codex(config.toml)", SymbolKind::Symbol),
    ] {
        let offset = source.find(spelling).unwrap() + usize::from(spelling.starts_with('['));
        assert_eq!(
            index.definition_at(offset).unwrap().kind,
            kind,
            "{spelling}"
        );
    }
    let items = index
        .definitions()
        .iter()
        .find(|definition| definition.name == "items")
        .unwrap();
    assert_eq!(items.detail, "range items");
}

#[test]
fn canonical_builtin_members_do_not_navigate_to_shadowing_values() {
    let source = "filter := shadow\nany_of := shadow\nboolean keep(item):\n    return item\nflags := [true, false]\nselected := flags | $view::filter(keep)\nready = $view::any_of(flags, keep)\n";
    let index = index(source);
    for member in ["$view::filter", "$view::any_of"] {
        let offset = source.find(member).unwrap() + "$view::".len();
        assert!(index.definition_at(offset).is_none(), "{member}");
    }
    for call in ["filter(keep)", "any_of(flags, keep)"] {
        let offset = source.find(call).unwrap() + call.find("keep").unwrap();
        assert_eq!(
            index.definition_at(offset).unwrap().kind,
            SymbolKind::Boolean
        );
    }
}

#[test]
fn scheduled_conditionals_reuse_branch_scope_and_bool_references() {
    let source = "ready = true\nif release ready:\n    hidden = true\nelse if acquire ready:\n    alternate = false\nif acqrel ready:\n    last = true\nafter = false\n";
    let index = index(source);
    for spelling in ["release ready", "acquire ready", "acqrel ready"] {
        let offset = source.find(spelling).unwrap() + spelling.find("ready").unwrap();
        assert_eq!(index.definition_at(offset).unwrap().name, "ready");
    }
    let after = source.find("after =").unwrap();
    let visible = index.visible_at(after);
    for hidden in ["hidden", "alternate", "last"] {
        assert!(!visible.iter().any(|definition| definition.name == hidden));
    }
}

#[test]
fn scope_qualified_pure_calls_are_not_native_program_definitions() {
    let source = "symbol identity(value):\n    return value\nlocal => $local::identity($local)\n";
    let index = index(source);
    let local = index
        .definitions()
        .iter()
        .find(|definition| definition.name == "local")
        .unwrap();
    assert_eq!(local.kind, SymbolKind::Space);
    let call = source.find("identity($local)").unwrap();
    assert_eq!(index.definition_at(call).unwrap().kind, SymbolKind::Symbol);
}

#[test]
fn explicit_view_scope_aliases_keep_member_and_callback_identities_distinct() {
    let source = "filter := unrelated\nboolean keep(item):\n    return item\napi := $view\nalias => api\nflags := [true]\nselected := flags | alias::filter(keep)\n";
    let index = index(source);
    let call = source.find("alias::filter(keep)").unwrap();
    let alias = index.definition_at(call).unwrap();
    assert_eq!(alias.name, "alias");
    assert_eq!(alias.kind, SymbolKind::Space);
    assert!(index.is_view_scope(alias.id));
    assert!(index.definition_at(call + "alias::".len()).is_none());
    assert_eq!(
        index
            .definition_at(call + "alias::filter(".len())
            .unwrap()
            .kind,
        SymbolKind::Boolean
    );
    let api = source.find("alias => api").unwrap() + "alias => ".len();
    assert_eq!(index.definition_at(api).unwrap().name, "api");
}
