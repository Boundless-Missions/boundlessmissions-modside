import { useCallback, useEffect, useState } from "react";
import { AlertTriangle, Loader2, RefreshCw, Wifi, WifiOff } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { ProfileCard } from "@/screens/ProfileCard";
import { Missions } from "@/screens/Missions";
import { Contracts } from "@/screens/Contracts";
import { Tools } from "@/screens/Tools";
import { Settings } from "@/screens/Settings";
import { NotificationFeed } from "@/components/NotificationFeed";
import { TabBar, useTab } from "@/components/Tabs";
import {
  BridgeError,
  type GameState,
  type Job,
  api,
  emitJob,
  openSession,
  subscribe,
} from "@/lib/bridge";

export default function App() {
  const [ready, setReady] = useState(false);
  const [fatal, setFatal] = useState<string | null>(null);
  const [state, setState] = useState<GameState | null>(null);
  const [live, setLive] = useState(false);
  const [refreshKey, setRefreshKey] = useState(0);
  const [tab, setTab] = useTab();
  // Seeded from /gk/state, then corrected by NotificationFeed with the server's
  // authoritative count — the mod cannot observe reads that went through the proxy.
  const [unread, setUnread] = useState(0);

  // The handshake must complete before any other request, so everything below waits
  // on `ready` rather than firing in parallel and eating a rack of 401s.
  useEffect(() => {
    openSession()
      .then(() => setReady(true))
      .catch(() =>
        setFatal(
          "Could not start a session. Open the interface from the toolbar button in KSP; " +
            "this link can only be used once."
        )
      );
  }, []);

  const loadState = useCallback(async () => {
    try {
      const s = await api.get<GameState>("/gk/state");
      setState(s);
      setUnread((prev) => (prev === 0 && s.unread > 0 ? s.unread : prev));
    } catch (e) {
      if (e instanceof BridgeError && e.status === 401) {
        setFatal("This session expired. Reopen the interface from the toolbar button in KSP.");
      }
    }
  }, []);

  useEffect(() => {
    if (!ready) return;
    void loadState();
    // /gk/state is cheap and local, and it is how we notice the player switching
    // scenes, unlinking, or pausing data sharing from inside the game.
    const t = setInterval(loadState, 5000);
    return () => clearInterval(t);
  }, [ready, loadState]);

  useEffect(() => {
    if (!ready) return;
    return subscribe(
      {
        notification: () => setRefreshKey((n) => n + 1),
        contracts_changed: () => setRefreshKey((n) => n + 1),
        // Fan out to whichever component is waiting on this job.
        job: (data) => emitJob(data as Job),
      },
      setLive
    );
  }, [ready]);

  if (fatal) return <Fatal message={fatal} />;

  if (!ready) {
    return (
      <Centered>
        <Loader2 className="size-5 animate-spin text-muted-foreground" />
        <p className="text-sm text-muted-foreground">Connecting to KSP…</p>
      </Centered>
    );
  }

  return (
    <div className="min-h-screen">
      <header className="sticky top-0 z-10 border-b border-border bg-background/85 backdrop-blur">
        <div className="mx-auto flex w-full max-w-[1600px] flex-wrap items-center gap-3 px-6 pb-3 pt-4">
          <div className="flex-1 min-w-0">
            <h1 className="truncate text-base font-semibold">Boundless Missions</h1>
            <p className="truncate text-xs text-muted-foreground">
              {state?.username ? `${state.username} · ` : ""}
              {sceneLabel(state?.scene)}
            </p>
          </div>

          {live ? (
            <Badge variant="secondary" className="gap-1.5">
              <Wifi className="size-3" /> Live
            </Badge>
          ) : (
            <Badge variant="outline" className="gap-1.5 text-muted-foreground">
              <WifiOff className="size-3" /> Reconnecting
            </Badge>
          )}

          <Button
            variant="ghost"
            size="icon"
            onClick={() => setRefreshKey((n) => n + 1)}
            aria-label="Refresh"
          >
            <RefreshCw className="size-4" />
          </Button>

          <div className="w-full">
            <TabBar tab={tab} onChange={setTab} unread={unread} />
          </div>
        </div>
      </header>

      <main className="mx-auto w-full max-w-[1600px] space-y-4 px-6 py-6">
        {state && !state.dataGathering && (
          <Notice>
            Data sharing is turned off in KSP, so the mod is not contacting the server.
            Re-enable it from the in-game panel to use this interface.
          </Notice>
        )}
        {state?.updateRequired && (
          <Notice>
            This copy of the mod is out of date. Some features are unavailable until you update.
          </Notice>
        )}
        {/* Normally only reachable by switching servers from Settings — a server that has
            never seen this install issues no token, so every /api call below would fail
            with a bare 401 and no explanation of why. Linking itself stays in KSP: it
            needs a code typed in the game and an approval in Discord. */}
        {state && !state.linked && (
          <Notice>
            This copy of KSP is not linked to {state.serverUrl || "this server"}. Finish linking in
            the game window, or{" "}
            <button
              onClick={() => setTab("settings")}
              className="underline underline-offset-2 hover:text-foreground"
            >
              pick a different server
            </button>
            .
          </Notice>
        )}

        {tab === "missions" && <Missions refreshKey={refreshKey} onChanged={loadState} />}
        {tab === "contracts" && <Contracts refreshKey={refreshKey} />}
        {tab === "tools" && <Tools refreshKey={refreshKey} />}
        {tab === "settings" && (
          <Settings
            refreshKey={refreshKey}
            // A server switch invalidates every piece of account data the other screens
            // are holding, so re-read local state and force them all to refetch.
            onChanged={() => {
              void loadState();
              setRefreshKey((n) => n + 1);
            }}
          />
        )}
        {tab === "profile" && (
          // Side by side once there is room. On a 16:9 screen a single stacked column
          // wastes most of the width and pushes the notification feed below the fold.
          <div className="grid gap-4 lg:grid-cols-2 lg:items-start">
            <ProfileCard refreshKey={refreshKey} onChanged={loadState} />
            <NotificationFeed
              refreshKey={refreshKey}
              onChanged={loadState}
              onUnread={setUnread}
            />
          </div>
        )}
      </main>
    </div>
  );
}

/** KSP's scene enum is not something to show a player verbatim. */
function sceneLabel(scene?: string): string {
  switch (scene) {
    case "FLIGHT":
      return "In flight";
    case "EDITOR":
      return "In the VAB/SPH";
    case "SPACECENTER":
      return "At the Space Center";
    case "TRACKSTATION":
      return "Tracking Station";
    case "MAINMENU":
      return "Main menu";
    default:
      return "Connected";
  }
}

function Centered({ children }: { children: React.ReactNode }) {
  return (
    <div className="flex min-h-screen flex-col items-center justify-center gap-3">{children}</div>
  );
}

function Fatal({ message }: { message: string }) {
  return (
    <Centered>
      <AlertTriangle className="size-6 text-destructive" />
      <p className="max-w-sm px-6 text-center text-sm text-muted-foreground">{message}</p>
    </Centered>
  );
}

function Notice({ children }: { children: React.ReactNode }) {
  return (
    <div className="flex gap-3 rounded-lg border border-border bg-muted/40 px-4 py-3 text-sm text-muted-foreground">
      <AlertTriangle className="mt-0.5 size-4 shrink-0 text-primary" />
      <p>{children}</p>
    </div>
  );
}
