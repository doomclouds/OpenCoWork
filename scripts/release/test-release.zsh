#!/bin/zsh

emulate -L zsh
set -euo pipefail

script_directory=${0:A:h}
repository_root=${script_directory:h:h}
version=$(dotnet msbuild \
  "$repository_root/src/OpenCoWork.App/OpenCoWork.App.csproj" \
  -nologo -getProperty:Version)
commit=$(git -C "$repository_root" rev-parse HEAD)
work_directory=$(mktemp -d "${TMPDIR:-/tmp}/opencowork release test.XXXXXX")
trap 'rm -rf -- "$work_directory"' EXIT INT TERM

fail() {
  print -u2 -- "$1"
  exit 1
}

mac_publish="$work_directory/mac publish"
windows_publish="$work_directory/windows publish"
output_directory="$work_directory/release output"
mkdir -p "$mac_publish" "$windows_publish" "$output_directory"
printf '#!/bin/zsh\nprint -r -- "%s"\n' "$version" > "$mac_publish/opencowork"
chmod +x "$mac_publish/opencowork"
print -r -- 'fixture pdb' > "$mac_publish/OpenCoWork.App.pdb"
print -r -- 'fixture windows executable' > "$windows_publish/opencowork.exe"
print -r -- 'fixture pdb' > "$windows_publish/OpenCoWork.App.pdb"

"$script_directory/build-package.zsh" \
  --version "$version" --commit "$commit" --rid osx-arm64 \
  --output "$output_directory" --publish-directory "$mac_publish" --fixture \
  >/dev/null
"$script_directory/build-package.zsh" \
  --version "$version" --commit "$commit" --rid win-x64 \
  --output "$output_directory" --publish-directory "$windows_publish" --fixture \
  >/dev/null
"$script_directory/build-package.zsh" \
  --version "$version" --commit "$commit" --rid osx-arm64 \
  --output "$output_directory" --publish-directory "$mac_publish" --fixture \
  >/dev/null
"$script_directory/verify-release.zsh" \
  --directory "$output_directory" --version "$version" >/dev/null

[[ $(wc -l < "$output_directory/SHA256SUMS" | tr -d ' ') == 4 ]] || \
  fail 'SHA256SUMS is incomplete or contains duplicate entries.'
cp "$output_directory/SHA256SUMS" "$work_directory/SHA256SUMS.saved"
sed -i '' '/osx-arm64\.tar\.gz$/d' "$output_directory/SHA256SUMS"
if "$script_directory/verify-release.zsh" \
    --directory "$output_directory" --version "$version" >/dev/null 2>&1; then
  fail 'An incomplete SHA256SUMS was accepted.'
fi
mv "$work_directory/SHA256SUMS.saved" "$output_directory/SHA256SUMS"
if unzip -Z1 "$output_directory/opencowork-$version-win-x64.zip" | \
    grep -Eq '(\.pdb$|TestClient|IntegrationTests)'; then
  fail 'Windows package contains test or PDB files.'
fi
if tar -tzf "$output_directory/opencowork-$version-osx-arm64.tar.gz" | \
    grep -Eq '(\.pdb$|TestClient|IntegrationTests)'; then
  fail 'macOS package contains test or PDB files.'
fi

print -r -- 'tamper' >> "$output_directory/opencowork-$version-osx-arm64.tar.gz"
if "$script_directory/verify-release.zsh" \
    --directory "$output_directory" --version "$version" >/dev/null 2>&1; then
  fail 'A checksum mismatch was accepted.'
fi
"$script_directory/build-package.zsh" \
  --version "$version" --commit "$commit" --rid osx-arm64 \
  --output "$output_directory" --publish-directory "$mac_publish" --fixture \
  >/dev/null

missing_publish="$work_directory/missing publish"
mkdir -p "$missing_publish"
if "$script_directory/build-package.zsh" \
    --version "$version" --commit "$commit" --rid osx-arm64 \
    --output "$work_directory/missing output" \
    --publish-directory "$missing_publish" --fixture >/dev/null 2>&1; then
  fail 'A missing executable was accepted.'
fi

print -r -- 'test client' > "$mac_publish/OpenCoWork.Protocol.TestClient.dll"
if "$script_directory/build-package.zsh" \
    --version "$version" --commit "$commit" --rid osx-arm64 \
    --output "$work_directory/disallowed output" \
    --publish-directory "$mac_publish" --fixture >/dev/null 2>&1; then
  fail 'A TestClient file was accepted.'
fi
rm "$mac_publish/OpenCoWork.Protocol.TestClient.dll"

canary='m11-release-secret-canary'
print -r -- "$canary" > "$mac_publish/canary.txt"
if OPENCOWORK_VALIDATION_SECRET_CANARY="$canary" \
    "$script_directory/build-package.zsh" \
    --version "$version" --commit "$commit" --rid osx-arm64 \
    --output "$work_directory/canary output" \
    --publish-directory "$mac_publish" --fixture >/dev/null 2>&1; then
  fail 'A secret canary was accepted.'
fi
rm "$mac_publish/canary.txt"

extracted="$work_directory/extracted package"
home_directory="$work_directory/home with spaces"
mkdir -p "$extracted" "$home_directory"
tar -xzf "$output_directory/opencowork-$version-osx-arm64.tar.gz" -C "$extracted"
HOME="$home_directory" "$extracted/install.zsh" >/dev/null
[[ "$(HOME="$home_directory" "$home_directory/.local/bin/opencowork" --version)" == "$version" ]] || \
  fail 'Installed command returned the wrong version.'
mkdir -p "$home_directory/.opencowork"
print -r -- 'preserve' > "$home_directory/.opencowork/preserve.txt"
HOME="$home_directory" "$extracted/install.zsh" >/dev/null
if HOME="$home_directory" OPENCOWORK_INSTALL_TEST_FAILPOINT=after-backup \
    "$extracted/install.zsh" >/dev/null 2>&1; then
  fail 'The install fault point did not fail.'
fi
[[ "$(HOME="$home_directory" "$home_directory/.local/bin/opencowork" --version)" == "$version" ]] || \
  fail 'Upgrade rollback did not restore the previous install.'
HOME="$home_directory" "$home_directory/.local/share/opencowork/bin/uninstall.zsh" >/dev/null
[[ ! -e "$home_directory/.local/bin/opencowork" ]] || fail 'Uninstall left the command entry.'
[[ -f "$home_directory/.opencowork/preserve.txt" ]] || fail 'Default uninstall removed user data.'
HOME="$home_directory" "$extracted/uninstall.zsh" >/dev/null
if HOME="$home_directory" "$extracted/uninstall.zsh" --purge >/dev/null 2>&1; then
  fail 'Purge without confirmation was accepted.'
fi
[[ -f "$home_directory/.opencowork/preserve.txt" ]] || fail 'Rejected purge changed user data.'
HOME="$home_directory" "$extracted/uninstall.zsh" --purge --confirm-purge >/dev/null
[[ ! -e "$home_directory/.opencowork" ]] || fail 'Confirmed purge did not remove user data.'

boundary_home="$work_directory/boundary home"
outside="$work_directory/outside"
mkdir -p "$boundary_home" "$outside"
ln -s "$outside" "$boundary_home/.local"
if HOME="$boundary_home" "$extracted/install.zsh" >/dev/null 2>&1; then
  fail 'A symbolic-link install boundary was accepted.'
fi
[[ -z "$(find "$outside" -mindepth 1 -print -quit)" ]] || \
  fail 'Boundary refusal wrote outside the user install root.'

print -r -- 'release-fixtures=passed'
