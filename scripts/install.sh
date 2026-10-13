#!/usr/bin/env bash
set -euo pipefail

# Resolve sources relative to this script, not to the caller's working directory.
repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
install_dir="${HOME:?HOME must be set}/.local/share/lakecode"
bin_dir="$HOME/.local/bin"
command_path="$bin_dir/lakecode"

if [[ "$(uname -s)" != Linux ]]; then
    printf 'Ошибка: этот установщик поддерживает только Linux.\n' >&2
    exit 1
fi

case "$(uname -m)" in
    x86_64) runtime=linux-x64 ;;
    aarch64|arm64) runtime=linux-arm64 ;;
    *) printf 'Ошибка: неподдерживаемая архитектура. Нужна x64 или ARM64.\n' >&2; exit 1 ;;
esac

if ! command -v dotnet >/dev/null 2>&1; then
    printf 'Ошибка: установите .NET SDK 10, затем повторите установку.\n' >&2
    exit 1
fi

# Do not overwrite an unrelated command, including a dangling symlink.
if [[ -e "$command_path" || -L "$command_path" ]]; then
    if [[ ! -L "$command_path" ]] || [[ "$(readlink -- "$command_path")" != "$install_dir/LakeCode.Cli" ]]; then
        printf 'Ошибка: %s уже существует и не является ссылкой этого установщика.\n' "$command_path" >&2
        exit 1
    fi
fi

printf 'Публикация LakeCode (Release, %s) в %s\n' "$runtime" "$install_dir"
# publish also restores dependencies and builds the project and its references.
# Keep separate files: no trimming or single-file packaging for this dependency graph.
dotnet publish "$repo_root/LakeCode.Cli/LakeCode.Cli.csproj" \
    --configuration Release \
    --runtime "$runtime" \
    --self-contained true \
    -p:UseAppHost=true \
    -p:PublishTrimmed=false \
    -p:PublishSingleFile=false \
    --output "$install_dir"

mkdir -p -- "$bin_dir"
ln -sfnT -- "$install_dir/LakeCode.Cli" "$command_path"

printf '\nУстановлено: %s\nЗапуск из каталога вашего проекта: lakecode\n' "$command_path"
case ":${PATH:-}:" in
    *":$bin_dir:"*) ;;
    *)
        printf '\nДобавьте в ~/.bashrc и выполните в текущем Bash:\n'
        printf 'export PATH="$HOME/.local/bin:$PATH"\n'
        ;;
esac
