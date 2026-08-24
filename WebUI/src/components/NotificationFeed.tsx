import { useCallback, useEffect, useState } from "react";
import { Check, Loader2, Trash2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Badge } from "@/components/ui/badge";
import {
  BridgeError,
  type Notification,
  type NotificationsResponse,
  api,
} from "@/lib/bridge";

export function NotificationFeed({
  refreshKey,
  onChanged,
  onUnread,
}: {
  refreshKey: number;
  onChanged: () => void;
  onUnread: (n: number) => void;
}) {
  const [items, setItems] = useState<Notification[]>([]);
  const [unread, setUnread] = useState(0);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  // Second click armed on "Clear read". A single dismiss needs no confirm — it hides
  // one row the server can send again — but this one deletes every read row at once.
  const [confirmClear, setConfirmClear] = useState(false);

  const load = useCallback(async () => {
    try {
      const res = await api.get<NotificationsResponse>("/api/v1/user/notifications");
      setItems(res.notifications ?? []);

      // The server's count is the only authoritative one. Publish it upward for the
      // tab badge, and push it into the mod so the in-game toolbar badge and /gk/state
      // stay correct too — the mod cannot see reads that happened through the proxy.
      const n = res.unread_count ?? 0;
      setUnread(n);
      onUnread(n);
      void api.post("/gk/notifications/unread", { count: n }).catch(() => {
        /* a badge that lags is not worth surfacing an error for */
      });

      setError(null);
    } catch (e) {
      setError(e instanceof BridgeError ? e.message : "Could not load notifications.");
    } finally {
      setLoading(false);
    }
  }, [onUnread]);

  // refreshKey bumps on every SSE push, so this list is live without polling.
  useEffect(() => {
    void load();
  }, [load, refreshKey]);

  // Counted off the loaded rows rather than `unread`, which is the server's count of
  // the whole collection — this number labels the button that deletes what is here.
  const readCount = items.filter((n) => n.read).length;

  // An armed confirm outlives the rows it was armed for otherwise: a push that clears
  // the read half would leave "Delete n" showing the moment the next one is read.
  useEffect(() => {
    if (readCount === 0) setConfirmClear(false);
  }, [readCount]);

  async function markAllRead() {
    setBusy("all");
    try {
      await api.post("/api/v1/user/notifications/mark_read");
      await load();
      onChanged();
    } catch {
      /* the reload below will show whatever actually happened */
    } finally {
      setBusy(null);
    }
  }

  // Deletes the read half of the feed in one call rather than one DELETE per row:
  // the list shows 50 but the collection behind it is unbounded, and every request
  // here goes through KSP's main thread.
  async function clearRead() {
    setBusy("read");
    setItems((prev) => prev.filter((n) => !n.read));
    try {
      await api.del("/api/v1/user/notifications/read");
      onChanged();
    } catch {
      await load(); // put them back if the server disagreed
    } finally {
      setBusy(null);
      setConfirmClear(false);
    }
  }

  async function dismiss(id: string) {
    setBusy(id);
    // Optimistic: the round trip goes through KSP's main thread, so it can take a
    // frame or two, and a list item that lingers after a click feels broken.
    setItems((prev) => prev.filter((n) => n.id !== id));
    try {
      await api.del(`/api/v1/user/notifications/${encodeURIComponent(id)}`);
      onChanged();
    } catch {
      await load(); // put it back if the server disagreed
    } finally {
      setBusy(null);
    }
  }

  return (
    <Card className="animate-fade-up">
      <CardHeader className="flex-row items-center justify-between space-y-0 pb-3">
        <CardTitle className="flex items-center gap-2 text-base">
          Notifications
          {unread > 0 && <Badge className="h-5 px-2 text-xs">{unread}</Badge>}
        </CardTitle>
        <div className="flex items-center gap-1">
          {unread > 0 && (
            <Button variant="ghost" size="sm" onClick={markAllRead} disabled={busy === "all"}>
              {busy === "all" ? (
                <Loader2 className="size-4 animate-spin" />
              ) : (
                <Check className="size-4" />
              )}
              Mark all read
            </Button>
          )}
          {readCount > 0 &&
            (confirmClear ? (
              <>
                <Button
                  variant="default"
                  size="sm"
                  onClick={clearRead}
                  disabled={busy === "read"}
                  // Same armed styling the contract screen's two-click confirms use;
                  // the Button has no destructive variant of its own.
                  className="bg-destructive text-destructive-foreground hover:bg-destructive/90"
                >
                  {busy === "read" ? (
                    <Loader2 className="size-4 animate-spin" />
                  ) : (
                    <Trash2 className="size-4" />
                  )}
                  Delete {readCount}
                </Button>
                <Button variant="ghost" size="sm" onClick={() => setConfirmClear(false)}>
                  Cancel
                </Button>
              </>
            ) : (
              <Button variant="ghost" size="sm" onClick={() => setConfirmClear(true)}>
                <Trash2 className="size-4" />
                Clear read
              </Button>
            ))}
        </div>
      </CardHeader>

      <CardContent>
        {loading ? (
          <div className="flex items-center gap-2 py-4 text-sm text-muted-foreground">
            <Loader2 className="size-4 animate-spin" /> Loading…
          </div>
        ) : error ? (
          <p className="py-2 text-sm text-muted-foreground">{error}</p>
        ) : items.length === 0 ? (
          <p className="py-6 text-center text-sm text-muted-foreground">
            Nothing here yet. New activity appears the moment it happens.
          </p>
        ) : (
          <ul className="divide-y divide-border">
            {items.map((n) => (
              <li key={n.id} className="flex items-start gap-3 py-3">
                <span
                  aria-hidden
                  className={
                    "mt-1.5 size-2 shrink-0 rounded-full " +
                    (n.read ? "bg-border" : "bg-primary")
                  }
                />
                <div className="min-w-0 flex-1">
                  <div className="text-sm font-medium">{n.title}</div>
                  <div className="text-sm text-muted-foreground">{n.message}</div>
                  <div className="mt-1 text-xs text-muted-foreground/70">
                    {formatTime(n.timestamp)}
                  </div>
                </div>
                <Button
                  variant="ghost"
                  size="icon"
                  onClick={() => dismiss(n.id)}
                  disabled={busy === n.id}
                  aria-label="Dismiss"
                >
                  <Trash2 className="size-4" />
                </Button>
              </li>
            ))}
          </ul>
        )}
      </CardContent>
    </Card>
  );
}

function formatTime(ts: string): string {
  const d = new Date(ts);
  if (Number.isNaN(d.getTime())) return ts;

  const mins = Math.floor((Date.now() - d.getTime()) / 60000);
  if (mins < 1) return "just now";
  if (mins < 60) return `${mins}m ago`;
  if (mins < 1440) return `${Math.floor(mins / 60)}h ago`;
  return d.toLocaleDateString();
}
