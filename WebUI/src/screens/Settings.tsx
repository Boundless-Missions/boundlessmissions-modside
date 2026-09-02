import { useCallback, useEffect, useState } from "react";
import {
  AlertTriangle,
  Bell,
  Camera,
  Check,
  EyeOff,
  Loader2,
  MessageSquare,
  Radio,
  Server,
  ShieldOff,
} from "lucide-react";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { BridgeError, type Profile, type Settings as ModSettings, api } from "@/lib/bridge";
import { cn } from "@/lib/utils";

/**
 * The mod's settings, as opposed to the account's.
 *
 * The server selector is the reason this screen exists: without it, pointing KSP at a
 * locally running bot means switching back to the classic in-game UI (or hand-editing
 * settings.cfg and restarting), which makes testing a bot change a deploy-first loop.
 *
 * Switching servers is a bigger deal than it looks — the session token is per-server, so
 * a switch changes whether the mod is linked at all. The mod handles the consequences
 * (see GeneKermanMod.OnServerChanged); this screen's job is to say what happened.
 */
export function Settings({
  refreshKey,
  onChanged,
}: {
  refreshKey: number;
  onChanged: () => void;
}) {
  const [settings, setSettings] = useState<ModSettings | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      setSettings(await api.get<ModSettings>("/gk/settings"));
    } catch (e) {
      setError(e instanceof BridgeError ? e.message : "Could not read the mod's settings.");
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load, refreshKey]);

  if (error) {
    return (
      <Card>
        <CardContent className="flex items-center gap-2 py-6 text-sm text-muted-foreground">
          <AlertTriangle className="size-4 text-destructive" />
          {error}
        </CardContent>
      </Card>
    );
  }

  if (!settings) {
    return (
      <Card>
        <CardContent className="flex items-center gap-2 py-6 text-sm text-muted-foreground">
          <Loader2 className="size-4 animate-spin" />
          Reading settings…
        </CardContent>
      </Card>
    );
  }

  return (
    <div className="grid gap-4 lg:grid-cols-2 lg:items-start">
      <ServerCard settings={settings} onApplied={setSettings} onChanged={onChanged} />
      <div className="space-y-4">
        <PrivacyCard settings={settings} onApplied={setSettings} />
        <TogglesCard settings={settings} onApplied={setSettings} />
        <DiscordCard linked={settings.linked === true} refreshKey={refreshKey} />
        <AboutCard settings={settings} />
      </div>
    </div>
  );
}

function ServerCard({
  settings,
  onApplied,
  onChanged,
}: {
  settings: ModSettings;
  onApplied: (s: ModSettings) => void;
  onChanged: () => void;
}) {
  // The address box is only seeded once. Re-seeding it from `settings` on every poll
  // would yank characters out from under someone mid-type.
  const [url, setUrl] = useState(settings.customUrl);
  const [busy, setBusy] = useState(false);
  const [note, setNote] = useState<{ ok: boolean; text: string } | null>(null);

  async function apply(official: boolean) {
    setBusy(true);
    setNote(null);
    try {
      const next = await api.post<ModSettings>("/gk/settings", {
        official,
        ...(official ? {} : { customUrl: url }),
      });
      onApplied(next);
      setUrl(next.customUrl);

      if (!next.serverChanged) {
        setNote({ ok: true, text: "Already connected to that server." });
      } else if (next.linked) {
        // A token from a previous visit to this server was still on disk.
        setNote({ ok: true, text: `Connected to ${next.serverUrl} as ${next.username || "your account"}.` });
        onChanged();
      } else {
        setNote({
          ok: true,
          text: `Now pointing at ${next.serverUrl}. This server has not seen you yet, so the link window is waiting in KSP.`,
        });
        onChanged();
      }
    } catch (e) {
      setNote({
        ok: false,
        text: e instanceof BridgeError ? e.message : "Could not change the server.",
      });
    } finally {
      setBusy(false);
    }
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          <Server className="size-4" /> Server
        </CardTitle>
      </CardHeader>
      <CardContent className="space-y-4">
        <p className="text-sm text-muted-foreground">
          The official server, or your own if you are running one. Each server issues its own
          login, so the mod remembers them separately; switching back does not mean linking
          again.
        </p>

        <div className="flex gap-2">
          <Choice
            selected={settings.official}
            disabled={busy}
            onClick={() => apply(true)}
            title="Official server"
            subtitle={hostOf(settings.officialUrl)}
          />
          <Choice
            selected={!settings.official}
            disabled={busy}
            onClick={() => apply(false)}
            title="Custom server"
            subtitle={hostOf(settings.customUrl)}
          />
        </div>

        {!settings.official && (
          <div className="space-y-2">
            <label htmlFor="server-url" className="block text-xs font-medium text-muted-foreground">
              Address
            </label>
            <div className="flex gap-2">
              <input
                id="server-url"
                value={url}
                onChange={(e) => setUrl(e.target.value)}
                onKeyDown={(e) => {
                  if (e.key === "Enter" && !busy) void apply(false);
                }}
                spellCheck={false}
                autoComplete="off"
                placeholder="localhost:5022"
                className="h-9 min-w-0 flex-1 rounded-md border border-input bg-background px-3 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
              />
              <Button size="sm" onClick={() => apply(false)} disabled={busy || !url.trim()}>
                {busy ? <Loader2 className="size-4 animate-spin" /> : <Check className="size-4" />}
                Connect
              </Button>
            </div>
            <p className="text-xs text-muted-foreground">
              Host and port only, such as <code>localhost:5022</code>. http:// is assumed if you leave
              the scheme off.
            </p>
          </div>
        )}

        <div className="rounded-md border border-border bg-muted/40 px-3 py-2 text-xs">
          <span className="text-muted-foreground">Connected to </span>
          <span className="font-medium">{settings.serverUrl}</span>
          <span className="text-muted-foreground">
            {settings.linked ? ` · linked as ${settings.username || "your account"}` : " · not linked"}
          </span>
        </div>

        {note && (
          <p className={cn("text-sm", note.ok ? "text-muted-foreground" : "text-destructive")}>
            {note.text}
          </p>
        )}
      </CardContent>
    </Card>
  );
}

/**
 * Hiding other players' faces and corp names, by hand or by noticing OBS.
 *
 * The mirror of the in-game sidebar's Privacy card, and deliberately a separate card
 * from "In-game behaviour": these two are about what someone watching your screen can
 * see, not about how the mod plays.
 *
 * The detection line is not decoration. "Streamer mode" reads like a promise that the
 * mod knows when you are live, and it does not — no OS reports that a window is being
 * captured, so all any of this can say is which programs are running. Printing what it
 * found is how that limit stays visible instead of being a surprise later.
 */
function PrivacyCard({
  settings,
  onApplied,
}: {
  settings: ModSettings;
  onApplied: (s: ModSettings) => void;
}) {
  const [busy, setBusy] = useState<string | null>(null);

  async function set(key: string, value: boolean) {
    setBusy(key);
    try {
      onApplied(await api.post<ModSettings>("/gk/settings", { [key]: value }));
    } catch {
      /* the row snaps back to the mod's actual state, which is the honest answer */
    } finally {
      setBusy(null);
    }
  }

  const app = settings.broadcastApp ?? "";

  return (
    <Card>
      <CardHeader>
        <CardTitle>Privacy</CardTitle>
      </CardHeader>
      <CardContent className="space-y-1">
        <Toggle
          icon={EyeOff}
          label="Hide profile pictures and corp names"
          hint="Player lists show display names only. Pictures are not just hidden but never downloaded, so nothing on this PC asks Discord for them."
          checked={settings.hidePlayerDetails === true}
          busy={busy === "hidePlayerDetails"}
          onChange={(v) => set("hidePlayerDetails", v)}
        />
        <Toggle
          icon={Radio}
          label="Streamer mode"
          hint="Turns the switch above on by itself while OBS, Streamlabs, XSplit or similar is running. Checks the names of programs running on this PC every few seconds and nothing else: no window titles, nothing sent anywhere. While this is off, nothing is checked at all."
          checked={settings.streamerMode === true}
          busy={busy === "streamerMode"}
          onChange={(v) => set("streamerMode", v)}
        />
        {settings.streamerMode === true && (
          <p className="rounded-md bg-muted/50 px-3 py-2 text-xs text-muted-foreground">
            {app
              ? `${app} is running. ${
                  settings.hidePlayerDetails
                    ? "Details are hidden by the switch above anyway."
                    : "Profile pictures and corp names are hidden while it is."
                }`
              : "No broadcasting software running."}
          </p>
        )}
      </CardContent>
    </Card>
  );
}

function TogglesCard({
  settings,
  onApplied,
}: {
  settings: ModSettings;
  onApplied: (s: ModSettings) => void;
}) {
  const [busy, setBusy] = useState<string | null>(null);

  async function set(key: string, value: boolean) {
    setBusy(key);
    try {
      onApplied(await api.post<ModSettings>("/gk/settings", { [key]: value }));
    } catch {
      /* the row snaps back to the mod's actual state, which is the honest answer */
    } finally {
      setBusy(null);
    }
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>In-game behaviour</CardTitle>
      </CardHeader>
      <CardContent className="space-y-1">
        <Toggle
          icon={Bell}
          label="Notification popups"
          hint="Toasts over the game when something happens."
          checked={settings.notifications}
          busy={busy === "notifications"}
          onChange={(v) => set("notifications", v)}
        />
        {/* Drawn only while the mod reports the feature as available. The server's
            master switch currently refuses hero-shot uploads, so the whole flow is
            held off in the mod and this switch would control nothing. */}
        {settings.checkpointPhotosAvailable === true && (
          <Toggle
            icon={Camera}
            label="Milestone photo prompts"
            hint="Offers a hero shot on a rendezvous, flyby or asteroid encounter."
            checked={settings.checkpointPhotos}
            busy={busy === "checkpointPhotos"}
            onChange={(v) => set("checkpointPhotos", v)}
          />
        )}

        {/* Data sharing can be switched off from here but not back on. Turning it on is
            a consent decision, and it belongs in the game next to the panel that
            explains what gets sent — not behind a button in a background tab. */}
        <div className="flex items-start gap-3 border-t border-border pt-3">
          <ShieldOff className="mt-0.5 size-4 shrink-0 text-muted-foreground" />
          <div className="min-w-0 flex-1">
            <p className="text-sm font-medium">Data sharing</p>
            <p className="text-xs text-muted-foreground">
              {settings.dataGathering
                ? "On. Turning it off makes the mod inert immediately: nothing is collected or sent."
                : "Off. The mod is inert. Turn it back on from the in-game panel."}
            </p>
          </div>
          {settings.dataGathering && (
            <Button
              size="sm"
              variant="outline"
              disabled={busy === "dataGathering"}
              onClick={() => set("dataGathering", false)}
            >
              {busy === "dataGathering" ? <Loader2 className="size-4 animate-spin" /> : null}
              Turn off
            </Button>
          )}
        </div>
      </CardContent>
    </Card>
  );
}

/**
 * The one card here that is not a mod setting.
 *
 * Everything above is written to settings.cfg and takes effect on this PC. This is
 * stored on the account, because the thing it controls — the @-mention the bot puts
 * on a corp-channel post — is written by the server, and no file on this machine has
 * a say in it. So it reads the profile rather than `/gk/settings`, and writes through
 * the API proxy to /api/v1/user/preferences.
 *
 * The switch is drawn only once the profile has arrived. A switch shown at its default
 * before the real value lands would flip under anyone who had turned it off.
 */
function DiscordCard({ linked, refreshKey }: { linked: boolean; refreshKey: number }) {
  const [pings, setPings] = useState<boolean | null>(null);
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState(false);

  const load = useCallback(async () => {
    if (!linked) return;
    setFailed(false);
    try {
      setPings((await api.get<Profile>("/api/v1/user/profile")).corp_pings !== false);
    } catch {
      setFailed(true);
    }
  }, [linked]);

  useEffect(() => {
    void load();
  }, [load, refreshKey]);

  // Nothing to draw for an account that does not exist yet: linking happens in the
  // game, and this switch has no meaning until it has.
  if (!linked) return null;

  async function set(value: boolean) {
    setBusy(true);
    // Optimistic, then corrected by the server's answer — which is the value now
    // stored, so a refusal puts the switch back rather than leaving it showing a
    // state that was never saved.
    setPings(value);
    try {
      const r = await api.post<{ corp_pings: boolean }>("/api/v1/user/preferences", {
        corp_pings: value,
      });
      setPings(r.corp_pings !== false);
    } catch {
      setPings(!value);
    } finally {
      setBusy(false);
    }
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>Discord</CardTitle>
      </CardHeader>
      <CardContent className="space-y-1">
        {pings === null ? (
          <div className="flex items-center gap-2 py-2 text-sm text-muted-foreground">
            {failed ? (
              <>
                <AlertTriangle className="size-4 text-destructive" />
                Could not read your account settings.
                <Button size="sm" variant="outline" onClick={() => void load()}>
                  Retry
                </Button>
              </>
            ) : (
              <>
                <Loader2 className="size-4 animate-spin" />
                Loading your account settings…
              </>
            )}
          </div>
        ) : (
          <Toggle
            icon={MessageSquare}
            label="Mention me in my corporation channel"
            hint="Contract offers, disputes and hand-offs are posted to your corp channel with a ping so you see them. Off, the same posts still arrive and still say who they are for. Discord just will not notify you, so you would be reading the channel yourself. The in-game notifications are unaffected."
            checked={pings}
            busy={busy}
            onChange={(v) => void set(v)}
          />
        )}
      </CardContent>
    </Card>
  );
}

function AboutCard({ settings }: { settings: ModSettings }) {
  return (
    <Card>
      <CardContent className="flex items-center justify-between py-4 text-sm">
        <span className="text-muted-foreground">Mod version</span>
        <span className="font-medium">
          {settings.modVersion}
          {settings.updateRequired && (
            <span className="ml-2 text-destructive">update required</span>
          )}
        </span>
      </CardContent>
    </Card>
  );
}

function Choice({
  selected,
  disabled,
  onClick,
  title,
  subtitle,
}: {
  selected: boolean;
  disabled: boolean;
  onClick: () => void;
  title: string;
  subtitle: string;
}) {
  return (
    <button
      onClick={onClick}
      disabled={disabled}
      aria-pressed={selected}
      className={cn(
        "min-w-0 flex-1 rounded-md border px-3 py-2 text-left transition-colors",
        "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
        "disabled:opacity-60",
        selected
          ? "border-primary bg-secondary"
          : "border-border hover:bg-muted/50"
      )}
    >
      <span className="block text-sm font-medium">{title}</span>
      <span className="block truncate text-xs text-muted-foreground">{subtitle}</span>
    </button>
  );
}

function Toggle({
  icon: Icon,
  label,
  hint,
  checked,
  busy,
  onChange,
}: {
  icon: typeof Bell;
  label: string;
  hint: string;
  checked: boolean;
  busy: boolean;
  onChange: (v: boolean) => void;
}) {
  return (
    <button
      role="switch"
      aria-checked={checked}
      disabled={busy}
      onClick={() => onChange(!checked)}
      className="flex w-full items-start gap-3 rounded-md py-2 text-left transition-colors hover:bg-muted/50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring disabled:opacity-60"
    >
      <Icon className="mt-0.5 size-4 shrink-0 text-muted-foreground" />
      <span className="min-w-0 flex-1">
        <span className="block text-sm font-medium">{label}</span>
        <span className="block text-xs text-muted-foreground">{hint}</span>
      </span>
      <span
        className={cn(
          "mt-0.5 flex h-5 w-9 shrink-0 items-center rounded-full p-0.5 transition-colors",
          checked ? "bg-primary" : "bg-muted"
        )}
      >
        <span
          className={cn(
            "size-4 rounded-full bg-background transition-transform",
            checked && "translate-x-4"
          )}
        />
      </span>
    </button>
  );
}

/** The full URL is long and the scheme is noise in a button label. */
function hostOf(url: string): string {
  try {
    return new URL(url).host;
  } catch {
    return url;
  }
}
