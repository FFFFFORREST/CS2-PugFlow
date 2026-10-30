#!/usr/bin/env bash
set -euo pipefail

# Run as 'ubuntu' (an account allowed to use sudo), NOT as root or steam.
if [ "$(id -u)" -eq 0 ]; then
  echo 'ERROR: run as ubuntu (not root); script uses sudo selectively.' >&2; exit 1
fi

BASE="$(cd "$(dirname "$0")" && pwd)"
GAME="/home/steam/cs2/game/csgo"
API="$GAME/addons/counterstrikesharp/api/CounterStrikeSharp.API.dll"
PLUGINS="$GAME/addons/counterstrikesharp/plugins"
BACKUP="$HOME/cs2-startvote-backup-$(date +%Y%m%d-%H%M%S)"

sudo test -f "$API" || { echo "ERROR: CounterStrikeSharp API missing: $API" >&2; exit 1; }
sudo test -f "$GAME/cfg/server.cfg" || { echo "ERROR: server.cfg missing: $GAME/cfg/server.cfg" >&2; exit 1; }
[ -f "$BASE/source/PugFlow.csproj" ] || { echo "ERROR: run this script inside the extracted package" >&2; exit 1; }

if ! command -v dotnet >/dev/null 2>&1 || ! dotnet --list-sdks 2>/dev/null | grep '^10\.' >/dev/null; then
  echo 'ERROR: .NET 10 SDK is not installed. Run: sudo apt update && sudo apt install -y dotnet-sdk-10.0' >&2
  exit 1
fi

# Ubuntu typically cannot traverse /home/steam, so copy the API assembly
# as a local BUILD-ONLY reference. It will not be deployed with the plugin.
mkdir -p "$BASE/source/lib"
sudo cp "$API" "$BASE/source/lib/CounterStrikeSharp.API.dll"
sudo chown "$(id -un):$(id -gn)" "$BASE/source/lib/CounterStrikeSharp.API.dll"
chmod 644 "$BASE/source/lib/CounterStrikeSharp.API.dll"

echo '[1/5] Building plugin...'
dotnet build "$BASE/source/PugFlow.csproj" --configuration Release --nologo
DLL="$BASE/source/bin/Release/net10.0/PugFlow.dll"
[ -s "$DLL" ] || { echo 'ERROR: build did not generate DLL'; exit 1; }

mkdir -p "$BACKUP"
echo "[2/5] Backing up existing cfg files and plugin DLL to $BACKUP ..."
for name in server.cfg practice.cfg knife.cfg live.cfg rush_live.cfg gamemode_rush_server.cfg; do
  if sudo test -f "$GAME/cfg/$name"; then sudo cp "$GAME/cfg/$name" "$BACKUP/$name"; fi
done
if sudo test -f "$PLUGINS/PugFlow/PugFlow.dll"; then
  sudo cp "$PLUGINS/PugFlow/PugFlow.dll" "$BACKUP/PugFlow.dll"
fi

# Downtime starts only after a successful build.
echo '[3/5] Stopping CS2 and deploying...'
sudo systemctl stop cs2
# If any deploy step fails, don't leave the server down.
trap 'echo "ERROR: deploy failed; restarting CS2. Backups: $BACKUP" >&2; sudo systemctl start cs2 || true' ERR
# Versions before 1.6.0 were called WarmupStartVote. Move that plugin into the backup so
# the old and new plugins don't both load and answer every command twice.
if sudo test -d "$PLUGINS/WarmupStartVote"; then sudo mv "$PLUGINS/WarmupStartVote" "$BACKUP/WarmupStartVote-plugin"; fi
sudo install -d -o steam -g steam "$PLUGINS/PugFlow"
sudo install -m 644 -o steam -g steam "$DLL" "$PLUGINS/PugFlow/PugFlow.dll"
sudo install -m 644 -o steam -g steam "$BASE/configs/practice.cfg" "$GAME/cfg/practice.cfg"
sudo install -m 644 -o steam -g steam "$BASE/configs/live.cfg" "$GAME/cfg/live.cfg"
sudo install -m 644 -o steam -g steam "$BASE/configs/knife.cfg" "$GAME/cfg/knife.cfg"
sudo install -m 644 -o steam -g steam "$BASE/configs/rush_live.cfg" "$GAME/cfg/rush_live.cfg"
# CS2 runs gamemode_rush_server.cfg after gamemode_rush.cfg; it holds Rush's warmup until !votestart.
sudo install -m 644 -o steam -g steam "$BASE/configs/gamemode_rush_server.cfg" "$GAME/cfg/gamemode_rush_server.cfg"

# The plugin runs practice.cfg itself after each map start, after the gamemode cfg.
# Versions before 1.4.0 appended "exec practice.cfg" to server.cfg, which would overwrite
# Rush rules, so take that line (and its comment) out of the user's server.cfg.
sudo sed -i -E -e '/^[[:space:]]*\/\/ Start in infinite free warmup\. \(WarmupStartVote\)[[:space:]]*$/d' \
  -e '/^[[:space:]]*exec[[:space:]]+practice\.cfg([[:space:]]*(\/\/.*)?)?$/d' "$GAME/cfg/server.cfg"

trap - ERR
echo '[4/5] Starting CS2...'
sudo systemctl start cs2

echo '[5/5] Deployment complete.'
echo "Backups: $BACKUP"
echo 'Check: sudo systemctl status cs2 --no-pager'
echo "Check plugin: sudo journalctl -u cs2 -n 150 --no-pager | grep -Ei 'PugFlow|error|exception'"
echo 'In game: !votestart -> !yes -> knife -> winning team !stay/!swap -> live (Rush maps: straight to live).'
