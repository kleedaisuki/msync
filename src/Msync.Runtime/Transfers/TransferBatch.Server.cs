namespace Msync.Runtime;

public static partial class TransferEngine
{
    private static string BatchServer => field ??= ShellProgram.Source(ShellLibrary + "\n" + """
        coordinator=$(mktemp -d "${TMPDIR:-/tmp}/.msync-batch-XXXXXXXXXX")
        exec 9< "$coordinator"
        MSYNC_OWNED_DIRECTORY_FD=9
        export MSYNC_OWNED_DIRECTORY_FD
        stage=
        cleanup() {
            if [ -n "$stage" ]; then rm -rf -- "$stage"; fi
            rm -rf -- "$coordinator"
        }
        trap cleanup EXIT HUP INT TERM
        error=$coordinator/error
        empty=$coordinator/empty
        : > "$empty"
        decode() {
            IFS= read -r encoded || return 1
            decoded=$(printf '%b.' "$encoded"); decoded=${decoded%.}
        }
        result() {
            bytes=$(wc -c < "$3")
            printf 'MSYNC-BATCH %s %s %s\n' "$1" "$2" "$bytes"
            cat -- "$3"
        }
        source_kind() {
            case "$2" in
                CopyFile) [ -f "$1" ] && [ ! -L "$1" ] ;;
                CopyDirectory) [ -d "$1" ] && [ ! -L "$1" ] ;;
                CopyLink) [ -L "$1" ] ;;
                *) [ -e "$1" ] || [ -L "$1" ] ;;
            esac
        }
        query_batch() {
            count=$1
            decode; program=$decoded
            set --
            while [ "$count" -gt 0 ]; do
                decode
                set -- "$@" "$decoded"
                count=$((count - 1))
            done
            status=0
            sh -c "$program" sh "$@" < /dev/null > "$coordinator/output" 2> "$error" || status=$?
            if [ "$status" -ne 0 ]; then cat -- "$error" >> "$coordinator/output"; fi
            result -1 "$status" "$coordinator/output"
        }
        prepare_upload() {
            destination=$(expand "$raw"; printf .); destination=${destination%.}
            target=$destination
            if [ "$mode" = replace ]; then target=${destination%/.}; [ -n "$target" ] || target=/; fi
            if [ "$mode" = container ]; then target=$destination/$basename; fi
            parent=${target%/*}
            mkdir -p -- "$parent" || return
            stage=$(mktemp -d "$parent/.msync-XXXXXXXXXX") || return
        }
        publish_upload() {
            payload=$stage/payload
            if [ "$kind" = relay ]; then payload=$stage/$basename; fi
            if [ ! -e "$payload" ] && [ ! -L "$payload" ]; then
                printf 'The source did not produce its payload: %s\n' "$raw" >&2
                return 1
            fi
            if [ "$mode" = replace ] || [ "$mode" = replace-final ]; then
                if [ ! -d "$payload" ] || [ -L "$payload" ]; then
                    printf 'Tree replacement requires a directory source: %s\n' "$raw" >&2
                    return 1
                fi
                rm -rf -- "$target" || return
            elif [ "$mode" = exact ] && [ -d "$target" ]; then
                printf 'Exact destination is an existing directory: %s; append / or /.\n' "$target" >&2
                return 1
            fi
            if [ "$kind" = literal ] && [ -e "$target" ]; then
                chmod --reference="$target" -- "$payload" || return
            fi
            install "$payload" "$target"
        }
        upload_batch() {
            count=$1
            index=0
            while [ "$index" -lt "$count" ]; do
                decode; raw=$decoded
                decode; mode=$decoded
                decode; basename=$decoded
                decode; kind=$decoded
                : > "$error"
                status=0
                prepare_upload 2> "$error" || status=$?
                if [ "$status" -eq 0 ]; then
                    if [ "$kind" = relay ]; then
                        tar -b1 --transform=__MSYNC_ARCHIVE_NAME_DECODING__ -xpf - -C "$stage" 2>> "$error" || status=$?
                    else
                        tar -b1 -xpf - -C "$stage" 2>> "$error" || status=$?
                    fi
                else
                    tar -b1 -tf - > /dev/null 2>> "$error" || :
                fi
                decode
                if [ -n "$decoded" ]; then printf '%s\n' "$decoded" >> "$error"; status=1; fi
                if [ "$status" -eq 0 ]; then publish_upload 2>> "$error" || status=$?; fi
                if [ -n "$stage" ]; then rm -rf -- "$stage"; stage=; fi
                result "$index" "$status" "$error"
                index=$((index + 1))
            done
        }
        download_batch() {
            count=$1
            index=0
            while [ "$index" -lt "$count" ]; do
                decode; raw=$decoded
                decode; kind=$decoded
                source=$(expand "$raw"; printf .); source=${source%.}
                while [ "${source%/}" != "$source" ] && [ "$source" != / ]; do source=${source%/}; done
                : > "$error"
                if source_kind "$source" "$kind"; then
                    result "$index" 0 "$empty"
                    parent=${source%/*}; name=${source##*/}
                    status=0
                    tar -b1 --no-unquote --format=pax --transform=__MSYNC_ARCHIVE_NAME_ENCODING__ -cpf - -C "$parent" "./$name" 2> "$error" || status=$?
                    result "$index" "$status" "$error"
                else
                    printf 'Cannot execute %s for source: %s\n' "$kind" "$source" > "$error"
                    result "$index" 1 "$error"
                fi
                index=$((index + 1))
            done
        }
        remote_copy_batch() {
            count=$1
            index=0
            while [ "$index" -lt "$count" ]; do
                decode; raw=$decoded
                decode; kind=$decoded
                decode; destination=$decoded
                decode; mode=$decoded
                source=$(expand "$raw"; printf .); source=${source%.}
                : > "$error"
                status=0
                if source_kind "$source" "$kind"; then
                    sh -c
        """ + " " + Quote(CopyScript) + " \"sh\" \"$destination\" \"$mode\" single \"$raw\" > \"$coordinator/output\" 2> \"$error\" || status=$?\n" + """
                else
                    printf 'Cannot execute %s for source: %s\n' "$kind" "$source" > "$error"
                    status=1
                fi
                result "$index" "$status" "$error"
                index=$((index + 1))
            done
        }
        metadata_batch() {
            count=$1
            include_kinds=$2
            index=0
            cached_parent=
            canonical_parent=
            while [ "$index" -lt "$count" ]; do
                decode; raw=$decoded
                path=$(expand "$raw"; printf .); path=${path%.}
                while [ "${path%/}" != "$path" ] && [ "$path" != / ]; do path=${path%/}; done
                parent=${path%/*}; leaf=${path##*/}
                [ -n "$parent" ] || parent=/
                : > "$error"
                status=0
                if [ "$parent" != "$cached_parent" ]; then
                    canonical_parent=$(realpath -m -- "$parent" 2> "$error" && printf .) || status=$?
                    if [ "$status" -eq 0 ]; then
                        canonical_parent=${canonical_parent%.}; canonical_parent=${canonical_parent%?}
                        cached_parent=$parent
                    fi
                fi
                canonical=${canonical_parent%/}/$leaf
                case "$leaf" in
                    ''|.|..)
                        canonical=$(realpath -m -- "$path" 2>> "$error" && printf .) || status=$?
                        canonical=${canonical%.}; canonical=${canonical%?}
                        ;;
                esac
                kind=
                if [ "$include_kinds" -eq 1 ]; then
                    kind=unknown
                    if attributes=$(LC_ALL=C stat -c %F -- "$path" 2>&1); then
                        case "$attributes" in
                            regular*) kind=regular ;; directory) kind=directory ;; 'symbolic link') kind=symlink ;;
                            'block special file') kind=block ;; 'character special file') kind=character ;;
                            fifo) kind=fifo ;; socket) kind=socket ;;
                        esac
                    else
                        case "$attributes" in
                            *': No such file or directory'|*': Not a directory') kind=not_found ;;
                            *) printf '%s\n' "$attributes" >> "$error"; status=1 ;;
                        esac
                    fi
                fi
                if [ "$status" -eq 0 ]; then
                    printf '%s\0%s' "$kind" "$canonical" > "$coordinator/output"
                    result "$index" 0 "$coordinator/output"
                else
                    result "$index" "$status" "$error"
                fi
                index=$((index + 1))
            done
        }
        while read -r command count; do
            case "$command" in
                QUERY) query_batch "$count" ;;
                UPLOAD) upload_batch "$count" ;;
                DOWNLOAD) download_batch "$count" ;;
                REMOTECOPY) remote_copy_batch "$count" ;;
                METADATA) metadata_batch "$count" 1 ;;
                NAMESPACE) metadata_batch "$count" 0 ;;
                *) printf 'Unknown native batch operation: %s\n' "$command" >&2; exit 1 ;;
            esac
        done
        """).Replace("__MSYNC_ARCHIVE_NAME_ENCODING__", Quote(ArchiveNameEncoding), StringComparison.Ordinal)
            .Replace("__MSYNC_ARCHIVE_NAME_DECODING__", Quote(ArchiveNameDecoding), StringComparison.Ordinal);
}
