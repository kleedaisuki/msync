use msync::language;
use msync::tooling::{SymbolIndex, SymbolKind, standard_library};

fn index(source: &str) -> SymbolIndex {
    SymbolIndex::new(&language::parse(source).unwrap())
}

#[test]
fn lexical_scope_members_keep_their_declaration_and_namespace_identity() {
    let source = "codex := unrelated\nscope hello:\n    codex := ~/.codex\n    scope nested:\n        config := config.toml\n    symbol identity(value):\n        return value\nalias => hello\nhello::codex -> alias::nested::config\nresult := alias::identity(hello::codex)\n";
    let index = index(source);
    let member = source.rfind("hello::codex").unwrap() + "hello::".len();
    let declaration = index.definition_at(member).unwrap();
    assert_eq!(declaration.name, "codex");
    assert_eq!(
        declaration.selection.start,
        source.find("    codex").unwrap() + 4
    );
    let nested = source.find("alias::nested::config").unwrap() + "alias::nested::".len();
    assert_eq!(index.definition_at(nested).unwrap().name, "config");
    let call = source.find("alias::identity").unwrap() + "alias::".len();
    assert_eq!(index.definition_at(call).unwrap().kind, SymbolKind::Symbol);
    let members = index.members_at("alias", source.len()).unwrap();
    assert_eq!(members.len(), 3);
    assert!(members.iter().all(|definition| definition.name != "alias"));
    assert_eq!(
        index.members_at("alias::nested", source.len()).unwrap()[0].name,
        "config"
    );
}

#[test]
fn standard_library_aliases_reference_source_members_not_lexical_shadows() {
    let source = "exists := shadow\nfilter := shadow\nfs => $std::fs\nstd => $std\nviews => std::ranges\nboolean keep(item):\n    return item\nready = fs::exists(file)\nflags := [true] | views::filter(keep)\nother = $std::fs::is_directory(directory)\n";
    let index = index(source);
    for (spelling, module, name) in [
        ("fs::exists", "fs", "exists"),
        ("views::filter", "ranges", "filter"),
        ("$std::fs::is_directory", "fs", "is_directory"),
    ] {
        let offset = source.find(spelling).unwrap() + spelling.rfind("::").unwrap() + 2;
        let reference = index
            .library_reference_at(offset)
            .unwrap_or_else(|| panic!("missing {spelling}: {:?}", index.library_references()));
        assert_eq!((&*reference.module, &*reference.name), (module, name));
        assert!(index.definition_at(offset).is_none());
    }
    assert_eq!(
        index.library_namespace_at("views", source.len()).as_deref(),
        Some("ranges")
    );
    assert_eq!(
        index
            .library_namespace_at("std::fs", source.len())
            .as_deref(),
        Some("fs")
    );
    let callback = source.find("filter(keep)").unwrap() + "filter(".len();
    assert_eq!(
        index.definition_at(callback).unwrap().kind,
        SymbolKind::Boolean
    );
}

#[test]
fn library_inventory_is_derived_from_actual_exported_source_declarations() {
    let library = standard_library();
    let filesystem = library.iter().find(|module| module.name == "fs").unwrap();
    let exists = filesystem
        .definitions()
        .find(|definition| definition.name == "exists")
        .unwrap();
    assert_eq!(exists.kind, SymbolKind::Native);
    assert_eq!(
        &filesystem.source[exists.selection.start..exists.selection.end],
        "exists"
    );
    assert!(exists.detail.starts_with("native exists(path) -> Bool"));
    assert!(
        !filesystem
            .definitions()
            .any(|definition| definition.name == "fs")
    );
}

#[test]
fn intrinsic_range_navigation_uses_actual_native_declarations() {
    let source =
        "boolean keep(item):\n    return item\nflags := [true] | $std::ranges::filter(keep)\n";
    let index = index(source);
    let offset = source.find("filter(keep)").unwrap();
    let reference = index.library_reference_at(offset).unwrap();
    assert_eq!(reference.module, "ranges");
    let module = standard_library()
        .iter()
        .find(|module| module.name == "ranges")
        .unwrap();
    let definition = module
        .definitions()
        .find(|definition| definition.name == reference.name)
        .unwrap();
    assert_eq!(
        &module.source[definition.selection.start..definition.selection.end],
        "filter"
    );
    assert_eq!(definition.kind, SymbolKind::Native);
    assert!(module.source[definition.span.start..definition.span.end].starts_with("native filter"));
    assert!(
        definition
            .detail
            .starts_with("native filter(values, predicate)")
    );
    assert!(!definition.detail.contains(" -> "));
}
