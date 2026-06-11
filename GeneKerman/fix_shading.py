import re

def fix_mainwindow():
    with open("/home/ayd/Desktop/GK-DW/KSP Mod Side/GeneKerman/UI/MainWindow.cs", "r", encoding="utf-8") as f:
        content = f.read()

    # Inject global button and textfield flattening into InitStyles
    global_styles = """
            GUI.skin.button = new GUIStyle(GUI.skin.button) {
                normal = { background = GKSkin.MakeTex(2, 2, new Color(0.15f, 0.15f, 0.2f, 1f)) },
                hover = { background = GKSkin.MakeTex(2, 2, new Color(0.2f, 0.2f, 0.28f, 1f)) },
                active = { background = GKSkin.MakeTex(2, 2, new Color(0.2f, 0.2f, 0.28f, 1f)) },
                border = new RectOffset(0, 0, 0, 0)
            };
            GUI.skin.textField = new GUIStyle(GUI.skin.textField) {
                normal = { background = GKSkin.MakeTex(2, 2, new Color(0.08f, 0.08f, 0.11f, 1f)), textColor = Color.white },
                focused = { background = GKSkin.MakeTex(2, 2, new Color(0.1f, 0.15f, 0.2f, 1f)), textColor = Color.white },
                border = new RectOffset(0, 0, 0, 0), padding = new RectOffset(5, 5, 5, 5)
            };
            stylesReady = true;"""
            
    content = content.replace("stylesReady = true;", global_styles)
    
    # In MainWindow.cs Settings tab, use the new global textfield/button to avoid mismatch
    # Remove the inline custom textFieldStyle we added earlier so it falls back to the clean global one
    content = re.sub(r'serverUrlInput = GUILayout\.TextField\(serverUrlInput, new GUIStyle\(GUI\.skin\.textField\).*?\}\);', 
                     'serverUrlInput = GUILayout.TextField(serverUrlInput, GUILayout.Height(24));', 
                     content, flags=re.DOTALL)
    
    # Also replace 📥 with [+] just in case
    content = content.replace("📥", "[+]")
    content = content.replace("🔓", "[U]")

    with open("/home/ayd/Desktop/GK-DW/KSP Mod Side/GeneKerman/UI/MainWindow.cs", "w", encoding="utf-8") as f:
        f.write(content)

def fix_linkwindow():
    with open("/home/ayd/Desktop/GK-DW/KSP Mod Side/GeneKerman/UI/LinkWindow.cs", "r", encoding="utf-8") as f:
        content = f.read()

    # Same for LinkWindow.cs InitStyles (if it has it, or just inject at start of Draw)
    # LinkWindow doesn't have a robust InitStyles, it relies on MainWindow styles or standard skin
    # We will just inject it at the beginning of Draw()
    
    draw_patch = """        public void Draw()
        {
            GUI.skin.button = new GUIStyle(GUI.skin.button) {
                normal = { background = GKSkin.MakeTex(2, 2, new Color(0.15f, 0.15f, 0.2f, 1f)) },
                hover = { background = GKSkin.MakeTex(2, 2, new Color(0.2f, 0.2f, 0.28f, 1f)) },
                active = { background = GKSkin.MakeTex(2, 2, new Color(0.2f, 0.2f, 0.28f, 1f)) },
                border = new RectOffset(0, 0, 0, 0)
            };
            GUI.skin.textField = new GUIStyle(GUI.skin.textField) {
                normal = { background = GKSkin.MakeTex(2, 2, new Color(0.08f, 0.08f, 0.11f, 1f)), textColor = Color.white },
                focused = { background = GKSkin.MakeTex(2, 2, new Color(0.1f, 0.15f, 0.2f, 1f)), textColor = Color.white },
                border = new RectOffset(0, 0, 0, 0), padding = new RectOffset(5, 5, 5, 5)
            };
            
            windowRect = GUILayout.Window("""
            
    content = content.replace("        public void Draw()\n        {\n            windowRect = GUILayout.Window(", draw_patch)
    
    # Clean up the inline styles we previously added to LinkWindow to avoid conflict
    content = re.sub(r'var textFieldStyle = new GUIStyle\(GUI\.skin\.textField\).*?;\n\s*var tabStyle = new GUIStyle\(GUI\.skin\.button\).*?;', '', content, flags=re.DOTALL)
    content = content.replace('serverUrlInput = GUILayout.TextField(serverUrlInput, textFieldStyle);', 'serverUrlInput = GUILayout.TextField(serverUrlInput, GUILayout.Height(24));')
    content = content.replace('if (GUILayout.Button("Set", tabStyle, GUILayout.Width(40)))', 'if (GUILayout.Button("Set", GUILayout.Width(40), GUILayout.Height(24)))')

    with open("/home/ayd/Desktop/GK-DW/KSP Mod Side/GeneKerman/UI/LinkWindow.cs", "w", encoding="utf-8") as f:
        f.write(content)

fix_mainwindow()
fix_linkwindow()
