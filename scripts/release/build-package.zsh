#!/bin/zsh

emulate -L zsh
set -euo pipefail

usage() {
  print -u2 -- "Usage: build-package.zsh --version <version> --commit <sha> --rid <osx-arm64|win-x64> --output <directory> [--publish-directory <directory> --fixture]"
  exit 2
}

script_directory=${0:A:h}
repository_root=${script_directory:h:h}
version=
commit=
rid=
output_directory=
publish_directory=
fixture=0

while (( $# > 0 )); do
  case "$1" in
    --version|--commit|--rid|--output|--publish-directory)
      (( $# >= 2 )) || usage
      case "$1" in
        --version) version=$2 ;;
        --commit) commit=$2 ;;
        --rid) rid=$2 ;;
        --output) output_directory=$2 ;;
        --publish-directory) publish_directory=$2 ;;
      esac
      shift 2
      ;;
    --fixture)
      fixture=1
      shift
      ;;
    *) usage ;;
  esac
done

[[ -n "$version" && -n "$commit" && -n "$rid" && -n "$output_directory" ]] || usage
print -r -- "$version" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$' || usage
print -r -- "$commit" | grep -Eq '^[0-9a-f]{40}$' || usage
[[ "$rid" == "osx-arm64" || "$rid" == "win-x64" ]] || usage
if [[ -n "$publish_directory" && $fixture -ne 1 ]]; then
  usage
fi
if [[ -z "$publish_directory" && $fixture -eq 1 ]]; then
  usage
fi

repository_version=$(dotnet msbuild \
  "$repository_root/src/OpenCoWork.App/OpenCoWork.App.csproj" \
  -nologo -getProperty:Version)
[[ "$repository_version" == "$version" ]] || {
  print -u2 -- "Version mismatch: repository=$repository_version requested=$version"
  exit 1
}

work_directory=$(mktemp -d "${TMPDIR:-/tmp}/opencowork-package.XXXXXX")
trap 'rm -rf -- "$work_directory"' EXIT INT TERM

if [[ -z "$publish_directory" ]]; then
  [[ "$(git -C "$repository_root" rev-parse HEAD)" == "$commit" ]] || {
    print -u2 -- "Commit does not match the current HEAD."
    exit 1
  }
  [[ -z "$(git -C "$repository_root" status --porcelain --untracked-files=all)" ]] || {
    print -u2 -- "Release packages require a clean worktree."
    exit 1
  }
  publish_directory="$work_directory/publish"
  dotnet publish "$repository_root/src/OpenCoWork.App/OpenCoWork.App.csproj" \
    -c Release -r "$rid" --self-contained true \
    -p:SourceRevisionId="$commit" -p:DebugType=None -p:DebugSymbols=false \
    -o "$publish_directory"
fi

publish_directory=${publish_directory:A}
[[ -d "$publish_directory" ]] || {
  print -u2 -- "Publish directory does not exist."
  exit 1
}

executable=opencowork
[[ "$rid" == "win-x64" ]] && executable=opencowork.exe
[[ -f "$publish_directory/$executable" ]] || {
  print -u2 -- "Published executable is missing: $executable"
  exit 1
}
if (( fixture == 0 )); then
  if [[ "$rid" == "osx-arm64" && "$(uname -s)" == "Darwin" && "$(uname -m)" == "arm64" ]]; then
    actual_version=$("$publish_directory/$executable" --version)
    [[ "$actual_version" == "opencowork $version" ]] || {
      print -u2 -- "Published version mismatch: $actual_version"
      exit 1
    }
  fi
fi

stage="$work_directory/stage"
mkdir -p "$stage" "$output_directory"
cp -R "$publish_directory"/. "$stage"/
[[ -z "$(find "$stage" -type l -print -quit)" ]] || {
  print -u2 -- "Publish directory contains a symbolic link."
  exit 1
}
find "$stage" -type f -name '*.pdb' -delete
disallowed=$(find "$stage" -type f \( \
  -iname '*TestClient*' -o \
  -iname '*IntegrationTests*' -o \
  -iname '*TestResults*' -o \
  -iname '*.db' -o \
  -iname '*.db-wal' -o \
  -iname '.DS_Store' \
  \) -print -quit)
[[ -z "$disallowed" ]] || {
  print -u2 -- "Disallowed release file: ${disallowed:t}"
  exit 1
}
canary=${OPENCOWORK_VALIDATION_SECRET_CANARY:-}
if [[ -n "$canary" ]] && LC_ALL=C grep -R -F -l -- "$canary" "$stage" >/dev/null 2>&1; then
  print -u2 -- "Release package contains the secret canary."
  exit 1
fi

cp "$repository_root/LICENSE" "$stage/LICENSE"
cp "$repository_root/docs/getting-started.md" "$stage/INSTALL.md"
cp "$repository_root/docs/integration-guide.md" "$stage/INTEGRATIONS.md"
cp "$repository_root/docs/release-notes.md" "$stage/RELEASE-NOTES.md"
if [[ "$rid" == "osx-arm64" ]]; then
  cp "$script_directory/install.zsh" "$stage/install.zsh"
  cp "$script_directory/uninstall.zsh" "$stage/uninstall.zsh"
  chmod +x "$stage/$executable" "$stage/install.zsh" "$stage/uninstall.zsh"
else
  cp "$script_directory/install.ps1" "$stage/install.ps1"
  cp "$script_directory/uninstall.ps1" "$stage/uninstall.ps1"
fi

printf '%s\n' \
  'OpenCoWork Runtime 1.0 package: Unsigned' \
  'This package is not code-signed or notarized.' \
  'Verify SHA256SUMS before making a local operating-system trust decision.' \
  > "$stage/UNSIGNED.txt"
printf '{\n  "schemaVersion": 1,\n  "product": "OpenCoWork",\n  "version": "%s",\n  "commit": "%s",\n  "runtimeIdentifier": "%s",\n  "unsigned": true\n}\n' \
  "$version" "$commit" "$rid" > "$stage/release-manifest.json"

artifact_base="opencowork-$version-$rid"
sbom="$output_directory/$artifact_base.spdx"
created=$(date -u '+%Y-%m-%dT%H:%M:%SZ')
verification_input="$work_directory/package-verification"
while IFS= read -r relative; do
  shasum -a 1 "$stage/$relative" | awk '{print $1}'
done < <(cd "$stage" && find . -type f -print | sed 's#^\./##' | LC_ALL=C sort) | \
  LC_ALL=C sort | tr -d '\n' > "$verification_input"
package_verification=$(shasum -a 1 "$verification_input" | awk '{print $1}')
{
  print -r -- 'SPDXVersion: SPDX-2.3'
  print -r -- 'DataLicense: CC0-1.0'
  print -r -- 'SPDXID: SPDXRef-DOCUMENT'
  print -r -- "DocumentName: $artifact_base"
  print -r -- "DocumentNamespace: https://spdx.org/spdxdocs/$artifact_base-$commit"
  print -r -- 'Creator: Organization: OpenCoWork'
  print -r -- 'Creator: Tool: scripts/release/build-package.zsh'
  print -r -- "Created: $created"
  print
  print -r -- "PackageName: OpenCoWork $rid"
  print -r -- 'SPDXID: SPDXRef-Package'
  print -r -- "PackageVersion: $version"
  print -r -- 'PackageDownloadLocation: NOASSERTION'
  print -r -- 'FilesAnalyzed: true'
  print -r -- "PackageVerificationCode: $package_verification"
  print -r -- 'PackageVerificationCodeExcludedFile: ./SBOM.spdx'
  print -r -- 'PackageLicenseConcluded: NOASSERTION'
  print -r -- 'PackageLicenseDeclared: NOASSERTION'
  print -r -- 'PackageCopyrightText: NOASSERTION'
  print -r -- "PackageComment: Unsigned self-contained runtime from commit $commit."
  print -r -- 'Relationship: SPDXRef-DOCUMENT DESCRIBES SPDXRef-Package'

  index=0
  while IFS= read -r relative; do
    (( ++index ))
    checksum=$(shasum -a 256 "$stage/$relative" | awk '{print $1}')
    print
    print -r -- "FileName: ./$relative"
    print -r -- "SPDXID: SPDXRef-File-$index"
    print -r -- "FileChecksum: SHA256: $checksum"
    print -r -- 'LicenseConcluded: NOASSERTION'
    print -r -- 'LicenseInfoInFile: NOASSERTION'
    print -r -- 'FileCopyrightText: NOASSERTION'
    print -r -- "Relationship: SPDXRef-Package CONTAINS SPDXRef-File-$index"
  done < <(cd "$stage" && find . -type f -print | sed 's#^\./##' | LC_ALL=C sort)
} > "$sbom"
cp "$sbom" "$stage/SBOM.spdx"

if [[ "$rid" == "osx-arm64" ]]; then
  archive="$output_directory/$artifact_base.tar.gz"
  rm -f -- "$archive"
  COPYFILE_DISABLE=1 tar -czf "$archive" -C "$stage" .
else
  archive="$output_directory/$artifact_base.zip"
  rm -f -- "$archive"
  (cd "$stage" && zip -q -r "$archive" .)
fi

checksum_file="$output_directory/SHA256SUMS"
checksum_temp="$work_directory/SHA256SUMS"
for artifact in \
  "$output_directory"/opencowork-"$version"-*.tar.gz(N) \
  "$output_directory"/opencowork-"$version"-*.zip(N) \
  "$output_directory"/opencowork-"$version"-*.spdx(N); do
  checksum=$(shasum -a 256 "$artifact" | awk '{print $1}')
  print -r -- "$checksum  ${artifact:t}"
done | LC_ALL=C sort -k2 > "$checksum_temp"
mv "$checksum_temp" "$checksum_file"

"$script_directory/verify-release.zsh" --directory "$output_directory" --version "$version"
print -r -- "$archive"
