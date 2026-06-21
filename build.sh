#!/bin/bash
# build.sh – Build GeneKerman KSP mod and deploy to test instance
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_DIR="$SCRIPT_DIR/GeneKerman"
# Deploy to every KSP install here. First entry is the "normal" instance (logs are
# its KSP.log); the Steam instance is the "heavymod" instance used for TweakScale /
# mod-compatibility testing (its KSP.log is the "heavymod log").
KSP_PATHS=(
    "/home/ayd/Documents/Kerbal Space Program"
    "/home/ayd/.local/share/Steam/steamapps/common/Kerbal Space Program"
    "/home/ayd/Documents/Fake Kerman KSP Clone"
)
GAMEDATA_SRC="$SCRIPT_DIR/GameData/GeneKerman"

echo "═══════════════════════════════════════════════════"
echo "  Gene Kerman KSP Mod — Build Script"
echo "═══════════════════════════════════════════════════"

# ── Step 1: Build ────────────────────────────────────────
echo ""
echo "▶ Building GeneKerman.dll..."
cd "$PROJECT_DIR"

# Use dotnet build (works with .NET SDK + .NET 4.7.2 targeting pack)
dotnet build -c Release 2>&1

if [ ! -f "$PROJECT_DIR/bin/GeneKerman.dll" ]; then
    echo "❌ Build failed — DLL not found."
    exit 1
fi

echo "✅ Build successful."

# Print the DLL's SHA256 — this is the hash to register with /admin publishversion
# in Discord (or paste into its `sha256` field) so the update gate recognises this build.
if command -v sha256sum >/dev/null 2>&1; then
    DLL_HASH="$(sha256sum "$PROJECT_DIR/bin/GeneKerman.dll" | cut -d' ' -f1)"
    echo "   GeneKerman.dll SHA256: $DLL_HASH"
fi

# ── Step 2: Prepare GameData ─────────────────────────────
echo ""
echo "▶ Preparing GameData structure..."

mkdir -p "$GAMEDATA_SRC/Plugins"
mkdir -p "$GAMEDATA_SRC/PluginData"
mkdir -p "$GAMEDATA_SRC/Textures"

# Copy DLL
cp "$PROJECT_DIR/bin/GeneKerman.dll" "$GAMEDATA_SRC/Plugins/"
echo "  → Copied GeneKerman.dll"

# Copy websocket-sharp dependency (ships next to GeneKerman.dll)
cp "$PROJECT_DIR/lib/websocket-sharp.dll" "$GAMEDATA_SRC/Plugins/"
echo "  → Copied websocket-sharp.dll"

# Copy Icon
if [ -f "$SCRIPT_DIR/logo_38.png" ]; then
    cp "$SCRIPT_DIR/logo_38.png" "$GAMEDATA_SRC/Textures/icon_toolbar.png"
    echo "  → Copied custom toolbar icon (logo_38.png)"
elif [ -f "$SCRIPT_DIR/logo.png" ]; then
    cp "$SCRIPT_DIR/logo.png" "$GAMEDATA_SRC/Textures/icon_toolbar.png"
    echo "  → Copied custom toolbar icon (logo.png)"
fi

# Copy Iconpack-1 UI icons
if [ -d "$SCRIPT_DIR/Iconpack-1" ]; then
    cp "$SCRIPT_DIR/Iconpack-1/"*.png "$GAMEDATA_SRC/Textures/"
    echo "  → Copied Iconpack-1 UI icons"
fi

# Copy default settings if not exists
if [ ! -f "$GAMEDATA_SRC/PluginData/settings.cfg" ]; then
    cat > "$GAMEDATA_SRC/PluginData/settings.cfg" << 'EOF'
GeneKerman
{
    serverUrl = http://localhost:5022
    checkInterval = 600
    enableNotifications = true
    enableKVV = true
    enableContractInjection = true
}
EOF
    echo "  → Created default settings.cfg"
fi

# ── Step 3: Deploy to KSP ───────────────────────────────
echo ""
echo "▶ Deploying to KSP instance(s)..."

deployed=0
for KSP_PATH in "${KSP_PATHS[@]}"; do
    GAMEDATA_DST="$KSP_PATH/GameData/GeneKerman"
    if [ -d "$KSP_PATH" ]; then
        mkdir -p "$GAMEDATA_DST"
        # Preserve the install's own settings.cfg (server choice + toggles) — it's
        # user data, not a build artifact, so a redeploy must never clobber it. Back
        # it up, copy, then restore; installs without one still get the default.
        DST_CFG="$GAMEDATA_DST/PluginData/settings.cfg"
        if [ -f "$DST_CFG" ]; then cp "$DST_CFG" "$DST_CFG.deploybak"; fi
        cp -r "$GAMEDATA_SRC/"* "$GAMEDATA_DST/"
        if [ -f "$DST_CFG.deploybak" ]; then mv -f "$DST_CFG.deploybak" "$DST_CFG"; fi
        echo "  → Deployed to $GAMEDATA_DST ($(du -h "$GAMEDATA_DST/Plugins/GeneKerman.dll" | cut -f1))"
        deployed=$((deployed + 1))
    else
        echo "  ⚠️  KSP not found at $KSP_PATH — skipping."
    fi
done

echo ""
if [ "$deployed" -gt 0 ]; then
    echo "✅ Build complete! Deployed to $deployed KSP instance(s)."
else
    echo "⚠️  No KSP install found — DLL is at: $GAMEDATA_SRC/Plugins/GeneKerman.dll"
fi

echo ""
echo "═══════════════════════════════════════════════════"
