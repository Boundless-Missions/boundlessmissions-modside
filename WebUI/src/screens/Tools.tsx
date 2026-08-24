import { useCallback, useEffect, useState } from "react";
import { Flag, Loader2, Save, Send } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { PlayerPicker } from "@/components/PlayerPicker";
import { BridgeError, type Corp, api, runJob } from "@/lib/bridge";
import { cn } from "@/lib/utils";

interface CraftState {
  scene: string;
  editorCraft: string;
  editorType: string;
  editorParts: number;
  editorSaved: boolean;
  activeVessel: string;
}

type Outcome = { ok: boolean; message: string } | null;

export function Tools({ refreshKey }: { refreshKey: number }) {
  const [craft, setCraft] = useState<CraftState | null>(null);

  const loadCraft = useCallback(async () => {
    try {
      setCraft(await api.get<CraftState>("/gk/craft/current"));
    } catch {
      setCraft(null);
    }
  }, []);

  useEffect(() => {
    void loadCraft();
    // The player switches scenes and loads crafts while this page is open, and none of
    // that produces an event — so poll. It is a local call, no network involved.
    const t = setInterval(loadCraft, 4000);
    return () => clearInterval(t);
  }, [loadCraft, refreshKey]);

  return (
    <div className="grid gap-4 lg:grid-cols-2 lg:items-start">
      <Quicksend craft={craft} />
      <div className="space-y-4">
        <ExportCraft craft={craft} />
        <ImportFlag />
      </div>
    </div>
  );
}

function Quicksend({ craft }: { craft: CraftState | null }) {
  const [recipient, setRecipient] = useState<Corp | null>(null);
  const [busy, setBusy] = useState(false);
  const [outcome, setOutcome] = useState<Outcome>(null);

  // A flying vessel goes as a live vessel (crew and all); otherwise a saved editor
  // craft goes as a blueprint. Mirrors the classic window's rule.
  const kind: "vessel" | "craft" | null = craft?.activeVessel
    ? "vessel"
    : craft?.editorCraft && craft.editorSaved
      ? "craft"
      : null;

  const what =
    kind === "vessel"
      ? `${craft!.activeVessel}: sent as a live vessel, crew included.`
      : kind === "craft"
        ? `${craft!.editorCraft} (${craft!.editorType}, ${craft!.editorParts} parts): sent as a blueprint.`
        : null;

  const blocked =
    craft?.editorCraft && !craft.editorSaved
      ? `Save '${craft.editorCraft}' in KSP first: there's no file to send yet.`
      : "Fly a vessel, or open a saved craft in the editor, to send it.";

  async function send() {
    if (!recipient || !kind) return;

    setBusy(true);
    setOutcome(null);
    try {
      const job = await runJob("/gk/actions/quicksend", {
        recipient_id: recipient.owner_id,
        recipient_name: recipient.owner_name,
        kind,
      });
      setOutcome({ ok: job.state === "done", message: job.message });
    } catch (e) {
      setOutcome({ ok: false, message: e instanceof BridgeError ? e.message : "Send failed." });
    } finally {
      setBusy(false);
    }
  }

  return (
    <Card>
      <CardHeader className="pb-3">
        <CardTitle className="flex items-center gap-2 text-base">
          <Send className="size-4" /> Quicksend to a friend
        </CardTitle>
      </CardHeader>
      <CardContent className="space-y-3">
        <p className={cn("text-sm", kind ? "" : "text-muted-foreground")}>{what ?? blocked}</p>

        {kind === "vessel" && (
          /* A live vessel send is a hand-over, and the player must read that
             BEFORE pressing Send — same warning as the in-game sidebar. */
          <p className="text-xs text-muted-foreground">
            This hands the vessel over: it and its crew leave your save once sent (the ship
            you&apos;re flying goes when you leave it). If your friend declines, it comes back.
          </p>
        )}

        <PlayerPicker
          value={recipient?.owner_id ?? ""}
          onChange={setRecipient}
          emptyLabel="No other players found to send to."
        />

        <Button size="sm" onClick={send} disabled={busy || !kind || !recipient}>
          {busy ? <Loader2 className="size-4 animate-spin" /> : <Send className="size-4" />}
          {busy ? "Sending…" : recipient ? `Send to ${recipient.owner_name}` : "Pick a player"}
        </Button>

        <Result outcome={outcome} />
      </CardContent>
    </Card>
  );
}

function ExportCraft({ craft }: { craft: CraftState | null }) {
  const [busy, setBusy] = useState(false);
  const [outcome, setOutcome] = useState<Outcome>(null);

  const ready = !!craft?.editorCraft && craft.editorSaved;

  async function run() {
    setBusy(true);
    setOutcome(null);
    try {
      const res = await api.post<{ ok: boolean; path: string }>("/gk/actions/export-craft");
      setOutcome({ ok: true, message: `Saved to ${res.path}` });
    } catch (e) {
      setOutcome({
        ok: false,
        message: e instanceof BridgeError ? e.message : "Export failed.",
      });
    } finally {
      setBusy(false);
    }
  }

  return (
    <Card>
      <CardHeader className="pb-3">
        <CardTitle className="flex items-center gap-2 text-base">
          <Save className="size-4" /> Export flag-encoded craft
        </CardTitle>
      </CardHeader>
      <CardContent className="space-y-3">
        <p className="text-sm text-muted-foreground">
          Writes the loaded craft with its custom flags, mod list and thumbnail baked in, so
          they survive when you share the file.
        </p>
        <Button size="sm" variant="outline" onClick={run} disabled={busy || !ready}>
          {busy ? <Loader2 className="size-4 animate-spin" /> : <Save className="size-4" />}
          {ready ? `Export ${craft!.editorCraft}` : "Open a saved craft in KSP"}
        </Button>
        <Result outcome={outcome} />
      </CardContent>
    </Card>
  );
}

function ImportFlag() {
  const [url, setUrl] = useState("");
  const [name, setName] = useState("");
  const [busy, setBusy] = useState(false);
  const [outcome, setOutcome] = useState<Outcome>(null);

  async function run() {
    setBusy(true);
    setOutcome(null);
    try {
      const job = await runJob("/gk/actions/import-flag", { url: url.trim(), name: name.trim() });
      setOutcome({ ok: job.state === "done", message: job.message });
      if (job.state === "done") {
        setUrl("");
        setName("");
      }
    } catch (e) {
      setOutcome({ ok: false, message: e instanceof BridgeError ? e.message : "Import failed." });
    } finally {
      setBusy(false);
    }
  }

  return (
    <Card>
      <CardHeader className="pb-3">
        <CardTitle className="flex items-center gap-2 text-base">
          <Flag className="size-4" /> Import a flag
        </CardTitle>
      </CardHeader>
      <CardContent className="space-y-3">
        <p className="text-sm text-muted-foreground">
          Paste a public PNG or JPEG link to add it to your in-game flag picker.
        </p>
        <input
          value={url}
          onChange={(e) => setUrl(e.target.value)}
          placeholder="https://example.com/flag.png"
          className="h-9 w-full rounded-md border border-border bg-transparent px-2 text-sm"
        />
        <input
          value={name}
          onChange={(e) => setName(e.target.value)}
          placeholder="Name (optional)"
          className="h-9 w-full rounded-md border border-border bg-transparent px-2 text-sm"
        />
        <Button size="sm" variant="outline" onClick={run} disabled={busy || !url.trim()}>
          {busy ? <Loader2 className="size-4 animate-spin" /> : <Flag className="size-4" />}
          {busy ? "Importing…" : "Import flag"}
        </Button>
        <Result outcome={outcome} />
      </CardContent>
    </Card>
  );
}

function Result({ outcome }: { outcome: Outcome }) {
  if (!outcome) return null;
  return (
    <p className={cn("break-words text-sm", outcome.ok ? "text-primary" : "text-destructive")}>
      {outcome.message}
    </p>
  );
}
