#!/bin/zsh

set -eu

readonly api_key_variable="OPENCOWORK_RELEASE_DEEPSEEK_API_KEY"
readonly base_url_variable="OPENCOWORK_RELEASE_DEEPSEEK_BASE_URL"
readonly base_url="https://api.deepseek.com"

if [[ "${1:-}" == "--clear" ]]; then
  launchctl unsetenv "$api_key_variable"
  launchctl unsetenv "$base_url_variable"
  print "DeepSeek release environment cleared."
  exit 0
fi

if [[ -n "${1:-}" ]]; then
  print -u2 "Usage: $0 [--clear]"
  exit 64
fi

if ! read -r -s "opencowork_deepseek_key?请输入 DeepSeek API Key: "; then
  print
  exit 1
fi
print

if [[ -z "$opencowork_deepseek_key" ]]; then
  print -u2 "API Key cannot be empty."
  exit 1
fi

launchctl setenv "$base_url_variable" "$base_url"
launchctl setenv "$api_key_variable" "$opencowork_deepseek_key"
unset opencowork_deepseek_key

print "DeepSeek release environment is ready for newly started apps."
print "Quit and reopen Codex before validation. Rebooting clears these values."
print "Run '$0 --clear' to remove them sooner."
