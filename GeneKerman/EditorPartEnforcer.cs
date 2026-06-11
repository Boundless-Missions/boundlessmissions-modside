using System;
using System.Collections.Generic;
using KSP.UI.Screens;
using UnityEngine;

namespace GeneKerman
{
    [KSPAddon(KSPAddon.Startup.EditorAny, false)]
    public class EditorPartEnforcer : MonoBehaviour
    {
        public static EditorPartEnforcer Instance { get; private set; }

        public string ActiveContractId { get; private set; }
        private HashSet<string> allowedMods;
        private List<string> excludePaths; // partUrl prefixes to block even if folder is allowed
        private bool isEnforcing;

        void Awake()
        {
            Instance = this;
        }

        public void EnforceModlist(string contractId, string modlistCsv)
        {
            ActiveContractId = contractId;
            if (string.IsNullOrEmpty(modlistCsv))
            {
                allowedMods = null;
                excludePaths = null;
                isEnforcing = false;
            }
            else
            {
                allowedMods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                excludePaths = new List<string>();
                foreach (var token in modlistCsv.Split(','))
                {
                    string t = token.Trim();
                    if (t.StartsWith("-")) excludePaths.Add(t.Substring(1));
                    else allowedMods.Add(t);
                }
                isEnforcing = true;
            }

            RefreshEditor();
        }

        public void StopEnforcing()
        {
            ActiveContractId = null;
            isEnforcing = false;
            allowedMods = null;
            excludePaths = null;
            RefreshEditor();
        }

        public bool IsEnforcing()
        {
            return isEnforcing;
        }

        private void RefreshEditor()
        {
            if (PartCategorizer.Instance != null)
            {
                // Unselect all filters to force a rebuild
                PartCategorizer.Instance.filters.ForEach(f => f.button.activeButton.SetState(KSP.UI.UIRadioButton.State.False, KSP.UI.UIRadioButton.CallType.APPLICATION, null));
                
                // Select the 'Filter by Function' tab to repopulate the list
                var allFilter = PartCategorizer.Instance.filters.Find(f => f.button.categoryName == "Filter by Function");
                if (allFilter != null) allFilter.button.activeButton.SetState(KSP.UI.UIRadioButton.State.True, KSP.UI.UIRadioButton.CallType.APPLICATION, null);
                
                // Unity UI layout refresh hack for the EditorPartList
                if (EditorPartList.Instance != null)
                {
                    EditorPartList.Instance.Refresh();
                }
            }
        }

        void Start()
        {
            EditorPartListFilter<AvailablePart> filter = new EditorPartListFilter<AvailablePart>("GeneKermanContractFilter", PartFilterFunc);
            EditorPartList.Instance.ExcludeFilters.AddFilter(filter);
        }

        private bool PartFilterFunc(AvailablePart part)
        {
            if (!isEnforcing || allowedMods == null) return true;

            string modFolder = GetModFolder(part);
            if (string.IsNullOrEmpty(modFolder)) return true;

            if (!allowedMods.Contains(modFolder)) return false;

            // Check exclusion sub-paths (e.g. "-Squad/Expansions" to block DLC)
            if (excludePaths != null && excludePaths.Count > 0)
            {
                string url = part.partUrl ?? "";
                foreach (var excl in excludePaths)
                    if (url.StartsWith(excl, StringComparison.OrdinalIgnoreCase))
                        return false;
            }

            return true;
        }

        private string GetModFolder(AvailablePart part)
        {
            if (part == null || string.IsNullOrEmpty(part.partUrl)) return null;
            
            string[] parts = part.partUrl.Split('/');
            if (parts.Length > 0)
                return parts[0];
                
            return null;
        }
    }
}
