import { useCallback, useEffect, useState } from "react";
import { ExternalLink, Loader2, LogOut } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Separator } from "@/components/ui/separator";
import { BridgeError, type Profile, api } from "@/lib/bridge";

const PRIVACY_URL = "https://boundlessmissions.com/pp";
const TERMS_URL = "https://boundlessmissions.com/tos";

export function ProfileCard({
  refreshKey,
  onChanged,
}: {
  refreshKey: number;
  onChanged: () => void;
}) {
  const [profile, setProfile] = useState<Profile | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [unlinking, setUnlinking] = useState(false);

  const load = useCallback(async () => {
    try {
      setProfile(await api.get<Profile>("/api/v1/user/profile"));
      setError(null);
    } catch (e) {
      setError(e instanceof BridgeError ? e.message : "Could not load your profile.");
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load, refreshKey]);

  async function unlink() {
    setUnlinking(true);
    try {
      await api.post("/gk/actions/unlink");
      // Linking is an in-game act — a code typed in KSP plus a Discord approval —
      // so the mod raises its own link window and this page has nothing left to show.
      setProfile(null);
      setError("Unlinked. Finish linking again from the window that just opened in KSP.");
      onChanged();
    } catch (e) {
      setError(e instanceof BridgeError ? e.message : "Could not unlink.");
    } finally {
      setUnlinking(false);
    }
  }

  // Opening a link goes through the mod so the OS browser is driven by KSP, and so the
  // target is checked against an allow-list rather than trusted from the page.
  function openExternal(url: string) {
    void api.post("/gk/actions/open-url", { url }).catch(() => {
      /* the in-game panel is the fallback; a failed link is not worth an error state */
    });
  }

  return (
    <Card className="animate-fade-up">
      <CardHeader className="pb-3">
        <CardTitle className="text-base">Profile</CardTitle>
      </CardHeader>
      <CardContent>
        {loading ? (
          <div className="flex items-center gap-2 py-4 text-sm text-muted-foreground">
            <Loader2 className="size-4 animate-spin" /> Loading…
          </div>
        ) : error ? (
          <p className="py-2 text-sm text-muted-foreground">{error}</p>
        ) : profile ? (
          <>
            <div className="mb-1 text-lg font-semibold">{profile.username}</div>

            <div className="grid grid-cols-2 gap-4 py-4 sm:grid-cols-4">
              <Stat label={profile.currency_name} value={profile.balance.toLocaleString()} />
              <Stat label="Level" value={String(profile.level)} />
              <Stat label="XP" value={profile.xp.toLocaleString()} />
              <Stat label="Messages" value={profile.messages.toLocaleString()} />
            </div>

            {profile.debt > 0 && (
              // Drawn wherever the balance is: a share of every payout goes to these,
              // and a reward that arrives smaller with nothing explaining why reads as
              // the mod being broken.
              <div className="rounded-lg border border-amber-500/40 px-3 py-2 text-sm">
                <p className="font-medium">
                  Unpaid fines: {profile.debt.toLocaleString()} {profile.currency_name}
                </p>
                <p className="text-xs text-muted-foreground">
                  {profile.debt_garnish_percent > 0
                    ? `${profile.debt_garnish_percent}% of what you earn goes towards them until they are paid off. Nothing else is restricted.`
                    : "Repaid out of a share of what you earn."}
                </p>
              </div>
            )}

            <Separator className="my-2" />

            <div className="flex flex-wrap items-center gap-2 pt-3">
              <Button variant="outline" size="sm" onClick={unlink} disabled={unlinking}>
                {unlinking ? (
                  <Loader2 className="size-4 animate-spin" />
                ) : (
                  <LogOut className="size-4" />
                )}
                Unlink this install
              </Button>
              <Button variant="ghost" size="sm" onClick={() => openExternal(PRIVACY_URL)}>
                <ExternalLink className="size-4" /> Privacy
              </Button>
              <Button variant="ghost" size="sm" onClick={() => openExternal(TERMS_URL)}>
                <ExternalLink className="size-4" /> Terms
              </Button>
            </div>
          </>
        ) : null}
      </CardContent>
    </Card>
  );
}

function Stat({ label, value }: { label: string; value: string }) {
  return (
    <div>
      <div className="font-mono text-xl tabular-nums text-primary">{value}</div>
      <div className="text-xs uppercase tracking-wide text-muted-foreground">{label}</div>
    </div>
  );
}
