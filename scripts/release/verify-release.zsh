#!/bin/zsh

emulate -L zsh
set -euo pipefail

usage() {
  print -u2 -- "Usage: verify-release.zsh --directory <directory> --version <version>"
  exit 2
}

directory=
version=
while (( $# > 0 )); do
  case "$1" in
    --directory|--version)
      (( $# >= 2 )) || usage
      [[ "$1" == "--directory" ]] && directory=$2 || version=$2
      shift 2
      ;;
    *) usage ;;
  esac
done
[[ -n "$directory" && -n "$version" ]] || usage
directory=${directory:A}
checksum_file="$directory/SHA256SUMS"
[[ -f "$checksum_file" ]] || {
  print -u2 -- "SHA256SUMS is missing."
  exit 1
}

work_directory=$(mktemp -d "${TMPDIR:-/tmp}/opencowork-verify.XXXXXX")
trap 'rm -rf -- "$work_directory"' EXIT INT TERM
typeset -A seen
archive_count=0
while IFS= read -r line; do
  checksum=${line%% *}
  file_name=${line#"$checksum  "}
  print -r -- "$checksum" | grep -Eq '^[0-9a-f]{64}$' || {
    print -u2 -- "Invalid checksum entry."
    exit 1
  }
  [[ "$line" == "$checksum  $file_name" && -n "$file_name" && "$file_name" == "${file_name:t}" ]] || {
    print -u2 -- "Invalid checksum file name."
    exit 1
  }
  [[ "$file_name" == opencowork-"$version"-*.tar.gz || \
     "$file_name" == opencowork-"$version"-*.zip || \
     "$file_name" == opencowork-"$version"-*.spdx ]] || {
    print -u2 -- "Unexpected checksum artifact: $file_name"
    exit 1
  }
  [[ -z ${seen[$file_name]-} ]] || {
    print -u2 -- "Duplicate checksum entry: $file_name"
    exit 1
  }
  seen[$file_name]=1
  [[ -f "$directory/$file_name" ]] || {
    print -u2 -- "Release artifact is missing: $file_name"
    exit 1
  }
  actual=$(shasum -a 256 "$directory/$file_name" | awk '{print $1}')
  [[ "$actual" == "$checksum" ]] || {
    print -u2 -- "Checksum mismatch: $file_name"
    exit 1
  }
done < "$checksum_file"

for archive in \
  "$directory"/opencowork-"$version"-*.tar.gz(N) \
  "$directory"/opencowork-"$version"-*.zip(N); do
  (( ++archive_count ))
  name=${archive:t}
  rid=${name#opencowork-$version-}
  rid=${rid%.tar.gz}
  rid=${rid%.zip}
  [[ "$rid" == "osx-arm64" || "$rid" == "win-x64" ]] || {
    print -u2 -- "Unexpected release RID: $rid"
    exit 1
  }
  [[ -n ${seen[$name]-} && -n ${seen[opencowork-$version-$rid.spdx]-} ]] || {
    print -u2 -- "Archive or SBOM is missing from SHA256SUMS: $name"
    exit 1
  }
  extract="$work_directory/$rid"
  mkdir -p "$extract"
  if [[ "$archive" == *.tar.gz ]]; then
    tar -xzf "$archive" -C "$extract"
  else
    unzip -q "$archive" -d "$extract"
  fi
  [[ -z "$(find "$extract" -type l -print -quit)" ]] || {
    print -u2 -- "Archive contains a symbolic link: $name"
    exit 1
  }
  disallowed=$(find "$extract" -type f \( \
    -iname '*.pdb' -o \
    -iname '*TestClient*' -o \
    -iname '*IntegrationTests*' -o \
    -iname '*.db' -o \
    -iname '*.db-wal' \
    \) -print -quit)
  [[ -z "$disallowed" ]] || {
    print -u2 -- "Archive contains a disallowed file: ${disallowed:t}"
    exit 1
  }
  for required in release-manifest.json UNSIGNED.txt SBOM.spdx INSTALL.md INTEGRATIONS.md RELEASE-NOTES.md LICENSE; do
    [[ -f "$extract/$required" ]] || {
      print -u2 -- "Archive is missing $required: $name"
      exit 1
    }
  done
  grep -Fq '"unsigned": true' "$extract/release-manifest.json"
  grep -Fq '"product": "OpenCoWork"' "$extract/release-manifest.json"
  grep -Fq '"version": "'$version'"' "$extract/release-manifest.json"
  grep -Fq '"runtimeIdentifier": "'$rid'"' "$extract/release-manifest.json"
  grep -Eq '"commit": "[0-9a-f]{40}"' "$extract/release-manifest.json"
  grep -Fq 'OpenCoWork Runtime 1.0 package: Unsigned' "$extract/UNSIGNED.txt"
  grep -Fq 'SPDXVersion: SPDX-2.3' "$extract/SBOM.spdx"
  cmp -s "$extract/SBOM.spdx" "$directory/opencowork-$version-$rid.spdx" || {
    print -u2 -- "Internal and external SBOM differ: $name"
    exit 1
  }
  if [[ "$rid" == "osx-arm64" ]]; then
    [[ -x "$extract/opencowork" && -x "$extract/install.zsh" && -x "$extract/uninstall.zsh" ]]
  else
    [[ -f "$extract/opencowork.exe" && -f "$extract/install.ps1" && -f "$extract/uninstall.ps1" ]]
  fi
done
(( archive_count > 0 )) || {
  print -u2 -- "No release archives were found."
  exit 1
}
print -r -- "verified=$archive_count"
