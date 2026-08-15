#!/usr/bin/env bash
# Asserts this port still asks the backup service for exactly what upstream asks it for.
#
# The cloud tests (tests/IAGrim.Cloud.Tests) check behaviour against a real instance of the
# server. This checks the *tables*: the endpoint list, the item fields, and the conversion
# between a stored item and the wire. Those are the things that can drift without any test
# failing, because the server accepts whatever it is sent -- a field upstream adds and this port
# does not send is not an error, it is an item that quietly loses a property on every machine it
# reaches.
#
# Three comparisons, all extracted from source rather than written out here:
#   1. every URL upstream's Uris.cs builds exists in CloudUris.cs, with the same path;
#   2. every property of upstream's CloudItemDto exists in ours, in the same order;
#   3. the fields ItemConverter maps in each direction are the same set upstream maps -- which
#      is where the two known asymmetries live, so they are pinned rather than rediscovered.

set -uo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
LINUX_PORT="$(dirname "$SCRIPT_DIR")"
UPSTREAM="$(dirname "$LINUX_PORT")/iagd"

UP_URIS="$UPSTREAM/IAGrim/Backup/Cloud/Uris.cs"
UP_DTO="$UPSTREAM/IAGrim/Backup/Cloud/Dto/CloudItemDto.cs"
UP_CONVERTER="$UPSTREAM/IAGrim/Backup/Cloud/Util/ItemConverter.cs"

OUR_URIS="$LINUX_PORT/src/IAGrim.Cloud/CloudUris.cs"
OUR_DTO="$LINUX_PORT/src/IAGrim.Cloud/Dto/CloudItemDto.cs"
OUR_CONVERTER="$LINUX_PORT/src/IAGrim.Cloud/ItemConverter.cs"

for f in "$UP_URIS" "$UP_DTO" "$UP_CONVERTER" "$OUR_URIS" "$OUR_DTO" "$OUR_CONVERTER"; do
    [ -f "$f" ] || { echo "error: file not found: $f" >&2; exit 1; }
done

status=0

# ------------------------------------------------------------------ 1. the endpoint list

# Every path upstream builds from $host, as "Property=/path". Ours is read the same way, so the
# comparison is between endpoint tables rather than between two files of C#.
endpoints() {
    grep -oE '^\s*[A-Za-z0-9]+ = \$"\{(host|wsHost)\}[^"]*"' "$1" \
        | sed -E 's/^\s*//; s/ = \$"\{(host|wsHost)\}/=/; s/"$//' \
        | sort
}

up_endpoints="$(endpoints "$UP_URIS")"
our_endpoints="$(endpoints "$OUR_URIS")"

if [ "$up_endpoints" = "$our_endpoints" ]; then
    echo "OK    Cloud endpoints match upstream ($(echo "$up_endpoints" | grep -c .) endpoints)"
else
    echo "DRIFT The set of endpoints this port calls is no longer upstream's"
    comm -23 <(echo "$up_endpoints") <(echo "$our_endpoints") | sed 's/^/        upstream only: /'
    comm -13 <(echo "$up_endpoints") <(echo "$our_endpoints") | sed 's/^/        ours only:     /'
    status=1
fi

# The login page is not built from $host and is checked separately.
up_login="$(grep -oE 'LoginPageUrl = "[^"]*"' "$UP_URIS" | head -1)"
our_login="$(grep -oE 'LoginPageUrl = "[^"]*"' "$OUR_URIS" | head -1)"
if [ "$up_login" != "$our_login" ]; then
    echo "DRIFT The login page URL changed"
    echo "        upstream: $up_login"
    echo "        ours:     $our_login"
    status=1
fi

# The production host itself. A typo here is a request to somebody else's server.
up_host="$(grep -oE 'host = "https://[^"]*"' "$UP_URIS" | head -1 | sed 's/.*"\(.*\)"/\1/')"
our_host="$(grep -oE 'CloudHost = "https://[^"]*"' "$OUR_URIS" | head -1 | sed 's/.*"\(.*\)"/\1/')"
if [ "$up_host" != "$our_host" ]; then
    echo "DRIFT The backup host changed: upstream '$up_host', ours '$our_host'"
    status=1
fi

# ------------------------------------------------------------------ 2. the item fields

# Property names in declaration order. Order matters because upstream serialises with Newtonsoft,
# which writes properties in that order, and the wire-format test asserts the exact bytes.
properties() {
    grep -oE '^\s*public (virtual )?[A-Za-z0-9?<>]+ [A-Za-z0-9]+ \{ get; set; \}' "$1" \
        | sed -E 's/.* ([A-Za-z0-9]+) \{ get; set; \}/\1/'
}

up_properties="$(properties "$UP_DTO")"
our_properties="$(properties "$OUR_DTO")"

if [ "$up_properties" = "$our_properties" ]; then
    echo "OK    Cloud item fields match upstream ($(echo "$up_properties" | grep -c .) fields, same order)"
else
    echo "DRIFT The item sent to the backup service is no longer upstream's"
    diff <(echo "$up_properties") <(echo "$our_properties") | sed 's/^/        /'
    status=1
fi

# ------------------------------------------------------------------ 3. the conversion

# Which properties each direction assigns. A field upstream maps and this port does not is a
# field lost on every machine the item reaches; the reverse invents data upstream never sends.
# $1 file, $2 method name. Runs from the method signature to the end of the object initializer
# it returns, which is the first line that is just "};". That terminator is the same whether the
# method is written as `return new X { ... };` (upstream) or `=> new() { ... };` (here), so one
# extractor reads both without caring how the C# is shaped.
mapped() {
    awk -v method="$2" '
        index($0, "public static") && index($0, method "(") { inside = 1 }
        inside && /^[[:space:]]*};[[:space:]]*$/ { inside = 0 }
        inside && match($0, /^[[:space:]]*[A-Za-z0-9]+ = /) {
            field = $1
            print field
        }
    ' "$1" | sort
}

for direction in "ToUpload:ToUpload" "ToPlayerItem:ToPlayerItem"; do
    up_method="${direction%%:*}"
    our_method="${direction##*:}"

    up_fields="$(mapped "$UP_CONVERTER" "$up_method")"
    our_fields="$(mapped "$OUR_CONVERTER" "$our_method")"

    if [ "$up_fields" = "$our_fields" ]; then
        echo "OK    ItemConverter.$our_method maps upstream's $(echo "$up_fields" | grep -c .) fields"
    else
        echo "DRIFT ItemConverter.$our_method no longer maps what upstream maps"
        comm -23 <(echo "$up_fields") <(echo "$our_fields") | sed 's/^/        upstream maps, we do not: /'
        comm -13 <(echo "$up_fields") <(echo "$our_fields") | sed 's/^/        we map, upstream does not: /'
        status=1
    fi
done

# ------------------------------------------------------- 4. the limits the server enforces
#
# Read from the server's own source when it is checked out, so the batch size in this port is
# pinned to the rule that actually rejects a request rather than to a number someone remembered.
SERVER="$(dirname "$LINUX_PORT")/build/iagd-onlinesync/Server"
if [ -f "$SERVER/api/upload/upload.go" ]; then
    server_max="$(grep -oE 'len\(data\) > [0-9]+' "$SERVER/api/upload/upload.go" | grep -oE '[0-9]+' | head -1)"
    our_max="$(grep -oE 'MaxBatchSize = [0-9]+' "$LINUX_PORT/src/IAGrim.Cloud/Pacing.cs" | grep -oE '[0-9]+' | head -1)"

    if [ "$server_max" = "$our_max" ]; then
        echo "OK    Batch size matches the server's limit ($our_max items)"
    else
        echo "DRIFT The server accepts $server_max items per request, this port sends $our_max"
        status=1
    fi

    server_id_len="$(grep -oE 'len\(m.Id\) < [0-9]+' "$SERVER/api/upload/upload.go" | grep -oE '[0-9]+' | head -1)"
    our_id_len="$(grep -oE 'id.Length >= [0-9]+' "$LINUX_PORT/src/IAGrim.Platform/CloudIdentity.cs" | grep -oE '[0-9]+' | head -1)"
    if [ "$server_id_len" != "$our_id_len" ]; then
        echo "DRIFT The server requires ids of $server_id_len characters, this port checks for $our_id_len"
        status=1
    fi
else
    echo "SKIP  Server limits: run scripts/build-sync-server.sh to check against the real server"
fi

if [ $status -ne 0 ]; then
    echo
    echo "The wire format is the compatibility contract for a collection that lives on somebody"
    echo "else's server and is read by the Windows tool. Port the change before shipping."
fi
exit $status
