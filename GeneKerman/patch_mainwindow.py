import re

with open("/home/ayd/Desktop/GK-DW/KSP Mod Side/GeneKerman/UI/MainWindow.cs", "r") as f:
    content = f.read()

# 1. Add fields and Load/Save Trash
fields_addition = """        // Trash state
        private HashSet<string> trashedContracts = new HashSet<string>();
        private bool showTrash = false;
        private HashSet<string> collapsedWeeks = new HashSet<string>();

        private void LoadTrash()
        {
            string path = System.IO.Path.Combine(GeneKermanMod.PluginDataPath, "trashed_contracts.txt");
            trashedContracts.Clear();
            if (System.IO.File.Exists(path))
            {
                foreach (string line in System.IO.File.ReadAllLines(path))
                {
                    if (!string.IsNullOrEmpty(line)) trashedContracts.Add(line.Trim());
                }
            }
        }

        private void SaveTrash()
        {
            string path = System.IO.Path.Combine(GeneKermanMod.PluginDataPath, "trashed_contracts.txt");
            System.IO.File.WriteAllLines(path, new List<string>(trashedContracts).ToArray());
        }

        public void OnOpen()"""

content = content.replace("        public void OnOpen()", fields_addition)

# 2. Modify OnOpen to call LoadTrash()
onopen_mod = """        public void OnOpen()
        {
            LoadTrash();
            RefreshAll();
        }"""
content = content.replace("        public void OnOpen()\n        {\n            RefreshAll();\n        }", onopen_mod)

# 3. Flatten styles
style_mod1 = """            windowStyle = new GUIStyle(GUI.skin.box)
            {
                normal = { background = GKSkin.MakeTex(2, 2, new Color(0.12f, 0.12f, 0.15f, 1f)) },
                padding = new RectOffset(10, 10, 10, 10),
                border = new RectOffset(0, 0, 0, 0)
            };"""
content = re.sub(r'windowStyle = new GUIStyle\(GUI\.skin\.box\)\s*\{\s*normal = \{ background = GKSkin\.MakeTex[^}]+\},\s*padding = new RectOffset\(10, 10, 10, 10\)\s*\};', style_mod1, content)

style_mod2 = """            boxDarkStyle = new GUIStyle(GUI.skin.box)
            {
                normal = { background = GKSkin.MakeTex(2, 2, new Color(0.08f, 0.08f, 0.11f, 1f)) },
                padding = new RectOffset(10, 10, 8, 8), margin = new RectOffset(0, 0, 3, 3),
                border = new RectOffset(0, 0, 0, 0)
            };"""
content = re.sub(r'boxDarkStyle = new GUIStyle\(GUI\.skin\.box\)\s*\{\s*normal = \{ background = GKSkin\.MakeTex[^}]+\},\s*padding = new RectOffset\(10, 10, 8, 8\), margin = new RectOffset\(0, 0, 3, 3\)\s*\};', style_mod2, content)

style_mod3 = """            var rowBg = GKSkin.MakeTex(2, 2, new Color(0.1f, 0.1f, 0.14f, 1f));
            mailRowStyle = new GUIStyle(GUI.skin.box)
            {
                normal = { background = rowBg },
                padding = new RectOffset(4, 4, 3, 3),
                margin = new RectOffset(0, 0, 1, 1),
                fixedHeight = 26,
                border = new RectOffset(0, 0, 0, 0)
            };"""
content = re.sub(r'var rowBg = GKSkin\.MakeTex[^;]+;\s*mailRowStyle = new GUIStyle\(GUI\.skin\.box\)\s*\{\s*normal = \{ background = rowBg \},\s*padding = new RectOffset\(4, 4, 3, 3\),\s*margin = new RectOffset\(0, 0, 1, 1\),\s*fixedHeight = 26,\s*\};', style_mod3, content)

# Write back
with open("/home/ayd/Desktop/GK-DW/KSP Mod Side/GeneKerman/UI/MainWindow.cs", "w") as f:
    f.write(content)
