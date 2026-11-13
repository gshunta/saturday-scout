#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd -P "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
script="$repo_root/scripts/dev/create-test-player.sh"
temporary_directory="$(mktemp -d "${TMPDIR:-/tmp}/saturday-scout-dev-player-test.XXXXXX")"
trap 'rm -rf "$temporary_directory"' EXIT
mkdir -p "$temporary_directory/bin"

cat > "$temporary_directory/bin/dotnet" <<'STUB'
#!/usr/bin/env bash
set -euo pipefail
[[ "$ASPNETCORE_ENVIRONMENT" == 'Development' ]]
[[ "$SATURDAY_SCOUT_DEV_PLAYER_APPROVED" == 'yes' ]]
[[ "$SATURDAY_SCOUT_DB_CONNECTION" == *'Database=saturday_scout'* ]]
if [[ -n "${STUB_EXPECT_CONNECTION:-}" ]]; then
  [[ "$SATURDAY_SCOUT_DB_CONNECTION" == "$STUB_EXPECT_CONNECTION" ]]
fi
{
  printf 'environment=%s|%s\n' "$ASPNETCORE_ENVIRONMENT" "$SATURDAY_SCOUT_DEV_PLAYER_APPROVED"
  printf 'arguments=%s\n' "$*"
} > "$STUB_CAPTURE"
printf '%s\n' "${STUB_MESSAGE:-Stubbed provisioning completed.}"
exit "${STUB_EXIT_CODE:-0}"
STUB
chmod +x "$temporary_directory/bin/dotnet"

test_connection='Host=127.0.0.1;Port=55432;Database=saturday_scout;Username=test;Password="fixture;value"'
capture="$temporary_directory/capture"

assert_contains() {
  local file="$1" text="$2"
  grep -Fq -- "$text" "$file" || {
    printf 'FAIL: expected captured command to contain %s\n' "$text" >&2
    exit 1
  }
}

assert_rejected_without_child() {
  local expected="$1"
  shift
  rm -f "$capture"
  if output="$(cd / && PATH="$temporary_directory/bin:$PATH" STUB_CAPTURE="$capture" \
      SATURDAY_SCOUT_DB_CONNECTION="${SATURDAY_SCOUT_DB_CONNECTION:-$test_connection}" \
      "$script" "$@" 2>&1)"; then
    printf 'FAIL: expected rejection containing %s\n' "$expected" >&2
    exit 1
  fi
  [[ "$output" == *"$expected"* ]] || {
    printf 'FAIL: rejection did not explain %s\n' "$expected" >&2
    exit 1
  }
  [[ ! -e "$capture" ]] || {
    printf 'FAIL: invalid input invoked dotnet\n' >&2
    exit 1
  }
}

# Help works without database configuration.
PATH="$temporary_directory/bin:$PATH" "$script" --help >/dev/null

# A normal invocation works from outside the checkout and passes only safe CLI
# arguments plus Development-only environment settings to the .NET command.
(
  cd /
  PATH="$temporary_directory/bin:$PATH" \
  STUB_CAPTURE="$capture" \
  STUB_MESSAGE='Development Player is already provisioned; no changes made.' \
  ASPNETCORE_ENVIRONMENT=QA \
  SATURDAY_SCOUT_DEV_PLAYER_APPROVED=no \
  SATURDAY_SCOUT_DB_CONNECTION="$test_connection" \
  "$script" manual-player3@example.test --display-name 'Manual Player Three'
)
assert_contains "$capture" 'environment=Development|yes'
assert_contains "$capture" 'provision-dev-player --email manual-player3@example.test --display-name Manual Player Three --league-id 37770620-bb3b-4efd-b149-3a4e6737a26b --confirm-owned-email --confirm-operator-approval'

# Explicit league IDs are passed through, and the wrapper reports the delegated
# idempotent result without interpreting or changing account state.
(
  cd /
  PATH="$temporary_directory/bin:$PATH" STUB_CAPTURE="$capture" \
  STUB_MESSAGE='Development Player is already provisioned and linked. No changes made.' \
  SATURDAY_SCOUT_DB_CONNECTION="$test_connection" \
  "$script" manual-player3@example.test --display-name 'Manual Player Three' \
    --league-id 11111111-2222-3333-4444-555555555555
) > "$temporary_directory/idempotent-output"
assert_contains "$capture" '--league-id 11111111-2222-3333-4444-555555555555'
grep -Fq 'already provisioned and linked' "$temporary_directory/idempotent-output"

# Environment configuration takes precedence and is usable without a terminal,
# so this also proves the script does not prompt when the variable is present.
(
  cd /
  PATH="$temporary_directory/bin:$PATH" STUB_CAPTURE="$capture" \
  SATURDAY_SCOUT_DB_CONNECTION="$test_connection" \
  "$script" manual-player3@example.test --display-name 'Manual Player Three'
) >/dev/null
assert_contains "$capture" 'provision-dev-player'

# Without a configured connection, non-interactive execution fails safely.
rm -f "$capture"
if output="$(cd / && PATH="$temporary_directory/bin:$PATH" STUB_CAPTURE="$capture" \
    SATURDAY_SCOUT_DB_CONNECTION='' "$script" manual-player3@example.test \
    --display-name 'Manual Player Three' 2>&1)"; then
  printf 'FAIL: missing database configuration was accepted non-interactively\n' >&2
  exit 1
fi
[[ "$output" == *'no interactive terminal is available'* && ! -e "$capture" ]]

# Invalid email, non-local target, and missing league value
# must all fail before the provisioning command is invoked.
assert_rejected_without_child 'Invalid email address.' 'not-an-email' --display-name 'Manual Player Three'

rm -f "$capture"
if output="$(cd / && PATH="$temporary_directory/bin:$PATH" STUB_CAPTURE="$capture" \
    SATURDAY_SCOUT_DB_CONNECTION='Host=127.0.0.1;Port=5432;Database=saturday_scout;Username=test;Password=fake' \
    "$script" manual-player3@example.test --display-name 'Manual Player Three' 2>&1)"; then
  printf 'FAIL: incorrect database target was accepted\n' >&2
  exit 1
fi
[[ "$output" == *'Refusing non-local or non-development database target'* && ! -e "$capture" ]]

rm -f "$capture"
if output="$(cd / && PATH="$temporary_directory/bin:$PATH" STUB_CAPTURE="$capture" \
    SATURDAY_SCOUT_DB_CONNECTION='Host=127.0.0.1;Port=55432;Database=saturday_scout_qa;Username=test;Password=fake' \
    "$script" manual-player3@example.test --display-name 'Manual Player Three' 2>&1)"; then
  printf 'FAIL: non-development database name was accepted\n' >&2
  exit 1
fi
[[ "$output" == *'Refusing non-local or non-development database target'* && ! -e "$capture" ]]

rm -f "$capture"
if output="$(cd / && PATH="$temporary_directory/bin:$PATH" STUB_CAPTURE="$capture" \
    SATURDAY_SCOUT_DB_CONNECTION="$test_connection" "$script" manual-player3@example.test \
    --display-name 'Manual Player Three' --league-id 2>&1)"; then
  printf 'FAIL: missing league ID value was accepted\n' >&2
  exit 1
fi
[[ "$output" == *'--league-id requires a GUID'* && ! -e "$capture" ]]

# Exercise credential prompts through a pseudo-terminal. The harness checks
# terminal echo is disabled at the password prompt and the .NET child receives
# the expected Npgsql-quoted values without printing them.
python3 - "$script" "$temporary_directory/bin" "$capture" <<'PY'
import os
import pty
import select
import signal
import sys
import termios
import time

script, stub_bin, capture = sys.argv[1:]

def quote(value):
    return '"' + value.replace('"', '""') + '"'

def run_prompt_case(username_input, username_value, password):
    expected = (
        'Host=127.0.0.1;Port=55432;Database=saturday_scout;Username='
        + quote(username_value) + ';Password=' + quote(password)
    )
    pid, master = pty.fork()
    if pid == 0:
        os.chdir('/')
        env = os.environ.copy()
        env['PATH'] = stub_bin + os.pathsep + env.get('PATH', '')
        env['STUB_CAPTURE'] = capture
        env['STUB_EXPECT_CONNECTION'] = expected
        env.pop('SATURDAY_SCOUT_DB_CONNECTION', None)
        os.execve(script, [script, 'manual-player3@example.test', '--display-name', 'Manual Player Three'], env)

    output = bytearray()
    def until(marker):
        deadline = time.monotonic() + 8
        while marker not in output and time.monotonic() < deadline:
            ready, _, _ = select.select([master], [], [], 0.2)
            if ready:
                try:
                    output.extend(os.read(master, 4096))
                except OSError:
                    break
        if marker not in output:
            raise AssertionError('did not see expected credential prompt')

    try:
        until(b'PostgreSQL username [postgres]: ')
        os.write(master, username_input)
        until(b'PostgreSQL password: ')
        deadline = time.monotonic() + 3
        echo_disabled = False
        while time.monotonic() < deadline:
            if not (termios.tcgetattr(master)[3] & termios.ECHO):
                echo_disabled = True
                break
            time.sleep(0.02)
        if not echo_disabled:
            raise AssertionError('terminal echo remained enabled at password prompt')
        os.write(master, password.encode() + b'\n')
        deadline = time.monotonic() + 10
        status = None
        while time.monotonic() < deadline:
            ready, _, _ = select.select([master], [], [], 0.2)
            if ready:
                try:
                    output.extend(os.read(master, 4096))
                except OSError:
                    pass
            done, status = os.waitpid(pid, os.WNOHANG)
            if done:
                pid = -1
                break
        if pid != -1:
            os.kill(pid, signal.SIGKILL)
            os.waitpid(pid, 0)
            raise AssertionError('script did not finish after credential input')
        if not os.WIFEXITED(status) or os.WEXITSTATUS(status) != 0:
            raise AssertionError('credential prompt invocation failed')
        if password.encode() in output:
            raise AssertionError('password was echoed to the terminal')
        with open(capture, encoding='utf-8') as result:
            if 'provision-dev-player' not in result.read():
                raise AssertionError('provisioning child was not invoked')
    finally:
        os.close(master)

run_prompt_case(b'\n', 'postgres', 'pg;pass="sample"')
run_prompt_case(b'user; "name"=x\n', 'user; "name"=x', 'p;ass"word=ok')
print('Credential prompt, hidden entry, default username, and Npgsql escaping tests passed.')
PY

printf 'All create-test-player script tests passed.\n'
