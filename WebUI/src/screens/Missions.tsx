import { useCallback, useEffect, useState } from "react";
import { Loader2, Lock } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Card, CardContent } from "@/components/ui/card";
import {
  BridgeError,
  type Mission,
  type MissionSelectResult,
  type WeeklyMissions,
  api,
} from "@/lib/bridge";
import { cn, situationLabel } from "@/lib/utils";

/** Same thresholds the in-game window uses, so the two never disagree. */
function difficultyBand(d: number): { label: string; className: string } {
  if (d <= 3) return { label: "Easy", className: "border-emerald-500/40 text-emerald-400" };
  if (d <= 6) return { label: "Medium", className: "border-amber-500/40 text-amber-400" };
  if (d <= 8) return { label: "Hard", className: "border-orange-500/40 text-orange-400" };
  return { label: "Extreme", className: "border-red-500/40 text-red-400" };
}

export function Missions({
  refreshKey,
  onChanged,
}: {
  refreshKey: number;
  onChanged: () => void;
}) {
  const [data, setData] = useState<WeeklyMissions | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [accepting, setAccepting] = useState<number | null>(null);
  const [result, setResult] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      setData(await api.get<WeeklyMissions>("/api/v1/missions/weekly"));
      setError(null);
    } catch (e) {
      setError(e instanceof BridgeError ? e.message : "Could not load missions.");
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load, refreshKey]);

  async function accept(m: Mission) {
    setAccepting(m.id);
    setResult(null);
    try {
      const res = await api.post<MissionSelectResult>("/api/v1/missions/select", {
        mission_id: m.id,
      });
      // The server explains refusals better than a generic string can (already
      // selected this week, mission gone, locked) — surface its message verbatim.
      setResult(res.message);
      if (res.success) {
        await load();
        onChanged();
      }
    } catch (e) {
      setResult(e instanceof BridgeError ? e.message : "Could not accept that mission.");
    } finally {
      setAccepting(null);
    }
  }

  if (loading) {
    return (
      <div className="flex items-center gap-2 py-10 text-sm text-muted-foreground">
        <Loader2 className="size-4 animate-spin" /> Loading missions…
      </div>
    );
  }

  if (error) return <p className="py-8 text-sm text-muted-foreground">{error}</p>;

  if (!data || data.missions.length === 0) {
    return (
      <Card>
        <CardContent className="py-10 text-center text-sm text-muted-foreground">
          No missions available right now.
        </CardContent>
      </Card>
    );
  }

  return (
    <div className="space-y-3">
      <div className="flex flex-wrap items-center gap-3">
        <span className="text-sm text-muted-foreground">Week {data.week_key}</span>
        {data.is_locked ? (
          <Badge variant="outline" className="gap-1.5 border-amber-500/40 text-amber-400">
            <Lock className="size-3" /> Selection locked
          </Badge>
        ) : (
          <span className="text-xs text-muted-foreground">Closes {formatCloses(data.closes_at)}</span>
        )}
      </div>

      {result && (
        <div className="rounded-lg border border-border bg-muted/40 px-4 py-3 text-sm">{result}</div>
      )}

      {/* Two, then three columns as width allows. A single column of ~15 mission cards
          on a 16:9 screen is almost entirely scrolling. */}
      <div className="grid gap-3 lg:grid-cols-2 2xl:grid-cols-3">
      {data.missions.map((m) => {
        const band = difficultyBand(m.difficulty);
        return (
          <Card key={m.id} className="flex animate-fade-up flex-col">
            <CardContent className="flex flex-wrap items-start gap-4 py-4">
              <div className="min-w-0 flex-1">
                <div className="mb-1.5 flex flex-wrap items-center gap-2">
                  <Badge variant="outline" className={cn("text-xs", band.className)}>
                    {band.label} · {m.difficulty}/10
                  </Badge>
                  <span className="font-mono text-xs text-muted-foreground">#{m.id}</span>
                  {m.required_body && (
                    <Badge variant="secondary" className="text-xs">{m.required_body}</Badge>
                  )}
                  {m.required_situation && (
                    <Badge variant="secondary" className="text-xs">
                      {situationLabel(m.required_situation)}
                    </Badge>
                  )}
                </div>

                <p className="text-sm leading-relaxed">{m.desc_en}</p>

                <div className="mt-2 flex flex-wrap gap-x-4 gap-y-1 text-xs text-muted-foreground">
                  <span>+{m.xp.toLocaleString()} XP</span>
                  <span>+{m.coins.toLocaleString()} KCoins</span>
                  <span>Fine {m.fine.toLocaleString()}</span>
                </div>
              </div>

              {!data.is_locked && (
                <Button size="sm" onClick={() => accept(m)} disabled={accepting !== null}>
                  {accepting === m.id ? <Loader2 className="size-4 animate-spin" /> : null}
                  Accept
                </Button>
              )}
            </CardContent>
          </Card>
        );
      })}
      </div>
    </div>
  );
}

function formatCloses(iso: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return iso;

  const hours = Math.round((d.getTime() - Date.now()) / 3_600_000);
  if (hours < 1) return "shortly";
  if (hours < 48) return `in ${hours}h`;
  return d.toLocaleDateString();
}
