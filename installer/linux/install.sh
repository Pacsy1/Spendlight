#!/usr/bin/env bash
# Spendlight — Linux installer.
#
# This file is a self-extracting archive: a bash script with the app appended after the
# __PAYLOAD_BELOW__ line. Run it:
#
#   bash Spendlight-Linux-<version>.run            interactive (terminal or desktop dialogs)
#   bash Spendlight-Linux-<version>.run --yes      unattended, for your account
#   sudo bash Spendlight-Linux-<version>.run --system   for every user on this machine
#   bash Spendlight-Linux-<version>.run --uninstall
#
# It installs a single self-contained binary (no .NET or other runtime needed), a menu
# entry and icon, the `spendlight` command and a `spendlight-uninstall` command.
#
# Copyright (C) 2026 Pacsy1. Free software: GNU GPL v3 or later (see LICENSE).
# Source: https://github.com/Pacsy1/spendlight

if [ -z "${BASH_VERSION:-}" ]; then exec bash "$0" "$@"; fi
set -euo pipefail

APP_NAME="Spendlight"
APP_ID="spendlight"
VERSION="@VERSION@"
PAYLOAD_SHA256="@SHA256@"
INSTALLED_MB="@SIZE_MB@"
SELF="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/$(basename "${BASH_SOURCE[0]}")"

# ─────────────────────────────── options ───────────────────────────────

ORIG_ARGS=("$@")
ASSUME_YES=0; SYSTEM=0; UNINSTALL=0; LAUNCH=ask; MENU=1; UI=auto; PREFIX=""; EXTRACT=""

usage() {
  cat <<EOF
$APP_NAME $VERSION — installer

Usage: bash $(basename "$SELF") [options]

  -y, --yes          Don't ask questions; use the defaults
      --system       Install for all users (/opt, /usr/local/bin) — run with sudo
      --prefix DIR   Install the app into DIR instead of the default location
      --no-menu      Don't add a menu entry or icon
      --no-launch    Don't offer to open the app afterwards
      --launch       Open the app afterwards without asking
      --gui          Use desktop dialogs (needs zenity)
      --text         Use the terminal, even from a desktop
      --uninstall    Remove $APP_NAME
      --extract DIR  Just unpack the files into DIR
  -h, --help         Show this help
EOF
}

while [ $# -gt 0 ]; do
  case "$1" in
    -y|--yes) ASSUME_YES=1 ;;
    --system) SYSTEM=1 ;;
    --prefix) PREFIX="${2:?--prefix needs a folder}"; shift ;;
    --no-menu) MENU=0 ;;
    --no-launch) LAUNCH=no ;;
    --launch) LAUNCH=yes ;;
    --gui) UI=gui ;;
    --text) UI=text ;;
    --uninstall) UNINSTALL=1 ;;
    --extract) EXTRACT="${2:?--extract needs a folder}"; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown option: $1 (try --help)" >&2; exit 2 ;;
  esac
  shift
done

# ─────────────────────────────── look & feel ───────────────────────────────

IS_TTY=0; { [ -t 1 ] || [ -n "${SPENDLIGHT_FORCE_TTY:-}" ]; } && IS_TTY=1
COLOR=0; [ "$IS_TTY" = 1 ] && [ -z "${NO_COLOR:-}" ] && [ "${TERM:-dumb}" != dumb ] && COLOR=1
UTF=0
case "${LC_ALL:-${LC_CTYPE:-${LANG:-}}}" in *[Uu][Tt][Ff]-8*|*[Uu][Tt][Ff]8*) UTF=1 ;; esac
TRUECOLOR=0; case "${COLORTERM:-}" in truecolor|24bit) TRUECOLOR=1 ;; esac

# rgb R G B 256-color-fallback
rgb() { if [ "$COLOR" = 0 ]; then printf ''; elif [ "$TRUECOLOR" = 1 ]; then printf '\033[38;2;%s;%s;%sm' "$1" "$2" "$3"; else printf '\033[38;5;%sm' "$4"; fi; }
if [ "$COLOR" = 1 ]; then
  B=$'\033[1m'; D=$'\033[2m'; R=$'\033[0m'
else
  B=""; D=""; R=""
fi
BLUE=$(rgb 57 135 229 33); ORANGE=$(rgb 235 104 52 202); AQUA=$(rgb 27 175 122 36)
YELLOW=$(rgb 237 161 0 214); GREEN=$(rgb 12 163 12 34); RED=$(rgb 227 73 72 167)
MUTED=$(rgb 137 135 129 245); INK=$(rgb 230 229 224 254)

if [ "$UTF" = 1 ]; then
  OK="✓"; BAD="✗"; WARN="!"; DOT="·"; ARROW="›"; SPIN=(⠋ ⠙ ⠹ ⠸ ⠼ ⠴ ⠦ ⠧ ⠇ ⠏)
  TL="╭"; TR="╮"; BL="╰"; BR="╯"; H="─"; V="│"; BAR="━"
else
  OK="+"; BAD="x"; WARN="!"; DOT="-"; ARROW=">"; SPIN=('|' '/' '-' '\')
  TL="+"; TR="+"; BL="+"; BR="+"; H="-"; V="|"; BAR="="
fi

W=62   # inner width of boxes
repeat() { local s="" i; for ((i = 0; i < $2; i++)); do s+="$1"; done; printf '%s' "$s"; }
strip() { printf '%s' "$1" | sed $'s/\033\\[[0-9;]*m//g'; }
# box_line "text with colors" — pads to the box width using the visible length
box_line() { local vis; vis=$(strip "$1"); printf '  %s%s%s %s%*s %s%s%s\n' "$MUTED" "$V" "$R" "$1" $((W - ${#vis} - 2)) "" "$MUTED" "$V" "$R"; }
box_top() { printf '  %s%s%s%s%s\n' "$MUTED" "$TL" "$(repeat "$H" "$W")" "$TR" "$R"; }
box_bottom() { printf '  %s%s%s%s%s\n' "$MUTED" "$BL" "$(repeat "$H" "$W")" "$BR" "$R"; }

banner() {
  [ "$IS_TTY" = 1 ] && printf '\n'
  box_top
  box_line ""
  if [ "$UTF" = 1 ]; then
    box_line "  ${BLUE}▂▄${ORANGE}▃${BLUE}▆${AQUA}▅${BLUE}█${R}   ${B}${INK}${APP_NAME}${R}"
  else
    box_line "  ${BLUE}.:|${ORANGE}|${AQUA}|${R}   ${B}${APP_NAME}${R}"
  fi
  box_line "          ${MUTED}$1 ${DOT} version ${VERSION}${R}"
  box_line ""
  box_bottom
  printf '\n'
}
section() { printf '\n  %s%s%s\n' "$B" "$1" "$R"; }
row() { printf '    %s%-11s%s %s\n' "$MUTED" "$1" "$R" "$2"; }
ok() { printf '    %s%s%s %s\n' "$GREEN" "$OK" "$R" "$1"; }
warn() { printf '    %s%s%s %s\n' "$YELLOW" "$WARN" "$R" "$1"; }
fail() { printf '\n  %s%s %s%s\n\n' "$RED$B" "$BAD" "$1" "$R" >&2; exit 1; }
note() { printf '  %s%s%s\n' "$MUTED" "$1" "$R"; }
plural() { if [ "$1" = 1 ]; then printf '%s' "$2"; else printf '%ss' "$2"; fi; }
tilde() { case "$1" in "$HOME"/*) printf '~/%s' "${1#"$HOME"/}" ;; "$HOME") printf '~' ;; *) printf '%s' "$1" ;; esac; }

ask() {   # ask "Question" default(Y|N)  → returns 0 for yes
  local q="$1" def="$2" hint ans
  if [ "$ASSUME_YES" = 1 ] || [ ! -t 0 ]; then [ "$def" = Y ]; return; fi
  [ "$def" = Y ] && hint="Y/n" || hint="y/N"
  printf '\n  %s%s%s %s %s[%s]%s ' "$BLUE" "$ARROW" "$R" "$q" "$MUTED" "$hint" "$R"
  read -r ans || ans=""
  ans="${ans:-$def}"
  case "$ans" in [Yy]*) return 0 ;; *) return 1 ;; esac
}

# step "Label" command...  — spinner while it runs, ✓ or ✗ after
ERRLOG="$(mktemp 2>/dev/null || echo /tmp/spendlight-install.$$)"
step() {
  local label="$1"; shift
  if [ "$IS_TTY" = 0 ]; then
    if "$@" >>"$ERRLOG" 2>&1; then printf '    %s %s\n' "$OK" "$label"; return 0; fi
    printf '    %s %s\n' "$BAD" "$label"; sed 's/^/      /' "$ERRLOG" >&2; exit 1
  fi
  ( "$@" ) >>"$ERRLOG" 2>&1 &
  local pid=$! i=0
  printf '\033[?25l'
  while kill -0 "$pid" 2>/dev/null; do
    printf '\r    %s%s%s %s' "$BLUE" "${SPIN[i++ % ${#SPIN[@]}]}" "$R" "$label"
    sleep 0.08
  done
  if wait "$pid"; then
    printf '\r    %s%s%s %s\033[K\n' "$GREEN" "$OK" "$R" "$label"
    printf '\033[?25h'
  else
    printf '\r    %s%s%s %s\033[K\n\033[?25h' "$RED" "$BAD" "$R" "$label"
    printf '\n'; sed 's/^/      /' "$ERRLOG" >&2
    fail "Installation failed. Nothing you already had was changed."
  fi
}
cleanup() { if [ "$IS_TTY" = 1 ]; then printf '\033[?25h'; fi; rm -rf "${TMP:-}" "$ERRLOG" 2>/dev/null || true; }
trap cleanup EXIT
trap 'printf "\n"; exit 130' INT

# ─────────────────────────────── where things go ───────────────────────────────

[ "$(id -u)" = 0 ] && [ "$SYSTEM" = 0 ] && [ -z "${SUDO_USER:-}" ] && SYSTEM=1   # a real root shell: install system-wide
if [ "$SYSTEM" = 1 ]; then
  [ "$(id -u)" = 0 ] || fail "Installing for all users needs root. Run:  sudo bash $(basename "$SELF") --system"
  APPDIR="${PREFIX:-/opt/$APP_ID}"; BINDIR=/usr/local/bin
  APPS=/usr/share/applications; ICONS=/usr/share/icons/hicolor
  SCOPE="everyone on this computer"
else
  DATA="${XDG_DATA_HOME:-$HOME/.local/share}"
  APPDIR="${PREFIX:-$DATA/$APP_ID}"; BINDIR="$HOME/.local/bin"
  APPS="$DATA/applications"; ICONS="$DATA/icons/hicolor"
  SCOPE="your account ($(id -un))"
fi

# The app's earlier name. An install under it is replaced, keeping its saved settings.
LEGACY_NAME="Claude Code Spend"
if [ "$SYSTEM" = 1 ]; then LEGACY_DIR="/opt/claude-spend"; else LEGACY_DIR="$DATA/claude-spend"; fi
HAS_LEGACY=0; [ -f "$LEGACY_DIR/uninstall.sh" ] && HAS_LEGACY=1

# ─────────────────────────────── uninstall ───────────────────────────────

if [ "$UNINSTALL" = 1 ]; then
  if [ -x "$APPDIR/uninstall.sh" ]; then
    args=(); [ "$ASSUME_YES" = 1 ] && args+=(--yes)
    exec bash "$APPDIR/uninstall.sh" ${args[@]+"${args[@]}"}
  fi
  fail "$APP_NAME isn't installed in $(tilde "$APPDIR")."
fi

# ─────────────────────────────── system checks ───────────────────────────────

OS="$(uname -s)"
if [ "$OS" != Linux ] && [ -z "${SPENDLIGHT_INSTALLER_TEST:-}" ]; then
  fail "This installer is for Linux (found $OS). On Windows use Spendlight-Setup.exe."
fi
case "$(uname -m)" in
  x86_64|amd64) ARCH=x64; ARCH_LABEL="x86-64" ;;
  aarch64|arm64) ARCH=arm64; ARCH_LABEL="ARM64" ;;
  *) fail "Unsupported processor: $(uname -m). Builds exist for x86-64 and ARM64." ;;
esac
LIBC="glibc"
if command -v ldd >/dev/null 2>&1; then
  if ldd --version 2>&1 | grep -qi musl; then
    fail "This system uses musl libc (e.g. Alpine). $APP_NAME is built for glibc distributions."
  fi
  gv="$(ldd --version 2>/dev/null | head -n1 | grep -oE '[0-9]+\.[0-9]+' | tail -n1 || true)"
  [ -n "$gv" ] && LIBC="glibc $gv"
fi
DISTRO=""
[ -r /etc/os-release ] && DISTRO="$(. /etc/os-release && printf '%s' "${PRETTY_NAME:-${NAME:-}}")"

LOGS="${CLAUDE_CONFIG_DIR:-$HOME/.claude}/projects"
SESSIONS=0; PROJECTS=0
if [ -d "$LOGS" ]; then
  SESSIONS="$(find "$LOGS" -name '*.jsonl' -type f 2>/dev/null | wc -l | tr -d ' ')"
  PROJECTS="$(find "$LOGS" -name '*.jsonl' -type f -exec dirname {} \; 2>/dev/null | sort -u | wc -l | tr -d ' ')"
fi

BROWSER=""
for b in chromium chromium-browser google-chrome google-chrome-stable brave-browser brave microsoft-edge microsoft-edge-stable vivaldi vivaldi-stable; do
  if command -v "$b" >/dev/null 2>&1; then BROWSER="$b"; break; fi
done

PREVIOUS=""; [ -f "$APPDIR/VERSION" ] && PREVIOUS="$(cat "$APPDIR/VERSION" 2>/dev/null || true)"

# ─────────────────────────────── the actual work ───────────────────────────────

TMP=""
verify_payload() {
  command -v sha256sum >/dev/null 2>&1 || return 0
  local line sum
  line=$(awk '/^__PAYLOAD_BELOW__$/ { print NR + 1; exit 0 }' "$SELF")
  sum=$(tail -n +"$line" "$SELF" | sha256sum | cut -d' ' -f1)
  [ "$sum" = "$PAYLOAD_SHA256" ] || { echo "Checksum mismatch: the download is damaged. Download it again."; return 1; }
}
unpack() {
  local line
  line=$(awk '/^__PAYLOAD_BELOW__$/ { print NR + 1; exit 0 }' "$SELF")
  tail -n +"$line" "$SELF" | tar -xzf - -C "$TMP"
  [ -f "$TMP/bin/$ARCH/$APP_ID" ] || { echo "The package has no $ARCH build."; return 1; }
}
stop_running() {
  if command -v pkill >/dev/null 2>&1; then pkill -f "$APPDIR/$APP_ID" 2>/dev/null || true; fi
  sleep 0.3
}
remove_legacy() {
  [ "$HAS_LEGACY" = 1 ] || return 0
  # Its own uninstaller removes its app, commands, menu entry and icon; settings stay.
  bash "$LEGACY_DIR/uninstall.sh" --yes --keep-settings </dev/null >/dev/null 2>&1 || true
  # Hand the old window's saved settings (its browser profile) to Spendlight.
  if [ -d "$LEGACY_DIR/browser" ] && [ ! -e "$APPDIR/browser" ]; then
    mkdir -p "$APPDIR"; mv "$LEGACY_DIR/browser" "$APPDIR/browser"
  fi
  rmdir "$LEGACY_DIR" 2>/dev/null || true
}
install_files() {
  mkdir -p "$APPDIR"
  install -m 0755 "$TMP/bin/$ARCH/$APP_ID" "$APPDIR/$APP_ID.new"
  mv -f "$APPDIR/$APP_ID.new" "$APPDIR/$APP_ID"
  install -m 0644 "$TMP/share/$APP_ID.svg" "$APPDIR/icon.svg"
  if [ -f "$TMP/share/LICENSE" ]; then install -m 0644 "$TMP/share/LICENSE" "$APPDIR/LICENSE"; fi
  printf '%s\n' "$VERSION" > "$APPDIR/VERSION"
}
write_uninstaller() {
  {
    printf '#!/usr/bin/env bash\n# Removes %s. Generated by the installer.\n' "$APP_NAME"
    printf 'APP_NAME=%q\nAPP_ID=%q\nAPPDIR=%q\nBINDIR=%q\nAPPS=%q\nICONS=%q\nSYSTEM=%q\n' \
      "$APP_NAME" "$APP_ID" "$APPDIR" "$BINDIR" "$APPS" "$ICONS" "$SYSTEM"
    cat <<'UNINSTALL'
set -uo pipefail
YES=0; PURGE=ask
for a in "$@"; do case "$a" in -y|--yes) YES=1 ;; --purge) PURGE=yes ;; --keep-settings) PURGE=no ;; esac; done
if [ -t 1 ] && [ -z "${NO_COLOR:-}" ]; then B=$'\033[1m'; G=$'\033[32m'; M=$'\033[90m'; Rd=$'\033[31m'; R=$'\033[0m'; else B=; G=; M=; Rd=; R=; fi
case "${LC_ALL:-${LC_CTYPE:-${LANG:-}}}" in *[Uu][Tt][Ff]*8*) OK="✓" ;; *) OK="+" ;; esac
if [ "$SYSTEM" = 1 ] && [ "$(id -u)" != 0 ]; then echo "Run with sudo: sudo spendlight-uninstall"; exit 1; fi
# ask "Question" Y|N — without a terminal to answer on, the default wins.
ask() { if [ ! -t 0 ]; then [ "$2" = Y ]; return; fi; printf '\n  %s %s[%s]%s ' "$1" "$M" "$([ "$2" = Y ] && echo Y/n || echo y/N)" "$R"; read -r x || x=; x="${x:-$2}"; [[ "$x" == [Yy]* ]]; }
printf '\n  %sRemove %s%s %sfrom %s%s\n' "$B" "$APP_NAME" "$R" "$M" "$APPDIR" "$R"
printf '  %sYour Claude Code logs are never touched.%s\n' "$M" "$R"
if [ "$YES" != 1 ]; then ask "Remove it now?" N || { echo "  Nothing changed."; exit 0; }; fi
# Saved settings go only with --purge, or when you say so at the prompt.
if [ "$PURGE" = ask ]; then
  if [ "$YES" != 1 ] && [ -d "$APPDIR/browser" ] && ask "Also delete saved settings (filters, theme)?" N; then PURGE=yes; else PURGE=no; fi
fi
printf '\n'
done_() { printf '    %s%s%s %s\n' "$G" "$OK" "$R" "$1"; }
command -v pkill >/dev/null 2>&1 && pkill -f "$APPDIR/$APP_ID" 2>/dev/null; sleep 0.3
done_ "Closed $APP_NAME if it was running"
for l in "$BINDIR/$APP_ID" "$BINDIR/$APP_ID-uninstall"; do
  if [ -L "$l" ] && [[ "$(readlink "$l")" == "$APPDIR"/* ]]; then rm -f "$l"; fi
done
done_ "Removed the spendlight commands"
rm -f "$APPS/$APP_ID.desktop" "$ICONS/scalable/apps/$APP_ID.svg" "$ICONS"/{48x48,64x64,128x128,256x256}/apps/"$APP_ID.png"
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database -q "$APPS" 2>/dev/null
command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q -t "$ICONS" 2>/dev/null
done_ "Removed the menu entry and icon"
rm -f "$APPDIR/$APP_ID" "$APPDIR/$APP_ID.new" "$APPDIR/icon.svg" "$APPDIR/VERSION" "$APPDIR/LICENSE"
[ "$PURGE" = yes ] && rm -rf "$APPDIR/browser"
rm -f "$APPDIR/uninstall.sh"
rmdir "$APPDIR" 2>/dev/null
done_ "Removed the app$([ "$PURGE" = yes ] && echo ' and its saved settings')"
printf '\n  %s%s%s%s was removed.%s\n\n' "$G" "$B" "$OK " "$APP_NAME" "$R"
UNINSTALL
  } > "$APPDIR/uninstall.sh"
  chmod 0755 "$APPDIR/uninstall.sh"
}
link_commands() {
  mkdir -p "$BINDIR"
  ln -sfn "$APPDIR/$APP_ID" "$BINDIR/$APP_ID"
  ln -sfn "$APPDIR/uninstall.sh" "$BINDIR/$APP_ID-uninstall"
}
add_menu_entry() {
  mkdir -p "$APPS" "$ICONS/scalable/apps"
  install -m 0644 "$TMP/share/$APP_ID.svg" "$ICONS/scalable/apps/$APP_ID.svg"
  local s
  for s in 48 64 128 256; do
    if [ -f "$TMP/share/$APP_ID-$s.png" ]; then
      mkdir -p "$ICONS/${s}x${s}/apps"
      install -m 0644 "$TMP/share/$APP_ID-$s.png" "$ICONS/${s}x${s}/apps/$APP_ID.png"
    fi
  done
  cat > "$APPS/$APP_ID.desktop" <<EOF
[Desktop Entry]
Type=Application
Version=1.5
Name=$APP_NAME
GenericName=Usage Dashboard
Comment=See what your Claude Code usage would cost at API list prices
Exec="$APPDIR/$APP_ID"
TryExec=$APPDIR/$APP_ID
Icon=$APP_ID
Terminal=false
Categories=Development;Utility;
Keywords=claude;tokens;cost;usage;spend;dashboard;
StartupNotify=true
StartupWMClass=$APP_ID
EOF
  chmod 0644 "$APPS/$APP_ID.desktop"
  if command -v update-desktop-database >/dev/null 2>&1; then update-desktop-database -q "$APPS" 2>/dev/null || true; fi
  if command -v gtk-update-icon-cache >/dev/null 2>&1; then gtk-update-icon-cache -q -t "$ICONS" 2>/dev/null || true; fi
  touch "$ICONS" 2>/dev/null || true
}
launch_app() {
  if [ "$(id -u)" = 0 ]; then return 0; fi   # never start a desktop app as root
  if command -v setsid >/dev/null 2>&1; then
    setsid "$APPDIR/$APP_ID" >/dev/null 2>&1 < /dev/null &
  else
    nohup "$APPDIR/$APP_ID" >/dev/null 2>&1 < /dev/null &
  fi
  disown 2>/dev/null || true
}
path_has_bindir() { case ":$PATH:" in *":$BINDIR:"*) return 0 ;; *) return 1 ;; esac; }

# ─────────────────────────────── --extract ───────────────────────────────

if [ -n "$EXTRACT" ]; then
  mkdir -p "$EXTRACT"; TMP="$(cd "$EXTRACT" && pwd)"
  verify_payload && unpack && echo "Unpacked into $TMP"
  TMP=""; exit 0
fi

# ─────────────────────────────── desktop (zenity) flow ───────────────────────────────

HAS_DISPLAY=0; [ -n "${DISPLAY:-}${WAYLAND_DISPLAY:-}" ] && HAS_DISPLAY=1
if [ "$UI" = auto ]; then
  if [ "$IS_TTY" = 0 ] && [ "$HAS_DISPLAY" = 1 ] && command -v zenity >/dev/null 2>&1; then UI=gui; else UI=text; fi
fi
# Double-clicked with no terminal and no zenity: reopen in a terminal window if we can.
if [ "$UI" = text ] && [ "$IS_TTY" = 0 ] && [ "$HAS_DISPLAY" = 1 ] && [ "$ASSUME_YES" = 0 ] && [ -z "${SPENDLIGHT_IN_TERM:-}" ]; then
  export SPENDLIGHT_IN_TERM=1
  for t in x-terminal-emulator gnome-terminal konsole xfce4-terminal kitty alacritty xterm; do
    if command -v "$t" >/dev/null 2>&1; then
      case "$t" in
        gnome-terminal) exec "$t" -- bash "$SELF" ${ORIG_ARGS[@]+"${ORIG_ARGS[@]}"} ;;
        *) exec "$t" -e bash "$SELF" ${ORIG_ARGS[@]+"${ORIG_ARGS[@]}"} ;;
      esac
    fi
  done
fi

if [ "$UI" = gui ]; then
  command -v zenity >/dev/null 2>&1 || fail "--gui needs zenity. Run without it for the terminal installer."
  TITLE="$APP_NAME Setup"
  if [ "$SESSIONS" -gt 0 ]; then
    LOGLINE="Found <b>$SESSIONS</b> Claude Code $(plural "$SESSIONS" session) across <b>$PROJECTS</b> $(plural "$PROJECTS" project)."
  else
    LOGLINE="No Claude Code logs yet — the dashboard fills in once you use Claude Code."
  fi
  ACTION="Install"; [ -n "$PREVIOUS" ] && ACTION="Update"
  if [ "$ASSUME_YES" = 0 ]; then
    zenity --question --title="$TITLE" --width=460 --ok-label="$ACTION" --cancel-label="Cancel" \
      --text="<span size='x-large' weight='bold'>$ACTION $APP_NAME</span>\n<span foreground='#898781'>Version $VERSION</span>\n\nA desktop dashboard that shows what your Claude Code usage would cost at Anthropic API list prices, by model, project and session.\n\n$LOGLINE\n\n<b>Installs to</b>  $(tilde "$APPDIR")\n<b>For</b>  $SCOPE" \
      2>/dev/null || exit 0
  fi
  TMP="$(mktemp -d)"
  set +e
  (
    set -e
    echo 5;  echo "# Checking the download…";    verify_payload >>"$ERRLOG" 2>&1
    echo 20; echo "# Unpacking…";                unpack >>"$ERRLOG" 2>&1
    echo 40; echo "# Closing any running copy…";  stop_running
    if [ "$HAS_LEGACY" = 1 ]; then echo 48; echo "# Replacing the old $LEGACY_NAME…"; remove_legacy >>"$ERRLOG" 2>&1; fi
    echo 55; echo "# Copying the app…";          install_files >>"$ERRLOG" 2>&1; write_uninstaller
    echo 75; echo "# Adding the spendlight command…"; link_commands >>"$ERRLOG" 2>&1
    if [ "$MENU" = 1 ]; then echo 88; echo "# Adding to your applications menu…"; add_menu_entry >>"$ERRLOG" 2>&1; fi
    echo 100; echo "# Done"
  ) | zenity --progress --title="$TITLE" --text="Installing…" --percentage=0 --auto-close --no-cancel --width=460 2>/dev/null
  status=${PIPESTATUS[0]}
  set -e
  if [ "$status" != 0 ]; then
    zenity --error --title="$TITLE" --width=460 --text="<b>Installation failed.</b>\n\n$(tail -n 5 "$ERRLOG" | sed 's/&/\&amp;/g; s/</\&lt;/g')" 2>/dev/null || true
    exit 1
  fi
  if [ "$LAUNCH" = yes ] || { [ "$LAUNCH" = ask ] && zenity --question --title="$TITLE" --width=460 \
      --ok-label="Open $APP_NAME" --cancel-label="Close" \
      --text="<span size='x-large' weight='bold'>$APP_NAME is ready</span>\n\nFind it in your applications menu, or run <tt>$APP_ID</tt> in a terminal.\nRemove it any time with <tt>$APP_ID-uninstall</tt>." 2>/dev/null; }; then
    launch_app
  fi
  exit 0
fi

# ─────────────────────────────── terminal flow ───────────────────────────────

banner "Installer"
printf '  %sSee what your Claude Code usage would cost at Anthropic API list%s\n' "$INK" "$R"
printf '  %sprices %s by model, project and session.%s\n' "$INK" "$DOT" "$R"
printf '  %sFree software under the GNU GPL v3 %s github.com/Pacsy1/spendlight%s\n' "$MUTED" "$DOT" "$R"

section "System"
ok "Linux ${ARCH_LABEL}${DISTRO:+ $DOT $DISTRO} $DOT $LIBC"
if [ "$SESSIONS" -gt 0 ]; then
  ok "Claude Code logs: $SESSIONS $(plural "$SESSIONS" session) across $PROJECTS $(plural "$PROJECTS" project) ${MUTED}($(tilde "$LOGS"))${R}"
else
  warn "No Claude Code logs yet ${MUTED}— the dashboard fills in once you use Claude Code${R}"
fi
if [ -n "$BROWSER" ]; then
  ok "Opens as an app window in ${B}$BROWSER${R}"
else
  warn "No Chromium-based browser found ${MUTED}— opens in your default browser instead${R}"
fi
[ -n "$PREVIOUS" ] && ok "Version $PREVIOUS is installed ${MUTED}— it will be replaced${R}"
[ "$HAS_LEGACY" = 1 ] && ok "$LEGACY_NAME is installed ${MUTED}— Spendlight replaces it, keeping your settings${R}"

section "Plan"
row "For" "$SCOPE"
row "App" "$(tilde "$APPDIR")"
row "Command" "$(tilde "$BINDIR/$APP_ID")"
[ "$MENU" = 1 ] && row "Menu" "$APP_NAME ${MUTED}(Development, Utility)${R}"
row "Size" "$INSTALLED_MB MB ${MUTED}(self-contained — nothing else to install)${R}"

VERB="Install"; VERBING="Installing"
if [ -n "$PREVIOUS" ]; then VERB="Update"; VERBING="Updating"; fi
ask "$VERB $APP_NAME now?" Y || { printf '\n  Nothing was changed.\n\n'; exit 0; }

section "$VERBING"
TMP="$(mktemp -d)"
step "Verifying the download"          verify_payload
step "Unpacking"                       unpack
step "Closing any running copy"        stop_running
[ "$HAS_LEGACY" = 1 ] && step "Replacing the old $LEGACY_NAME" remove_legacy
step "Copying the app"                 install_files
step "Writing the uninstaller"         write_uninstaller
step "Adding the spendlight command"   link_commands
[ "$MENU" = 1 ] && step "Adding to your applications menu" add_menu_entry

printf '\n'
box_top
box_line ""
box_line "  ${GREEN}${B}${OK} ${APP_NAME} ${VERSION} is installed${R}"
box_line ""
[ "$MENU" = 1 ] && box_line "  ${MUTED}Open it from${R}  your applications menu"
box_line "  ${MUTED}Or run${R}        ${B}${APP_ID}${R}"
box_line "  ${MUTED}Remove with${R}   ${APP_ID}-uninstall"
box_line ""
box_bottom

if ! path_has_bindir; then
  printf '\n'
  warn "$(tilde "$BINDIR") isn't on your PATH yet, so the commands need their full path."
  note "      Add this to ~/.bashrc (or ~/.zshrc) and open a new terminal:"
  printf '        %sexport PATH="$HOME/.local/bin:$PATH"%s\n' "$B" "$R"
fi

if [ "$(id -u)" != 0 ] && [ "$HAS_DISPLAY" = 1 ]; then
  if [ "$LAUNCH" = yes ] || { [ "$LAUNCH" = ask ] && ask "Open $APP_NAME now?" Y; }; then
    launch_app
    printf '\n  %sOpening…%s\n' "$MUTED" "$R"
  fi
fi
printf '\n'
exit 0
