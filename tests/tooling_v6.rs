use msync::language;
use msync::tooling::{SymbolIndex, SymbolKind};

fn index(source: &str) -> SymbolIndex {
    SymbolIndex::new(&language::parse(source).unwrap())
}

fn names(index: &SymbolIndex, offset: usize) -> Vec<&str> {
    index
        .visible_at(offset)
        .into_iter()
        .map(|definition| definition.name.as_str())
        .collect()
}

#[test]
fn lexical_global_captures_are_definition_time_and_transitive() {
    let source = "root := first\nmacro outer(argument):\n    local := argument\n    macro inner():\n        global root, local\n        root/local -> output\n    inner()\nroot := second\nouter(input)\n";
    let index = index(source);
    let root_reference = source.find("root/local").unwrap();
    let root = index.definition_at(root_reference).unwrap();
    assert_eq!(root.selection.start, 0);
    assert_eq!(
        index.definition_at(root_reference + 5).unwrap().name,
        "local"
    );
    let argument_reference = source.find("local := argument").unwrap() + "local := ".len();
    let argument = index.definition_at(argument_reference).unwrap();
    assert_eq!(argument.kind, SymbolKind::Parameter);
    assert_eq!(argument.parent, Some(1));
    let inside = names(&index, root_reference);
    assert!(inside.contains(&"root") && inside.contains(&"local"));
    assert!(!inside.contains(&"argument"));
}

#[test]
fn values_require_import_but_prior_callables_are_lexically_visible() {
    let source = "value = true\nboolean prior(x):\n    return x\nboolean later(y):\n    return prior(y) and value\nlater(false)\n";
    let index = index(source);
    let call = source.find("prior(y)").unwrap();
    assert_eq!(index.definition_at(call).unwrap().kind, SymbolKind::Boolean);
    let unimported = source.find("and value").unwrap() + 4;
    assert!(index.definition_at(unimported).is_none());
    let visible = names(&index, call);
    assert!(visible.contains(&"prior") && visible.contains(&"y"));
    assert!(!visible.contains(&"value") && !visible.contains(&"later"));
}

#[test]
fn assignment_preserves_identity_and_drop_ends_visibility_not_captures() {
    let source = "ready = false\nboolean captured():\n    global ready\n    return ready\nready = true\ndrop ready\nresult = captured()\n";
    let index = index(source);
    let assignment = source.find("ready = true").unwrap();
    let ready = index.definition_at(assignment).unwrap();
    assert_eq!(ready.selection.start, 0);
    assert_eq!(
        index
            .definitions()
            .iter()
            .filter(|definition| definition.name == "ready")
            .count(),
        1
    );
    let captured = source.find("return ready").unwrap() + 7;
    assert_eq!(index.definition_at(captured).unwrap().id, ready.id);
    let after_drop = source.find("result =").unwrap();
    assert!(!names(&index, after_drop).contains(&"ready"));
    assert!(names(&index, source.len() + 20).contains(&"result"));
}

#[test]
fn branches_iterators_and_loop_values_do_not_export_fresh_bindings() {
    let source = "outer = false\nif exists input:\n    inner = true\n    outer = true\nelse:\n    alternate = false\nfor file in roots/<.+>:\n    file -> output/\n    body = true\nresult = loop:\n    local = \"done\"\n    break local\nlast = outer\n";
    let index = index(source);
    let inner = source.find("outer = true").unwrap();
    assert!(names(&index, inner).contains(&"inner"));
    let after_if = source.find("for file").unwrap();
    assert!(!names(&index, after_if).contains(&"inner"));
    assert!(!names(&index, after_if).contains(&"alternate"));
    let file_reference = source.find("file ->").unwrap();
    assert_eq!(
        index.definition_at(file_reference).unwrap().kind,
        SymbolKind::Iterator
    );
    let after_for = source.find("result =").unwrap();
    assert!(!names(&index, after_for).contains(&"file"));
    assert!(!names(&index, after_for).contains(&"body"));
    let local = source.find("break local").unwrap() + 6;
    assert_eq!(index.definition_at(local).unwrap().name, "local");
    assert!(names(&index, local).contains(&"local"));
    assert!(!names(&index, source.find("last =").unwrap()).contains(&"local"));
    assert_eq!(
        index
            .definitions()
            .iter()
            .find(|definition| definition.name == "last")
            .unwrap()
            .detail,
        "bool last"
    );
}

#[test]
fn range_builtin_names_are_not_lexical_references_but_arguments_are() {
    let source = "views = true\nranges = false\nempty := filename\ncount = \"2\"\nseed = \"text\"\nboolean keep(item):\n    return exists item\nsymbol project(item):\n    return item\nfiles := ranges::of(seed)\nselected := files | filter(keep) | views::transform(project) | take(count)\nready = files | empty\nfound = ranges::any_of(files, keep)\n";
    let index = index(source);
    for spelling in [
        "ranges::of",
        "filter(keep)",
        "views::transform",
        "take(count)",
        "empty\nfound",
        "ranges::any_of",
    ] {
        let offset = source.find(spelling).unwrap();
        assert!(index.definition_at(offset).is_none(), "{spelling}");
    }
    for spelling in ["of(seed)", "take(count)"] {
        let offset = source.find(spelling).unwrap() + spelling.find('(').unwrap() + 1;
        assert_eq!(
            index.definition_at(offset).unwrap().kind,
            SymbolKind::Binding
        );
    }
    for spelling in ["filter(keep)", "any_of(files, keep)"] {
        let offset = source.find(spelling).unwrap() + spelling.find("keep").unwrap();
        assert_eq!(
            index.definition_at(offset).unwrap().kind,
            SymbolKind::Boolean
        );
    }
    let projection = source.find("transform(project)").unwrap() + "transform(".len();
    assert_eq!(
        index.definition_at(projection).unwrap().kind,
        SymbolKind::Symbol
    );
}

#[test]
fn ordinary_user_functions_keep_range_like_names_outside_pipelines() {
    let source = "boolean empty(item):\n    return item\nmacro filter():\n    delete file\nready = empty(true)\nfilter()\n";
    let index = index(source);
    let empty = source.find("empty(true)").unwrap();
    assert_eq!(
        index.definition_at(empty).unwrap().kind,
        SymbolKind::Boolean
    );
    let filter = source.rfind("filter()").unwrap();
    assert_eq!(index.definition_at(filter).unwrap().kind, SymbolKind::Macro);
}

#[test]
fn callable_and_value_namespaces_do_not_confuse_filenames_or_ports() {
    let source = "boolean item():\n    return true\nitem := source\nitem -> output\nflag = item()\nP => executable()\nstdout := accidental\nP::stdout -> bytes\nlocal => $local\nlocal::item -> qualified\n$local::\"item\" -> quoted\n";
    let index = index(source);
    let value = source.find("item -> output").unwrap();
    assert_eq!(
        index.definition_at(value).unwrap().kind,
        SymbolKind::Binding
    );
    let callable = source.find("item()\nP").unwrap();
    assert_eq!(
        index.definition_at(callable).unwrap().kind,
        SymbolKind::Boolean
    );
    let port = source.find("P::stdout").unwrap();
    assert_eq!(index.definition_at(port).unwrap().kind, SymbolKind::Program);
    assert!(index.definition_at(port + 3).is_none());
    let qualified = source.find("local::item").unwrap();
    assert_eq!(
        index.definition_at(qualified).unwrap().kind,
        SymbolKind::Space
    );
    assert_eq!(
        index.definition_at(qualified + 7).unwrap().kind,
        SymbolKind::Binding
    );
    let quoted = source.find("\"item\"").unwrap() + 1;
    assert!(index.definition_at(quoted).is_none());
}

#[test]
fn unicode_spans_details_and_nested_symbol_parents_are_owned_by_ast() {
    let source = "boolean 可莉(朋友):\n    boolean 问候(对象):\n        return 对象\n    return 问候(朋友)\n可莉(true)\n";
    let index = index(source);
    let outer = index
        .definitions()
        .iter()
        .find(|definition| definition.name == "可莉")
        .unwrap();
    assert_eq!(outer.detail, "boolean 可莉(朋友)");
    let inner = index
        .definitions()
        .iter()
        .find(|definition| definition.name == "问候")
        .unwrap();
    assert_eq!(inner.parent, Some(outer.id));
    for definition in index.definitions() {
        assert_eq!(
            &source[definition.selection.start..definition.selection.end],
            definition.name
        );
    }
    for reference in index.references() {
        assert_eq!(
            &source[reference.span.start..reference.span.end],
            index.definitions()[reference.definition].name
        );
    }
}

#[test]
fn intrinsic_namespace_members_never_navigate_to_shadowing_values() {
    let source = "pwd = \"text\"\nthis = \"text\"\nhere => $pwd\nmodule => $file\nP => executable()\nprocess := P\nstdout = \"text\"\na := $pwd::pwd\nb := here::pwd\nc := module::this\nprocess::stdout -> output\n";
    let index = index(source);
    for member in ["$pwd::pwd", "here::pwd", "module::this", "process::stdout"] {
        let start = source.find(member).unwrap();
        let suffix = start + member.find("::").unwrap() + 2;
        assert!(index.definition_at(suffix).is_none(), "{member}");
    }
    let process = source.find("process::stdout").unwrap();
    assert_eq!(index.definition_at(process).unwrap().name, "process");
}

#[test]
fn pure_space_calls_are_not_mislabeled_as_program_ports() {
    let source = "symbol identity(x):\n    return x\nitem := source\nlocal => identity($local)\nlocal::item -> output\n";
    let index = index(source);
    let local = index
        .definitions()
        .iter()
        .find(|definition| definition.name == "local")
        .unwrap();
    assert_eq!(local.kind, SymbolKind::Space);
    let item = source.find("local::item").unwrap() + 7;
    assert_eq!(index.definition_at(item).unwrap().name, "item");
}

#[test]
fn final_template_and_loop_scopes_do_not_capture_arbitrary_following_offsets() {
    for source in [
        "root = true\nmacro last(parameter):\n    local = false\n",
        "root = true\nfor item in entries/<.+>:\n    local = false\n",
    ] {
        let index = index(source);
        let visible = names(&index, source.len() + 20);
        assert!(visible.contains(&"root"));
        for name in ["parameter", "local", "item"] {
            assert!(!visible.contains(&name), "{source}: {visible:?}");
        }
    }
}
