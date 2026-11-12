internal static class ManifestSource
{
    internal static string Build(int rows, int passes) => $$"""
        fn build(values: Range, directory: Path, endpoint: Connection) {
            let records = $std::storage::vector_empty(decltype(scope {
                name: "", path: directory, enabled: true, endpoint: endpoint,
            }));
            for value in values {
                let name = $std::string::concat("file-", value as String);
                $std::storage::vector_push(records, scope {
                    name: name, path: directory/name, enabled: true, endpoint: endpoint,
                });
            };
            $std::storage::vector_take(records)
        };
        let endpoint = $local;
        let directory = endpoint @ ./files/;
        let rows = build($std::ranges::iota(0, {{rows}}), directory, endpoint);
        let total = 0;
        for pass in $std::ranges::iota(0, {{passes}}) {
            for row in rows {
                if row::enabled
                    and ($std::path::filename(row::path) as String) == row::name {
                    total = total + 1;
                };
            };
        };
        total
        """;
}
