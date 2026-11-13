#!/usr/bin/env bash
set -euo pipefail

DEFAULT_LEAGUE_ID='37770620-bb3b-4efd-b149-3a4e6737a26b'

usage() {
  cat <<'USAGE'
Usage:
  ./scripts/dev/create-test-player.sh [email] [--display-name NAME] [--league-id GUID]
  ./scripts/dev/create-test-player.sh

Provision a development-only Player through SaturdayScout.Admin. When an email
already belongs to a complete matching account, the command reports it without
changing its password. New-account passwords are entered at the .NET CLI's
non-echoing prompt.

Options:
  --display-name NAME  Display name for a new account (prompted if omitted)
  --league-id GUID     Existing development league (default: 37770620-bb3b-4efd-b149-3a4e6737a26b)
  --help               Show this help

Database connection:
  SATURDAY_SCOUT_DB_CONNECTION  If set, use this Npgsql connection string
                                without prompting. It must target
                                127.0.0.1:55432 / saturday_scout.
  Otherwise, interactively prompt for PostgreSQL username (default postgres)
  and hidden password. The connection is passed only to the child process.

The script explicitly sets Development and local operator approval for the
child process. It does not load secrets from files or print connection data.
USAGE
}

fail() {
  printf 'create-test-player: %s\n' "$1" >&2
  exit 2
}

trim() {
  local value="$1"
  value="${value#"${value%%[![:space:]]*}"}"
  value="${value%"${value##*[![:space:]]}"}"
  printf '%s' "$value"
}

npgsql_quote() {
  local value="$1" escaped='' character index
  for ((index = 0; index < ${#value}; index++)); do
    character="${value:index:1}"
    if [[ "$character" == '"' ]]; then
      escaped+='""'
    else
      escaped+="$character"
    fi
  done
  printf '"%s"' "$escaped"
}

unquote_value() {
  local value="$1" last_index last_character
  if [[ ${#value} -ge 2 ]]; then
    last_index=$((${#value} - 1))
    last_character="${value:last_index:1}"
    if [[ ${value:0:1} == '"' && $last_character == '"' ]] ||
       [[ ${value:0:1} == "'" && $last_character == "'" ]]; then
      value="${value:1:${#value}-2}"
    fi
  fi
  printf '%s' "$value"
}

parse_connection_segment() {
  local item key value
  item="$(trim "$1")"
  [[ -n "$item" ]] || return 0
  [[ "$item" == *=* ]] || { connection_parse_error=1; return 0; }
  key="$(trim "${item%%=*}")"
  value="$(unquote_value "$(trim "${item#*=}")")"
  case "$key" in
    [Hh][Oo][Ss][Tt]|[Ss][Ee][Rr][Vv][Ee][Rr]|[Dd][Aa][Tt][Aa][[:space:]][Ss][Oo][Uu][Rr][Cc][Ee])
      host_count=$((host_count + 1)); connection_host="$value" ;;
    [Pp][Oo][Rr][Tt])
      port_count=$((port_count + 1)); connection_port="$value" ;;
    [Dd][Aa][Tt][Aa][Bb][Aa][Ss][Ee]|[Ii][Nn][Ii][Tt][Ii][Aa][Ll][[:space:]][Cc][Aa][Tt][Aa][Ll][Oo][Gg])
      database_count=$((database_count + 1)); connection_database="$value" ;;
    *) ;;
  esac
}

validate_database_target() {
  local connection character next segment quote index
  connection="$database_connection"
  connection_host=''; connection_port=''; connection_database=''
  host_count=0; port_count=0; database_count=0; connection_parse_error=0
  segment=''; quote=''; index=0

  # Split Npgsql's key/value format on unquoted semicolons so passwords may
  # contain separators without being printed or mistaken for target fields.
  while (( index < ${#connection} )); do
    character="${connection:index:1}"
    if [[ -n "$quote" ]]; then
      segment+="$character"
      if [[ "$quote" == '"' && "$character" == '\' && $((index + 1)) -lt ${#connection} ]]; then
        index=$((index + 1))
        segment+="${connection:index:1}"
      elif [[ "$character" == "$quote" ]]; then
        next="${connection:index+1:1}"
        if [[ "$next" == "$quote" ]]; then
          index=$((index + 1))
          segment+="$next"
        else
          quote=''
        fi
      fi
    else
      case "$character" in
        '"'|"'") quote="$character"; segment+="$character" ;;
        ';') parse_connection_segment "$segment"; segment='' ;;
        *) segment+="$character" ;;
      esac
    fi
    index=$((index + 1))
  done
  [[ -z "$quote" ]] || connection_parse_error=1
  parse_connection_segment "$segment"

  [[ $connection_parse_error -eq 0 ]] || fail 'SATURDAY_SCOUT_DB_CONNECTION is not a valid key/value connection string.'
  [[ $host_count -eq 1 && $port_count -eq 1 && $database_count -eq 1 ]] ||
    fail 'Connection string must contain exactly one host, port, and database setting.'
  [[ "$connection_host" == '127.0.0.1' && "$connection_port" == '55432' &&
     "$connection_database" == 'saturday_scout' ]] ||
    fail 'Refusing non-local or non-development database target; expected 127.0.0.1:55432 / saturday_scout.'
}

email=''
display_name=''
league_id="$DEFAULT_LEAGUE_ID"

while (($# > 0)); do
  case "$1" in
    --help|-h)
      usage
      exit 0
      ;;
    --display-name)
      (($# >= 2)) || fail '--display-name requires a value.'
      display_name="$2"
      shift 2
      ;;
    --league-id)
      (($# >= 2)) || fail '--league-id requires a GUID.'
      league_id="$2"
      shift 2
      ;;
    --*)
      fail "Unknown option: $1"
      ;;
    *)
      [[ -z "$email" ]] || fail 'Specify only one email address.'
      email="$1"
      shift
      ;;
  esac
done

if [[ -z "$email" ]]; then
  [[ -t 0 ]] || fail 'Email is required when running non-interactively.'
  read -r -p 'Player email: ' email
fi

email_pattern='^[[:alnum:]._%+\-]+@[[:alnum:]]([[:alnum:]-]*[[:alnum:]])?([.][[:alnum:]]([[:alnum:]-]*[[:alnum:]])?)+$'
[[ ${#email} -le 256 && "$email" =~ $email_pattern ]] || fail 'Invalid email address.'

league_pattern='^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}$'
[[ "$league_id" =~ $league_pattern ]] || fail '--league-id must be a GUID.'

if [[ -z "$display_name" ]]; then
  [[ -t 0 ]] || fail '--display-name is required when running non-interactively.'
  read -r -p 'Player display name: ' display_name
fi
[[ -n "$(trim "$display_name")" && ${#display_name} -le 120 ]] ||
  fail 'Display name must contain 1 to 120 characters.'

database_connection="${SATURDAY_SCOUT_DB_CONNECTION:-}"
if [[ -z "$database_connection" ]]; then
  [[ -r /dev/tty && -w /dev/tty ]] ||
    fail 'SATURDAY_SCOUT_DB_CONNECTION is not set and no interactive terminal is available for PostgreSQL credentials.'

  printf 'PostgreSQL username [postgres]: ' >/dev/tty
  IFS= read -r database_username </dev/tty || fail 'Could not read PostgreSQL username.'
  database_username="$(trim "$database_username")"
  [[ -n "$database_username" ]] || database_username='postgres'

  printf 'PostgreSQL password: ' >/dev/tty
  IFS= read -r -s database_password </dev/tty || fail 'Could not read PostgreSQL password.'
  printf '\n' >/dev/tty
  [[ -n "$database_password" ]] || fail 'PostgreSQL password must not be empty.'

  database_connection="Host=127.0.0.1;Port=55432;Database=saturday_scout;Username=$(npgsql_quote "$database_username");Password=$(npgsql_quote "$database_password")"
  unset database_password database_username
fi
validate_database_target

command -v dotnet >/dev/null 2>&1 || fail 'dotnet was not found on PATH.'

script_dir="$(cd -P "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -P "$script_dir/../.." && pwd)"
admin_project="$repo_root/src/SaturdayScout.Admin/SaturdayScout.Admin.csproj"
[[ -f "$admin_project" ]] || fail 'Could not locate the SaturdayScout.Admin project from this script.'

printf 'Provisioning development Player for %s in league %s.\n' "$email" "$league_id"
if (
  export ASPNETCORE_ENVIRONMENT=Development
  export SATURDAY_SCOUT_DEV_PLAYER_APPROVED=yes
  export SATURDAY_SCOUT_DB_CONNECTION="$database_connection"
  dotnet run --project "$admin_project" -- \
    provision-dev-player \
    --email "$email" \
    --display-name "$display_name" \
    --league-id "$league_id" \
    --confirm-owned-email \
    --confirm-operator-approval
); then
  printf 'Development Player provisioning command completed.\n'
else
  status=$?
  printf 'Development Player provisioning failed (exit %s).\n' "$status" >&2
  exit "$status"
fi
