import re

def fix_mainwindow():
    with open("/home/ayd/Desktop/GK-DW/KSP Mod Side/GeneKerman/UI/MainWindow.cs", "r", encoding="utf-8") as f:
        content = f.read()

    # 1. Replace emojis
    replacements = {
        "📥 Inbox": "[+] Inbox",
        "🗑️ Trash": "[X] Trash",
        "🗑️": "[Del]",
        "♻️ Restore": "[R] Restore",
        "♻️": "[Res]",
        "✏ Compose": "[New] Compose",
        "✅": "(Ok)",
        "❌": "(No)",
        "📤 Submit": "[Sub] Submit",
        "🔄": "[~]",
        "⚙️": "",
        "📋": "",
        "📜": "",
        "👤": "",
        "🔔": "",
        "📭": "",
        "🚀": "[!]",
        "💰": "$",
        "🟢": "(E)",
        "🟡": "(M)",
        "🔴": "(H)",
        "⚫": "(X)"
    }
    for old, new in replacements.items():
        content = content.replace(old, new)
        
    # Tab labels specifically (they had emojis)
    content = content.replace('" Missions"', '"Missions"')
    content = content.replace('" Contracts"', '"Contracts"')
    content = content.replace('" Profile"', '"Profile"')
    content = content.replace('" Notifications"', '"Notifications"')
    content = content.replace('" Settings"', '"Settings"')

    # 2. Add scrollbar flattening in InitStyles
    scrollbar_code = """
            GUI.skin.verticalScrollbar = new GUIStyle(GUI.skin.verticalScrollbar) {
                normal = { background = GKSkin.MakeTex(2, 2, new Color(0.08f, 0.08f, 0.1f, 1f)) },
                border = new RectOffset(0, 0, 0, 0), margin = new RectOffset(0, 0, 0, 0), fixedWidth = 12
            };
            GUI.skin.verticalScrollbarThumb = new GUIStyle(GUI.skin.verticalScrollbarThumb) {
                normal = { background = GKSkin.MakeTex(2, 2, new Color(0.2f, 0.2f, 0.25f, 1f)) },
                hover = { background = GKSkin.MakeTex(2, 2, new Color(0.3f, 0.3f, 0.38f, 1f)) },
                active = { background = GKSkin.MakeTex(2, 2, new Color(0.3f, 0.3f, 0.38f, 1f)) },
                border = new RectOffset(0, 0, 0, 0), margin = new RectOffset(0, 0, 0, 0), fixedWidth = 12
            };
            stylesReady = true;"""
    content = content.replace("stylesReady = true;", scrollbar_code)
    
    # 3. Adjust row element widths
    content = content.replace('GUILayout.Width(120)', 'GUILayout.Width(110)') # Sender Name
    # We leave subject as flexible (button without width limit) or let it fill
    # Button width 25, height 20 for Del/Res is fine
    # Wait, the date is 45, the amount is 60.

    with open("/home/ayd/Desktop/GK-DW/KSP Mod Side/GeneKerman/UI/MainWindow.cs", "w", encoding="utf-8") as f:
        f.write(content)

def fix_linkwindow():
    with open("/home/ayd/Desktop/GK-DW/KSP Mod Side/GeneKerman/UI/LinkWindow.cs", "r", encoding="utf-8") as f:
        content = f.read()

    # Replace emojis
    content = content.replace("🔗", "[Link]")
    content = content.replace("✅", "(Ok)")
    content = content.replace("❌", "(No)")
    content = content.replace("⚙️", "")

    with open("/home/ayd/Desktop/GK-DW/KSP Mod Side/GeneKerman/UI/LinkWindow.cs", "w", encoding="utf-8") as f:
        f.write(content)

fix_mainwindow()
fix_linkwindow()
