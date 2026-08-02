#!/bin/zsh

emulate -L zsh
set -euo pipefail

[[ -n ${HOME:-} && "$HOME" == /* && -d "$HOME" ]] || {
  print -u2 -- "HOME must be an existing absolute user directory."
  exit 1
}

purge=0
confirm_purge=0
while (( $# > 0 )); do
  case "$1" in
    --purge) purge=1 ;;
    --confirm-purge) confirm_purge=1 ;;
    *)
      print -u2 -- "Usage: uninstall.zsh [--purge --confirm-purge]"
      exit 2
      ;;
  esac
  shift
done
if (( confirm_purge == 1 && purge == 0 )); then
  print -u2 -- "--confirm-purge requires --purge."
  exit 2
fi
if (( purge == 1 && confirm_purge == 0 )); then
  print -u2 -- "Purge target: $HOME/.opencowork"
  print -u2 -- "Re-run with --purge --confirm-purge to delete that exact user-data directory."
  exit 3
fi

local_root="$HOME/.local"
install_root="$local_root/share/opencowork/bin"
entry="$local_root/bin/opencowork"
data_root="$HOME/.opencowork"
for boundary in "$local_root" "$local_root/share" "$local_root/bin" "$local_root/share/opencowork"; do
  [[ ! -L "$boundary" ]] || {
    print -u2 -- "Refusing a symbolic-link uninstall boundary: $boundary"
    exit 1
  }
done
if (( purge == 1 )) && [[ -L "$data_root" ]]; then
  print -u2 -- "Refusing to purge a symbolic-link data directory: $data_root"
  exit 1
fi
if [[ -e "$entry" || -L "$entry" ]]; then
  [[ -L "$entry" && "$(readlink "$entry")" == "$install_root/opencowork" ]] || {
    print -u2 -- "Refusing to remove an unrelated command entry: $entry"
    exit 1
  }
  rm -- "$entry"
fi
[[ ! -d "$install_root" ]] || rm -rf -- "$install_root"
rmdir "$local_root/share/opencowork" "$local_root/share" "$local_root/bin" "$local_root" \
  2>/dev/null || true

if (( purge == 1 )); then
  [[ ! -e "$data_root" ]] || rm -rf -- "$data_root"
  print -r -- "Purged user data: $data_root"
else
  print -r -- "Uninstalled OpenCoWork; preserved user data: $data_root"
fi
