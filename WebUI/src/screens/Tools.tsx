import { useCallback, useEffect, useState } from "react";
import { Flag, Loader2, Save, Send, UserPlus, Users } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { PlayerPicker } from "@/components/PlayerPicker";
import {
  BridgeError,
  type Corp,
  type FriendActionResult,
  type FriendList,
  api,
  runJob,
} from "@/lib/bridge";
import { cn } from "@/lib/utils";

interface CraftState {
  scene: string;
  editorCraft: string;
  editorType: string;
  editorParts: number;
  editorSaved: boolean;
  activeVessel: string;
  /** Why the flying vessel cannot be handed over right now; empty when it can, and
   *  always empty when nothing is being flown. */
  vesselSendBlock: string;
}

type Outcome = { ok: boolean; message: string } | null;

export function Tools({ refreshKey }: { refreshKey: number }) {
  const [craft, setCraft] = useState<CraftState | null>(null);
  // Bumped whenever the friend list changes, so the quicksend picker beside it
  // refetches. The two cards are the same list read twice — one to change it, one
  // to send from it — and a picker that kept showing the old set would offer people
  // the server now refuses.
  const [friendsKey, setFriendsKey] = useState(0);

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
      <div className="space-y-4">
        <Quicksend craft={craft} friendsKey={friendsKey} />
        <Friends refreshKey={refreshKey} onChanged={() => setFriendsKey((k) => k + 1)} />
      </div>
      <div className="space-y-4">
        <ExportCraft craft={craft} />
        <ImportFlag />
      </div>
    </div>
  );
}

function Quicksend({ craft, friendsKey }: { craft: CraftState | null; friendsKey: number }) {
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

  // Only a live vessel can be blocked: a blueprint takes nothing out of the save and
  // is sendable in any flight state.
  const vesselBlock = kind === "vessel" ? craft?.vesselSendBlock || "" : "";
  const canSend = !!kind && !vesselBlock;

  const blocked =
    craft?.editorCraft && !craft.editorSaved
      ? `Save '${craft.editorCraft}' in KSP first: there's no file to send yet.`
      : "Fly a vessel, or open a saved craft in the editor, to send it.";

  async function send() {
    if (!recipient || !kind || !canSend) return;

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

        {kind === "vessel" &&
          (vesselBlock ? (
            /* Instead of the permanence line, not alongside it: while the send is
               refused, what the player needs is the thing to go and fix. */
            <p className="text-xs text-muted-foreground">{vesselBlock}</p>
          ) : (
            /* A live vessel send is a hand-over, and the player must read that
               BEFORE pressing Send — same warning as the in-game sidebar. */
            <p className="text-xs text-muted-foreground">
              This hands the vessel over: it and its crew leave your save once sent (the ship
              you&apos;re flying goes when you leave it). If your friend declines, it comes back.
            </p>
          ))}

        <PlayerPicker
          value={recipient?.owner_id ?? ""}
          onChange={setRecipient}
          source="friends"
          refreshKey={friendsKey}
          emptyLabel="No friends yet. Add one below, by Boundless username or from your Discord server."
        />

        <Button size="sm" onClick={send} disabled={busy || !canSend || !recipient}>
          {busy ? <Loader2 className="size-4 animate-spin" /> : <Send className="size-4" />}
          {busy ? "Sending…" : recipient ? `Send to ${recipient.owner_name}` : "Pick a friend"}
        </Button>

        <Result outcome={outcome} />
      </CardContent>
    </Card>
  );
}

/**
 * The friend list, and the two ways to grow it.
 *
 * Quicksend can only reach friends — the server refuses anyone else — so this card
 * sits directly under it. A **Boundless account** is added by the permanent username
 * it claimed; a **Discord player** whose username you do not know is added from the
 * server roster. Both produce the same mutual friendship, and nothing downstream can
 * tell which way a friend arrived.
 */
function Friends({ refreshKey, onChanged }: { refreshKey: number; onChanged: () => void }) {
  const [data, setData] = useState<FriendList | null>(null);
  const [name, setName] = useState("");
  const [pick, setPick] = useState<Corp | null>(null);
  const [showRoster, setShowRoster] = useState(false);
  const [busy, setBusy] = useState(false);
  const [outcome, setOutcome] = useState<Outcome>(null);

  const load = useCallback(async () => {
    try {
      setData(await api.get<FriendList>("/api/v1/friends"));
    } catch {
      setData(null);
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load, refreshKey]);

  /** One call for every mutation: the reply shape and the follow-up are identical,
   *  and a refusal is usually "that is no longer there" — which means this list is
   *  the thing that is out of date, so it reloads either way. */
  async function act(run: () => Promise<FriendActionResult>) {
    setBusy(true);
    setOutcome(null);
    try {
      const res = await run();
      setOutcome({ ok: res.success, message: res.message });
      if (res.success) {
        setName("");
        setPick(null);
        onChanged();
      }
    } catch (e) {
      setOutcome({
        ok: false,
        message: e instanceof BridgeError ? e.message : "That didn't work.",
      });
    } finally {
      setBusy(false);
      void load();
    }
  }

  const add = (body: { username?: string; user_id?: string }) =>
    act(() => api.post<FriendActionResult>("/api/v1/friends/request", body));

  const verb = (id: string, action: "accept" | "decline" | "remove") =>
    act(() => api.post<FriendActionResult>(`/api/v1/friends/${encodeURIComponent(id)}/${action}`, {}));

  return (
    <Card>
      <CardHeader className="pb-3">
        <CardTitle className="flex items-center gap-2 text-base">
          <Users className="size-4" /> Friends
        </CardTitle>
      </CardHeader>
      <CardContent className="space-y-3">
        <p className="text-xs text-muted-foreground">
          You can only quicksend craft to friends, both ways, once they accept. A friendship
          is between two people, so it works across Discord servers and with players who only
          have a Boundless account.
        </p>

        <div className="flex gap-2">
          <input
            value={name}
            onChange={(e) => setName(e.target.value)}
            placeholder="Boundless username"
            className="h-9 w-full flex-1 rounded-md border border-border bg-transparent px-2 text-sm"
          />
          <Button
            size="sm"
            onClick={() => add({ username: name.trim() })}
            disabled={busy || !name.trim()}
          >
            <UserPlus className="size-4" /> Add
          </Button>
        </div>

        <Button size="sm" variant="ghost" onClick={() => setShowRoster((v) => !v)}>
          {showRoster ? "Hide server list" : "Or pick from your Discord server"}
        </Button>

        {showRoster && (
          <div className="space-y-2">
            <PlayerPicker
              value={pick?.owner_id ?? ""}
              onChange={setPick}
              emptyLabel="Nobody else has linked KSP in this server yet."
            />
            <Button
              size="sm"
              variant="secondary"
              onClick={() => pick && add({ user_id: pick.owner_id })}
              disabled={busy || !pick}
            >
              {pick ? `Send request to ${pick.owner_name}` : "Pick a player"}
            </Button>
          </div>
        )}

        {!!data?.incoming?.length && (
          <FriendGroup title={`Requests for you (${data.incoming.length})`}>
            {data.incoming.map((f) => (
              <FriendRow key={f.user_id} name={f.name} handle={f.username} level={f.level}>
                <Button size="sm" onClick={() => verb(f.user_id, "accept")} disabled={busy}>
                  Accept
                </Button>
                <Button
                  size="sm"
                  variant="ghost"
                  onClick={() => verb(f.user_id, "decline")}
                  disabled={busy}
                >
                  Decline
                </Button>
              </FriendRow>
            ))}
          </FriendGroup>
        )}

        {!!data?.outgoing?.length && (
          <FriendGroup title={`Waiting on them (${data.outgoing.length})`}>
            {data.outgoing.map((f) => (
              <FriendRow key={f.user_id} name={f.name} handle={f.username} level={f.level}>
                {/* "Cancel" rather than "Decline" for the same edit on the server:
                    withdrawing your own request and turning down someone else's are
                    one operation, and only the word differs. */}
                <Button
                  size="sm"
                  variant="ghost"
                  onClick={() => verb(f.user_id, "decline")}
                  disabled={busy}
                >
                  Cancel
                </Button>
              </FriendRow>
            ))}
          </FriendGroup>
        )}

        <FriendGroup title={`Friends (${data?.friends?.length ?? 0})`}>
          {data?.friends?.length ? (
            data.friends.map((f) => (
              <FriendRow key={f.user_id} name={f.name} handle={f.username} level={f.level}>
                <Button
                  size="sm"
                  variant="ghost"
                  onClick={() => verb(f.user_id, "remove")}
                  disabled={busy}
                >
                  Remove
                </Button>
              </FriendRow>
            ))
          ) : (
            <p className="px-2 py-3 text-sm text-muted-foreground">
              {data === null
                ? "Couldn't load your friends."
                : "No friends yet. Add one above, and they can send you craft as soon as they accept."}
            </p>
          )}
        </FriendGroup>

        <Result outcome={outcome} />
      </CardContent>
    </Card>
  );
}

function FriendGroup({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div className="space-y-1">
      <p className="text-xs font-medium text-muted-foreground">{title}</p>
      <div className="space-y-1 rounded-md border border-border p-1">{children}</div>
    </div>
  );
}

function FriendRow({
  name,
  handle,
  level,
  children,
}: {
  name: string;
  handle?: string;
  level?: number;
  children: React.ReactNode;
}) {
  return (
    <div className="flex items-center gap-2 rounded-md px-2 py-1.5">
      <span className="min-w-0 flex-1">
        <span className="block truncate text-sm font-medium">{name}</span>
        {handle && <span className="block truncate text-xs text-muted-foreground">@{handle}</span>}
      </span>
      {!!level && (
        <span className="shrink-0 rounded-full border border-border px-2 py-0.5 text-[11px] font-medium text-muted-foreground">
          Lv {level}
        </span>
      )}
      {children}
    </div>
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
