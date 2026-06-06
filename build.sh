#!/bin/bash
# build.sh – Build GeneKerman KSP mod and deploy to test instance
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_DIR="$SCRIPT_DIR/GeneKerman"
KSP_PATH="/home/ayd/Documents/Kerbal Space Program"
GAMEDATA_SRC="$SCRIPT_DIR/GameData/GeneKerman"
GAMEDATA_DST="$KSP_PATH/GameData/GeneKerman"

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

# ── Step 2: Prepare GameData ─────────────────────────────
echo ""
echo "▶ Preparing GameData structure..."

mkdir -p "$GAMEDATA_SRC/Plugins"
mkdir -p "$GAMEDATA_SRC/PluginData"
mkdir -p "$GAMEDATA_SRC/Textures"

# Copy DLL
cp "$PROJECT_DIR/bin/GeneKerman.dll" "$GAMEDATA_SRC/Plugins/"
echo "  → Copied GeneKerman.dll"

# Copy default settings if not exists
if [ ! -f "$GAMEDATA_SRC/PluginData/settings.cfg" ]; then
    cat > "$GAMEDATA_SRC/PluginData/settings.cfg" << 'EOF'
GeneKerman
{
    serverUrl = http://localhost:5850
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
echo "▶ Deploying to KSP test instance..."

if [ -d "$KSP_PATH" ]; then
    mkdir -p "$GAMEDATA_DST"
    cp -r "$GAMEDATA_SRC/"* "$GAMEDATA_DST/"
    echo "  → Deployed to $GAMEDATA_DST"
    echo ""
    echo "✅ Build complete! DLL deployed to KSP GameData."
    echo "   Size: $(du -h "$GAMEDATA_DST/Plugins/GeneKerman.dll" | cut -f1)"
else
    echo "⚠️  KSP not found at $KSP_PATH — skipping deployment."
    echo "   DLL is at: $GAMEDATA_SRC/Plugins/GeneKerman.dll"
fi

echo ""
echo "═══════════════════════════════════════════════════"
