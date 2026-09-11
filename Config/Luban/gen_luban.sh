#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
project_root="$(cd "$script_dir/../.." && pwd)"
source_root="$script_dir/source"
code_output="$project_root/Assets/Splatoon/Config/Generated"
data_output="$project_root/Assets/GameResource/Bootstrap/Config/Luban"
luban="$project_root/Tools/Luban/tools/Luban/Luban.dll"

if [[ $# -ne 0 ]]; then
    echo "Usage: Config/Luban/gen_luban.sh" >&2
    exit 2
fi

mkdir -p "$code_output" "$data_output"

dotnet "$luban" \
    -t client \
    -c cs-simple-json \
    -d json \
    --conf "$source_root/luban.conf" \
    -x "outputCodeDir=$code_output" \
    -x "outputDataDir=$data_output"

echo "Luban generation completed."
