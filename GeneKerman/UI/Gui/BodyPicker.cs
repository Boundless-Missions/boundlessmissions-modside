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
 * The list scrolls, exactly like PlayerPicker's: a fixed-height host with a nested
 * ScrollView inside, so every body is reachable by wheel as well as by search — a big
 * planet pack used to be cut off at six rows with a "N more; type to narrow" line that
 * overflowed into the fields below. ScrollForwarder/ScrollRelay already make a nested
 * ScrollRect coexist with the panel's own scroll view (the wheel goes to whichever list
 * the pointer is over), so the old no-scroll rule no longer applies.
 */

using System;
using System.Collections.Generic;
using UnityEngine;

namespace GeneKerman.UI.Gui
{
    internal sealed class BodyPicker
    {
        private const float ListHeight = 150f;

        private readonly List<ContractCreation.BodyInfo> bodies = new List<ContractCreation.BodyInfo>();
        private string query = "";
        private El listHost;
        private El chosenHost;
        private Action markDirty;

        /// <summary>Names this picker's scroll offset (see ScrollMemory). Per instance,
        /// like PlayerPicker's: two forms each holding a picker are two lists.</summary>
        private readonly string scrollKey = "body-picker#" + (++instances);
        private static int instances;

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
            // A fresh form is a fresh list; resuming the last one's offset would open
            // a new rescue part-way down the last one's planet pack.
            ScrollMemory.Forget(scrollKey);
        }

        public void Build(El parent)
        {
            var box = UIF.Box(parent, "BodyPicker").Column(Theme.Space2);

            // Stated outside the list, because the list is the one place it can go
            // missing: the search filters it, and the chosen row may be scrolled out
            // of view. No Clear — a rescue is always somewhere, and "no body" is not
            // an answer this form accepts.
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

            // Every match, in a scroll view — the search narrows, the wheel reaches.
            El content;
            UIF.ScrollView(listHost, out content, scrollKey).Flex(1f, 1f);
            foreach (var b in shown) Row(content, b);
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
