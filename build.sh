#!/bin/bash
# build.sh – Build GeneKerman KSP mod and deploy to test instance
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_DIR="$SCRIPT_DIR/GeneKerman"

# --release additionally packages dist/BoundlessMissions-<version>.zip. The zip must
# contain exactly what BoundlessMissions.netkan's `filter` expects to be installing, so
# the two are kept in step here rather than by hand — see PACKAGING.md.
RELEASE=0
if [ "${1:-}" = "--release" ]; then RELEASE=1; fi

# ── Build channel ────────────────────────────────────────────────────────────
# 'production' (the default) ships a clean DLL with the in-game debug test panel
# (DebugTestPanel.cs) COMPILED OUT — it is wrapped in #if GK_DEBUG_PANEL, which the
# csproj defines ONLY when this is not 'production'. Set CHANNEL=dev here (or run
# `GK_CHANNEL=dev ./build.sh`) to include the panel for the in-game live security
# tests. A dev build must NEVER be published: it carries test-only code and its
# DLL hash differs, so the server version gate would reject it anyway.
#
# >>> Keep this at 'production' for anything you ship. <<<
CHANNEL="${GK_CHANNEL:-production}"

# A packaged release with the debug panel in it is almost certainly a mistake — refuse.
if [ "$RELEASE" = "1" ] && [ "$CHANNEL" != "production" ]; then
    echo "❌ Refusing to package a release on channel '$CHANNEL' — the debug test panel"
    echo "   would be bundled. Run without --release, or set CHANNEL=production."
    exit 1
fi
# Deploy to every KSP install here. KR-KSP carries its own mod spread (SSPX, TAC-LS,
# Kerbalism, Deferred + TexturesUnlimited, …), is the rendering-stack compatibility
# testbed, and is what the csproj compiles against; KR2-KSP duplicates it so two games
# can run side by side for multiplayer tests; RSS-RO runs the Realism Overhaul / Real
# Solar System suite (RealFuels, FAR, Kopernicus+RSS…).
#
# FK-KSP — the heavily-modded "heavymod" instance (TweakScale / mod-compatibility
# testing) — was deleted on 2026-08-22 and removed from this list. Note the deploy loop
# below only tests `-d "$KSP_PATH"`, so a leftover empty directory would still be
# deployed into rather than skipped.
#
# FAK1 (added 2026-08-30) carries the "Far All Kerbalkind" modpack — RP-1 / Realism
# Overhaul / RSS with Kerbalism, FAR, KCT, FMRS, RealAntennas, Kerbal Konstructs — and
# is the heaviest compatibility testbed of the four (Kerbalism + RO + FAR at once).
#
# Stock-1 / Stock-2 (added 2026-09-01) are two *unmodded* installs kept as a matched
# pair, for the two-player cases the T0–T7 plan needs: a rescue round trip and the
# impersonation case both require two accounts acting on two saves, and every earlier
# attempt at them was blocked on having only one instance available.
#
# Stock on purpose, and it is not only about load time. Every previous in-game finding
# had to be argued against a heavily modded install — a NaN orbit, an exception storm
# and a vanishing toolbar button each cost a round of ruling third-party mods out. A
# clean pair makes "this is ours" the default reading rather than the conclusion of an
# investigation. They are also light enough to run simultaneously, which the modded
# instances are not.
KSP_PATHS=(
    "/home/ayd/Documents/KSP DEV Instances/KR-KSP"
    "/home/ayd/Documents/KSP DEV Instances/KR2-KSP"
    "/home/ayd/Documents/KSP DEV Instances/RSS-RO"
    "/home/ayd/Documents/KSP DEV Instances/FAK1"
    "/home/ayd/Documents/KSP DEV Instances/Stock-1"
    "/home/ayd/Documents/KSP DEV Instances/Stock-2"
)
GAMEDATA_SRC="$SCRIPT_DIR/GameData/BoundlessMissions"

echo "═══════════════════════════════════════════════════"
echo "  Gene Kerman KSP Mod — Build Script"
echo "═══════════════════════════════════════════════════"

# ── Step 1: Build ────────────────────────────────────────
echo ""
echo "▶ Building GeneKerman.dll..."
cd "$PROJECT_DIR"

# Use dotnet build (works with .NET SDK + .NET 4.7.2 targeting pack).
# GKChannel gates the debug test panel: 'production' compiles it out entirely.
echo "  Channel: $CHANNEL$([ "$CHANNEL" != "production" ] && echo '  ⚠️  DEBUG TEST PANEL INCLUDED — do not publish')"
dotnet build -c Release -p:GKChannel="$CHANNEL" 2>&1

if [ ! -f "$PROJECT_DIR/bin/GeneKerman.dll" ]; then
    echo "❌ Build failed — DLL not found."
    exit 1
fi

if [ "$CHANNEL" != "production" ]; then
    echo ""
    echo "⚠️  ═══════════════════════════════════════════════════════════════════"
    echo "⚠️   This is a '$CHANNEL' build: the in-game debug test panel (F12) is"
    echo "⚠️   COMPILED IN. Do NOT publish it or register its hash. Rebuild with"
    echo "⚠️   CHANNEL=production (the default) for anything you ship."
    echo "⚠️  ═══════════════════════════════════════════════════════════════════"
fi

echo "✅ Build successful."

# A production DLL must contain no trace of the dev-only test bridge (Web/DebugBridge.cs,
# Web/DebugRoutes.cs) — a command channel that spawns vessels and edits rosters. It is
# held out by #if GK_DEBUG_PANEL, which is exactly the sort of gate that fails silently:
# delete one #if and everything still compiles and still passes every test.
#
# --no-build so this costs a `strings` pass rather than two more compiles. That skips the
# script's second half (proving a dev build DOES contain the markers, without which the
# absence proves nothing), so run `tools/assert_production_clean.sh` with no arguments
# before publishing.
# GK_FULL_VERIFY=1 runs BOTH halves — the second one builds a dev DLL and proves it
# DOES carry every marker, which is what makes the absence in the production DLL mean
# anything. Without it a renamed marker makes this check pass vacuously, which the
# script's own header calls "a green light that proves nothing". Default stays
# --no-build so an ordinary build is a `strings` pass rather than two more compiles;
# set GK_FULL_VERIFY=1 before publishing.
if [ "$CHANNEL" = "production" ]; then
    if [ -x "$SCRIPT_DIR/tools/assert_production_clean.sh" ]; then
        VERIFY_ARGS="--no-build"
        if [ "${GK_FULL_VERIFY:-0}" = "1" ]; then
            VERIFY_ARGS=""
            echo "🔎 GK_FULL_VERIFY=1 — running both halves of the debug-marker check."
        fi
        if ! "$SCRIPT_DIR/tools/assert_production_clean.sh" $VERIFY_ARGS; then
            echo "❌ Refusing to continue: the production DLL carries debug-bridge code."
            exit 1
        fi
        if [ "${GK_FULL_VERIFY:-0}" != "1" ]; then
            echo "ℹ️  Marker check ran in --no-build mode: it proved the markers are ABSENT"
            echo "   here, not that they would be PRESENT in a dev build. Before publishing:"
            echo "   GK_FULL_VERIFY=1 ./build.sh"
        fi
    else
        echo "❌ tools/assert_production_clean.sh is missing or not executable —"
        echo "   the production DLL cannot be verified, so this build is not publishable."
        echo "   (It is the ONE automated gate on the shipped artifact; a warning here"
        echo "    meant \`chmod -x\` silently disabled it, including for --release.)"
        exit 1
    fi
fi

# Print the DLL's SHA256 — on a production build this is the hash to register with
# /admin publishversion in Discord (or paste into its `sha256` field) so the update gate
# recognises this build. On any other channel the hash belongs to a DLL carrying the
# debug bridge, so it is printed with the refusal attached rather than silently: an
# unqualified hash under a banner that says "register this" is how a dev build gets
# blessed by the version gate and the DLL-attestation challenge.
if command -v sha256sum >/dev/null 2>&1; then
    DLL_HASH="$(sha256sum "$PROJECT_DIR/bin/GeneKerman.dll" | cut -d' ' -f1)"
    if [ "$CHANNEL" = "production" ]; then
        echo "   GeneKerman.dll SHA256: $DLL_HASH"
    else
        echo "   DEV — do not publish — GeneKerman.dll SHA256 ('$CHANNEL' build): $DLL_HASH"
    fi
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
    # Every key here must be one ApiClient.LoadSettings actually reads. This file used
    # to ship checkInterval / enableKVV / enableContractInjection, none of which any
    # code has read for some time — a shipped default that does nothing is worse than
    # no default, because players change it and reasonably expect an effect.
    #
    # Note the split serverProtocol/Host/Port: ConfigNode treats // as a comment
    # delimiter, so a whole URL cannot be stored as one value.
    cat > "$GAMEDATA_SRC/PluginData/settings.cfg" << 'EOF'
GeneKerman
{
    // Official server, or a custom one (used when useOfficialServer = false).
    useOfficialServer = true
    serverProtocol = http
    serverHost = localhost
    serverPort = 5022

    // In-game toast popups when something happens.
    enableNotifications = true

    // Milestone hero-shot prompts (rendezvous, flyby, asteroid encounter).
    enableCheckpointPhotos = true

    // Master opt-out (KSP add-on rule 8.2). While false the mod transmits nothing
    // and runs inert until it is turned back on from the in-game panel.
    enableDataGathering = true

    // Browser interface. When true, the toolbar button serves the UI from
    // 127.0.0.1 on a random port and opens it in your browser instead of drawing
    // the classic in-game windows. Off by default; togglable in Settings.
    enableWebUi = false
}
EOF
    echo "  → Created default settings.cfg"
fi

# ── Step 2a: Stamp GeneKerman.version ────────────────────
# KSP-AVC and CKAN read this file to decide what version is installed. It used to be
# hand-maintained and had drifted three ways at once (0.5.1 here, 1.0.0 in
# ModVersion.cs, 0.8.1 in the release zip's name), which would have had CKAN offering
# "upgrades" to builds players already had. Generate it from the single source of
# truth instead, so it cannot drift again.
MOD_VERSION="$(grep -oP 'Current\s*=\s*"\K[0-9]+\.[0-9]+\.[0-9]+' "$PROJECT_DIR/ModVersion.cs")"
if [ -z "$MOD_VERSION" ]; then
    echo "❌ Could not read ModVersion.Current from ModVersion.cs."
    exit 1
fi
IFS='.' read -r V_MAJOR V_MINOR V_PATCH <<< "$MOD_VERSION"

cat > "$GAMEDATA_SRC/GeneKerman.version" << EOF
{
    "NAME": "Boundless Missions",
    "URL": "https://boundlessmissions.com/GeneKerman.version",
    "DOWNLOAD": "https://github.com/Boundless-Missions/boundlessmissions-modside/releases/latest",
    "VERSION": {
        "MAJOR": $V_MAJOR,
        "MINOR": $V_MINOR,
        "PATCH": $V_PATCH
    },
    "KSP_VERSION": {
        "MAJOR": 1,
        "MINOR": 12,
        "PATCH": 5
    },
    "KSP_VERSION_MIN": {
        "MAJOR": 1,
        "MINOR": 12,
        "PATCH": 0
    },
    "KSP_VERSION_MAX": {
        "MAJOR": 1,
        "MINOR": 12,
        "PATCH": 99
    }
}
EOF
echo "  → Stamped GeneKerman.version ($MOD_VERSION)"

# The AVC `URL` above points at boundlessmissions.com/GeneKerman.version — the copy
# KSP-AVC fetches to learn what the latest version IS. It was never served (404), which
# does not fail loudly: it silently disables the in-game update check, which is why the
# DOWNLOAD field being wrong went unnoticed for so long. Publish the same bytes to the
# website's static root from the same source of truth, for the reason the block above
# generates rather than hand-maintains this file — two copies that are written together
# cannot drift, and two that are written separately always do.
WEBSITE_PUBLIC="$SCRIPT_DIR/../Website/public"
if [ -d "$WEBSITE_PUBLIC" ]; then
    cp "$GAMEDATA_SRC/GeneKerman.version" "$WEBSITE_PUBLIC/GeneKerman.version"
    echo "  → Published GeneKerman.version to Website/public (deploys with the site)"
else
    # A standalone modside checkout has no website next to it; that is fine, but say so,
    # because a release built here ships an AVC URL nothing is serving.
    echo "  ⚠️  Website/public not found — GeneKerman.version not published for AVC."
fi

# ── Step 2b: Build the browser UI ────────────────────────
# Vite writes straight into GameData/BoundlessMissions/WebUI/ and emits a
# manifest.json stamped with ModVersion.Current; the mod refuses to start the
# bridge if that does not match the running DLL.
#
# Skipped (not fatal) when node_modules is absent, so the C# build still works on a
# machine without Node — but then WebUI/ is whatever was last built, and a stale
# bundle is exactly what the manifest check exists to catch.
WEBUI_DIR="$SCRIPT_DIR/WebUI"
echo ""
if [ -d "$WEBUI_DIR/node_modules" ]; then
    echo "▶ Building browser UI..."
    ( cd "$WEBUI_DIR" && npm run build ) | tail -6
    echo "✅ Browser UI built."
elif [ -d "$WEBUI_DIR" ]; then
    echo "⚠️  Skipping browser UI build (run 'npm install' in WebUI/ first)."
else
    echo "⚠️  WebUI/ not found — skipping browser UI build."
fi

# ── Step 3: Deploy to KSP ───────────────────────────────
echo ""
echo "▶ Deploying to KSP instance(s)..."

deployed=0
for KSP_PATH in "${KSP_PATHS[@]}"; do
    GAMEDATA_DST="$KSP_PATH/GameData/BoundlessMissions"
    if [ -d "$KSP_PATH" ]; then
        mkdir -p "$GAMEDATA_DST"
        # Preserve the install's own settings.cfg (server choice + toggles) — it's
        # user data, not a build artifact, so a redeploy must never clobber it. Back
        # it up, copy, then restore; installs without one still get the default.
        DST_CFG="$GAMEDATA_DST/PluginData/settings.cfg"
        if [ -f "$DST_CFG" ]; then cp "$DST_CFG" "$DST_CFG.deploybak"; fi
        # WebUI assets are content-hashed, so cp alone would leave every previous
        # build's chunks behind forever. Clear it first — index.html only ever
        # references the current pair.
        rm -rf "$GAMEDATA_DST/WebUI"
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

# ── Step 4: Package a release ────────────────────────────
if [ "$RELEASE" = "1" ]; then
    echo ""
    echo "▶ Packaging release..."

    DIST_DIR="$SCRIPT_DIR/dist"
    STAGE="$DIST_DIR/stage"
    ZIP_PATH="$DIST_DIR/BoundlessMissions-$MOD_VERSION.zip"

    rm -rf "$STAGE"
    mkdir -p "$STAGE/GameData"
    cp -r "$GAMEDATA_SRC" "$STAGE/GameData/"

    # settings.cfg is the player's own configuration (server choice, data-sharing
    # opt-out). CKAN replaces every file it installed on upgrade, so shipping this
    # would reset those on every update — including silently re-enabling data
    # sharing, which is a rule 8.2 problem and not merely an annoyance. The mod
    # writes its own default on first run.
    rm -f "$STAGE/GameData/BoundlessMissions/PluginData/settings.cfg"

    # Nothing else in PluginData is a build artifact either — these are this
    # machine's credentials and consent record, and must never reach a release.
    rm -f "$STAGE/GameData/BoundlessMissions/PluginData/session.token" \
          "$STAGE/GameData/BoundlessMissions/PluginData/sessions.cfg" \
          "$STAGE/GameData/BoundlessMissions/PluginData/consent.cfg" \
          "$STAGE/GameData/BoundlessMissions/PluginData/favorites.cfg"

    cp "$SCRIPT_DIR/LICENSE" "$STAGE/GameData/BoundlessMissions/" 2>/dev/null || true

    # zip(1) is not installed everywhere (it is absent on this machine); Python is
    # already required for nothing else here, but it is universally present and its
    # zipfile module produces an identical archive.
    rm -f "$ZIP_PATH"
    if command -v zip >/dev/null 2>&1; then
        ( cd "$STAGE" && zip -qr "$ZIP_PATH" GameData )
    else
        python3 - "$STAGE" "$ZIP_PATH" << 'PYZIP'
import os, sys, zipfile
stage, out = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
    for root, _, files in os.walk(stage):
        for f in files:
            full = os.path.join(root, f)
            z.write(full, os.path.relpath(full, stage))
PYZIP
    fi
    rm -rf "$STAGE"

    echo "  → $ZIP_PATH ($(du -h "$ZIP_PATH" | cut -f1))"

    # Loud, because a release whose hash is not registered is rejected by the
    # server-side gate on every gated request — the mod looks broken, not outdated.
    echo ""
    echo "  Register this build before publishing:"
    echo "    /admin publishversion  version=$MOD_VERSION  sha256=$DLL_HASH"
    echo "✅ Release packaged."
fi

echo ""
echo "═══════════════════════════════════════════════════"
