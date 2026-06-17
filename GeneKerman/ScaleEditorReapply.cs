/*
 * ScaleEditorReapply.cs – Keep GeneKermanScale geometry intact across editor undo/redo.
 *
 * GeneKermanScale applies its scaled model + attach-node geometry at OnLoad/OnStart and
 * "settles" it over several frames via a coroutine started in OnStart (see the comment in
 * GeneKermanScale.OnStart): a single apply is partially overwritten by KSP's own surface-
 * attach re-projection, which finishes a few frames after the ship is built.
 *
 * Editor undo/redo tears down and rebuilds the part tree and re-runs that surface-attach
 * re-projection, but does NOT re-run OnStart — so the settle coroutine never fires and any
 * GK-scaled, surface-attached child reverts onto its prefab/unscaled node positions and is
 * flung off the vessel (radial parts in particular). The submit-side pos rewrite in
 * ScaleBridge masks this on first load (it patches each part's top-level `pos`) but not after
 * a rebuild, which is why an imported craft looks correct until the first ctrl+z.
 *
 * This persistent editor addon listens for undo/redo and re-asserts the geometry on every
 * GK-active part — surviving the part-tree rebuild because it lives on the scene, not on the
 * (recreated) parts. Re-asserting is idempotent, so a stray fire is harmless.
 */

using System.Collections;
using UnityEngine;

namespace GeneKerman
{
    [KSPAddon(KSPAddon.Startup.EditorAny, false)]
    public class ScaleEditorReapply : MonoBehaviour
    {
        void Start()
        {
            GameEvents.onEditorUndo.Add(OnUndoRedo);
            GameEvents.onEditorRedo.Add(OnUndoRedo);
        }

        void OnDestroy()
        {
            GameEvents.onEditorUndo.Remove(OnUndoRedo);
            GameEvents.onEditorRedo.Remove(OnUndoRedo);
        }

        private void OnUndoRedo(ShipConstruct ship)
        {
            // The ship handed to us is mid-rebuild; wait a frame so every part exists (and KSP's
            // first surface re-projection pass has run) before we re-assert over it.
            StartCoroutine(ReassertAfterFrame());
        }

        private IEnumerator ReassertAfterFrame()
        {
            yield return null;

            ShipConstruct ship = EditorLogic.fetch?.ship;
            if (ship?.parts == null) yield break;

            int n = 0;
            foreach (Part p in ship.parts)
            {
                GeneKermanScale gk = p?.FindModuleImplementing<GeneKermanScale>();
                if (gk == null) continue;
                gk.RequestGeometryReassert();
                n++;
            }

            if (n > 0)
                Debug.Log($"[GeneKerman] ScaleEditorReapply: re-asserted scale geometry on {n} part(s) after undo/redo.");
        }
    }
}
