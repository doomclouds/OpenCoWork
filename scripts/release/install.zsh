#!/bin/zsh

emulate -L zsh
set -euo pipefail

script_directory=${0:A:h}
[[ -n ${HOME:-} && "$HOME" == /* && -d "$HOME" ]] || {
  print -u2 -- "HOME must be an existing absolute user directory."
  exit 1
}
[[ -f "$script_directory/opencowork" && -f "$script_directory/release-manifest.json" ]] || {
  print -u2 -- "Run install.zsh from an extracted OpenCoWork macOS package."
  exit 1
}

local_root="$HOME/.local"
share_root="$local_root/share"
install_root="$share_root/opencowork/bin"
bin_root="$local_root/bin"
entry="$bin_root/opencowork"
for boundary in "$local_root" "$share_root" "$bin_root" "$share_root/opencowork"; do
  [[ ! -L "$boundary" ]] || {
    print -u2 -- "Refusing a symbolic-link install boundary: $boundary"
    exit 1
  }
done
if [[ -e "$entry" || -L "$entry" ]]; then
  [[ -L "$entry" && "$(readlink "$entry")" == "$install_root/opencowork" ]] || {
    print -u2 -- "Refusing to replace an unrelated command entry: $entry"
    exit 1
  }
fi

mkdir -p "$share_root" "$bin_root" "$share_root/opencowork"
stage=$(mktemp -d "$share_root/opencowork/.install.XXXXXX")
backup="$share_root/opencowork/.backup.$$"
entry_stage="$bin_root/.opencowork.$$"
had_previous=0
installed=0

rollback() {
  exit_code=$?
  rm -f -- "$entry_stage"
  if (( exit_code != 0 )); then
    if (( installed == 1 )); then
      rm -rf -- "$install_root"
    fi
    if (( had_previous == 1 )) && [[ -d "$backup" ]]; then
      mv "$backup" "$install_root"
    fi
  fi
  [[ ! -d "$stage" ]] || rm -rf -- "$stage"
  [[ ! -d "$backup" ]] || rm -rf -- "$backup"
  exit $exit_code
}
trap rollback EXIT INT TERM

cp -R "$script_directory"/. "$stage"/
chmod +x "$stage/opencowork" "$stage/install.zsh" "$stage/uninstall.zsh"
"$stage/opencowork" --version >/dev/null

if [[ -d "$install_root" ]]; then
  mv "$install_root" "$backup"
  had_previous=1
fi
if [[ ${OPENCOWORK_INSTALL_TEST_FAILPOINT:-} == "after-backup" ]]; then
  print -u2 -- "Injected install failure after backup."
  false
fi
mv "$stage" "$install_root"
installed=1
ln -s "$install_root/opencowork" "$entry_stage"
mv -f "$entry_stage" "$entry"
if (( had_previous == 1 )); then
  rm -rf -- "$backup"
fi
installed=0
trap - EXIT INT TERM

print -r -- "Installed Unsigned OpenCoWork to $install_root"
case ":${PATH:-}:" in
  *":$bin_root:"*) ;;
  *) print -r -- "Add $bin_root to PATH to run opencowork directly." ;;
esac
