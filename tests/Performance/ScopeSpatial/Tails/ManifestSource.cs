internal static class ManifestSource
{
    internal static string Build(int rows, int passes) => $$"""
        fn build(values: Range, directory: Path, endpoint: Connection) {
            let records = $std::storage::Buffer::new([]);
            for value in values {
                let name = $std::string::concat("file-", value as String);
                $std::storage::Buffer::push(records, scope {
                    name: name, path: directory/name, enabled: true, endpoint: endpoint,
                });
            };
            $std::storage::Buffer::snapshot(records)
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
