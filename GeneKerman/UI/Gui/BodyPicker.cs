/*
 * UI/Gui/BodyPicker.cs – Pick a celestial body: search, modded flag, selection.
 *
 * The body list comes from ContractCreation.ScanRescueContext (the real FlightGlobals
 * list, so planet packs are included and flagged), which is why this takes its items
 * rather than fetching: the caller already holds a scan and a second one would be a
 * second answer to the same question.
 *
 * Same contract as PlayerPicker, and for the same reason: the list rebuilds itself, not
 * the panel. Typing in the search box must filter as you type, and rebuilding the panel
 * would destroy the box being typed into — so RefreshList() refills the host in place.
 *
 * The list does not scroll. It is a fixed-height host that clips, so with a big planet
 * pack installed the matches past MaxRows are counted rather than drawn and the search
 * box is how you reach them. A nested ScrollRect inside the panel's own scroll view
 * would fight it for the wheel.
 */

using System;
using System.Collections.Generic;
using UnityEngine;

namespace GeneKerman.UI.Gui
{
    internal sealed class BodyPicker
    {
        private const float ListHeight = 150f;
        private const int MaxRows = 6;

        private readonly List<ContractCreation.BodyInfo> bodies = new List<ContractCreation.BodyInfo>();
        private string query = "";
        private El listHost;
        private El chosenHost;
        private Action markDirty;

        public string Selected { get; private set; }

        /// <summary>True when the chosen body came from a planet pack — the rescuer
        /// needs it installed, so both create forms warn about it.</summary>
        public bool SelectedIsModded
        {
            get
            {
                foreach (var b in bodies)
                    if (b.Name == Selected) return b.Modded;
                return false;
            }
        }

        public void Attach(Action onMarkDirty) => markDirty = onMarkDirty;

        /// <summary>Replace the list. Keeps the current selection when that body still
        /// exists (a rescan shouldn't silently retarget the contract), and otherwise
        /// falls back to <paramref name="preferred"/> — normally the vessel's own body.</summary>
        public void SetBodies(List<ContractCreation.BodyInfo> items, string preferred)
        {
            bodies.Clear();
            if (items != null) bodies.AddRange(items);

            bool stillThere = false;
            foreach (var b in bodies)
                if (b.Name == Selected) { stillThere = true; break; }

            if (!stillThere)
            {
                Selected = null;
                foreach (var b in bodies)
                    if (b.Name == preferred) { Selected = preferred; break; }
                if (Selected == null && bodies.Count > 0) Selected = bodies[0].Name;
            }
            RefreshList();
            RefreshChosen();
        }

        public void Reset()
        {
            bodies.Clear();
            query = "";
            Selected = null;
            listHost = null;
            chosenHost = null;
        }

        public void Build(El parent)
        {
            var box = UIF.Box(parent, "BodyPicker").Column(Theme.Space2);

            // Stated outside the list, because the list is the one place it can go
            // missing: the search filters it, and MaxRows truncates it, so the chosen
            // body is routinely not among the rows on screen. No Clear — a rescue is
            // always somewhere, and "no body" is not an answer this form accepts.
            chosenHost = UIF.Box(box, "Chosen").Column(0);
            RefreshChosen();

            var field = UIF.TextField(box, query, "Search bodies…", 28);
            field.E.PrefW(0).Flex(1f);
            field.OnChanged(s => { query = s; RefreshList(); });

            listHost = UIF.Box(box, "List").Column(1).Pad(Theme.Space1).H(ListHeight)
                          .Bg(Theme.Alpha(Theme.Muted, 0.35f), Theme.RadiusSm, Theme.Border);
            RefreshList();
        }

        /// <summary>Refill the "which body" line in place. Same host-is-gone contract
        /// as RefreshList.</summary>
        private void RefreshChosen()
        {
            if (chosenHost == null || chosenHost.Go == null) return;

            chosenHost.ClearChildren();
            // Inactive rather than empty, so it costs the column no spacing while
            // there is nothing to say — see PlayerPicker.RefreshChosen.
            bool has = !string.IsNullOrEmpty(Selected);
            chosenHost.Active(has);
            if (!has) return;

            UIF.Selection(chosenHost, "Target", Selected);
        }

        /// <summary>Refill in place. Safe when the host is gone — a rebuild or a scene
        /// change destroys it, and writing into a destroyed hierarchy throws.</summary>
        private void RefreshList()
        {
            if (listHost == null || listHost.Go == null) return;
            listHost.ClearChildren();

            if (bodies.Count == 0)
            {
                UIF.Muted(listHost, "No bodies read from the game yet.").Body();
                return;
            }

            var shown = new List<ContractCreation.BodyInfo>();
            string q = (query ?? "").Trim();
            foreach (var b in bodies)
                if (q.Length == 0 || b.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                    shown.Add(b);

            if (shown.Count == 0)
            {
                UIF.Muted(listHost, "No body matches \"" + q + "\".").Body();
                return;
            }

            int drawn = Math.Min(shown.Count, MaxRows);
            for (int i = 0; i < drawn; i++) Row(listHost, shown[i]);
            if (shown.Count > drawn)
                UIF.Muted(listHost, (shown.Count - drawn) + " more; type to narrow.").Body();
        }

        private void Row(El parent, ContractCreation.BodyInfo b)
        {
            var info = b;
            bool selected = Selected == b.Name;

            // MinH rather than H: a fixed height squeezes the label, and a squeezed
            // label using Ellipsis renders nothing at all.
            var row = UIF.ClickableRow(parent, () =>
                         {
                             Selected = info.Name;
                             RefreshList();
                             RefreshChosen();
                             markDirty?.Invoke();
                         }, selected, Theme.RadiusSm)
                         .Row(Theme.Space2)
                         .Pad(Theme.Space2, Theme.Space1, Theme.Space1, Theme.Space1)
                         .ChildAlign(TextAnchor.MiddleLeft)
                         .MinH(24);

            UIF.Label(row, b.Name, Theme.FontSm, selected ? Theme.AccentForeground : (Color?)null)
               .Bold(selected).E.PrefW(0).Flex(1f);
            if (b.Modded) UIF.Badge(row, "mod", Theme.Status("warning"));
        }
    }
}
