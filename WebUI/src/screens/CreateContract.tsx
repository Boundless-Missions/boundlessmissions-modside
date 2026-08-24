import { useEffect, useMemo, useState } from "react";
import { AlertTriangle, Gavel, Loader2, Rocket, Send, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Separator } from "@/components/ui/separator";
import { PlayerPicker } from "@/components/PlayerPicker";
import { BridgeError, type ContractContext, type Corp, type Profile, api, runJob } from "@/lib/bridge";
import { cn } from "@/lib/utils";

/**
 * Issue a contract, a reverse auction, or a rescue.
 *
 * Everything the game has to answer comes from /gk/contract/context, and the send goes
 * through /gk/actions/create-contract rather than straight to the API — the part
 * restriction and, for a rescue, the vessel itself are read from KSP at send time, so
 * this form names a *mode* and never the resulting mod list.
 */

/** Lowest opening price for a reverse auction — see settings.AUCTION_MIN_START_VALUE. */
const MIN_AUCTION_START = 2;

const TYPES = [
  {
    id: "craft_build",
    label: "Craft build",
    desc: "They submit a blueprint from the VAB/SPH.",
    auctionable: true,
  },
  {
    id: "active_vessel",
    label: "Active mission",
    desc: "They fly a craft to the target.",
    auctionable: true,
  },
  {
    id: "rescue",
    label: "Rescue",
    // The one type that cannot be auctioned: sending it destroys the issuer's vessel,
    // so it cannot be handed to a contractor who is not decided yet.
    desc: "They rescue the kerbals on your current vessel.",
    auctionable: false,
  },
  {
    id: "flag_design",
    label: "Flag design",
    desc: "They design a flag, reviewed via Discord.",
    auctionable: true,
  },
] as const;

type TypeId = (typeof TYPES)[number]["id"];

const MODLIST_MODES = [
  { id: "none", label: "No restriction", desc: "Any parts." },
  { id: "stock", label: "Stock only", desc: "Squad parts, no DLC." },
  { id: "stock_dlc", label: "Stock + DLC", desc: "Squad plus the official expansions." },
  { id: "mine", label: "My mod list", desc: "Every mod currently installed on your game." },
  { id: "janitor", label: "Janitor's Closet", desc: "Only mods visible in your JC filter." },
] as const;

/** Seven days out, as yyyy-MM-dd — what <input type="date"> and the server both want. */
function defaultDueDate(): string {
  const d = new Date();
  d.setDate(d.getDate() + 7);
  return d.toISOString().slice(0, 10);
}

export function CreateContract({
  onCancel,
  onCreated,
}: {
  onCancel: () => void;
  onCreated: () => void;
}) {
  const [ctx, setCtx] = useState<ContractContext | null>(null);
  const [profile, setProfile] = useState<Profile | null>(null);

  const [type, setType] = useState<TypeId>("craft_build");
  const [auction, setAuction] = useState(false);
  const [recipient, setRecipient] = useState<Corp | null>(null);
  const [mission, setMission] = useState("");
  const [payment, setPayment] = useState("");
  const [fine, setFine] = useState("0");
  const [dueDate, setDueDate] = useState(defaultDueDate);
  const [duration, setDuration] = useState("24");
  const [modlistMode, setModlistMode] = useState<string>("none");

  const [rescueMode, setRescueMode] = useState<"orbit" | "surface">("orbit");
  const [rescueRecovery, setRescueRecovery] = useState<"crew" | "vessel">("crew");
  const [minDv, setMinDv] = useState("0");
  const [body, setBody] = useState("");
  const [ap, setAp] = useState("100");
  const [pe, setPe] = useState("100");
  const [marginAlt, setMarginAlt] = useState("10");
  const [lat, setLat] = useState("0");
  const [lon, setLon] = useState("0");
  const [marginPos, setMarginPos] = useState("1");
  const [confirmRescue, setConfirmRescue] = useState(false);

  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    // Everything in the context is live game state: the player opens the VAB (which is
    // what makes the Janitor's Closet filter readable), or switches to the crewed
    // vessel, while this form is already on screen. None of that produces an event, so
    // poll — it is a local call that never touches the network.
    let seeded = false;

    const load = () =>
      api
        .get<ContractContext>("/gk/contract/context")
        .then((c) => {
          setCtx(c);

          // Seed the rescue target from where the vessel actually is — typing an orbit
          // you are already flying is busywork, and getting it wrong makes the contract
          // impossible for the rescuer. Once only: re-seeding on every poll would
          // overwrite whatever the player has typed, and the vessel's Ap/Pe drift every
          // second it is in flight.
          if (c.rescue.available && !seeded) {
            seeded = true;
            setBody(c.rescue.body);
            setAp(round(c.rescue.apKm));
            setPe(round(c.rescue.peKm));
            setLat(round(c.rescue.lat));
            setLon(round(c.rescue.lon));
            setMarginAlt(String(c.minMarginOrbitKm * 2));
            setMarginPos(String(c.minMarginSurfaceDeg * 2));
          }
        })
        .catch(() => {
          /* a dropped poll is not an outcome; keep the last good context */
        });

    void load();
    const t = setInterval(load, 4000);

    api
      .get<Profile>("/api/v1/user/profile")
      .then(setProfile)
      .catch(() => setProfile(null));

    return () => clearInterval(t);
  }, []);

  const isRescue = type === "rescue";
  const typeInfo = TYPES.find((t) => t.id === type)!;
  const isAuction = auction && typeInfo.auctionable;
  const currency = profile?.currency_name || "KCoins";

  // Checked here rather than in the mod, which would have to fetch the balance to know.
  // The server re-checks it at escrow time, so this only saves a doomed round trip.
  const paymentNum = Number.parseInt(payment, 10);
  const overBalance =
    !!profile && Number.isFinite(paymentNum) && paymentNum > profile.balance;

  const problem = useMemo(() => {
    if (!isAuction && !recipient) return "Pick who this is for.";
    if (mission.trim().length < 3) return "Describe the mission (a few words at least).";
    if (!Number.isFinite(paymentNum) || paymentNum <= 0) return "Enter a payment amount.";
    // Mirrors settings.AUCTION_MIN_START_VALUE: a bid must undercut the current price
    // and stay above zero, so an auction opened at 1 has no legal bid at all.
    if (isAuction && paymentNum < MIN_AUCTION_START)
      return `An auction has to start at ${MIN_AUCTION_START} ${currency} or more; nobody can undercut a lower opening price.`;
    if (overBalance)
      return `You only have ${profile!.balance.toLocaleString()} ${currency}.`;
    if (!dueDate) return "Pick a due date.";
    if (isAuction && (!Number.isFinite(Number(duration)) || Number(duration) < 1))
      return "Enter an auction duration in hours.";
    if (isRescue) {
      if (!ctx?.rescue.available)
        return "Switch to the crewed vessel in flight to send a rescue.";
      if (!body) return "Pick a target body.";
      if (!confirmRescue) return "Confirm that your vessel will be handed over.";
    }
    if (modlistMode === "janitor" && ctx && !ctx.editorFilterReadable)
      return "Open the VAB or SPH so the Janitor's Closet filter can be read.";
    return null;
  }, [
    isAuction, recipient, mission, paymentNum, overBalance, profile, currency, dueDate,
    duration, isRescue, ctx, body, confirmRescue, modlistMode,
  ]);

  async function send() {
    setBusy(true);
    setError(null);
    try {
      const job = await runJob("/gk/actions/create-contract", {
        kind: isRescue ? "rescue" : isAuction ? "auction" : "contract",
        contractor_id: recipient?.owner_id ?? "",
        contractor_name: recipient?.owner_name ?? "",
        mission: mission.trim(),
        payment: paymentNum,
        fine: Math.max(0, Number.parseInt(fine, 10) || 0),
        due_date: dueDate,
        contract_type: type,
        // Rescue always carries the issuer's own mod list, and a flag has no build step
        // to restrict — in both cases the mod ignores whatever is sent here.
        modlist_mode: isRescue || type === "flag_design" ? "none" : modlistMode,
        duration_hours: Number.parseInt(duration, 10) || 24,
        rescue: isRescue
          ? {
              mode: rescueMode,
              recovery: rescueRecovery,
              min_dv: Number(minDv) || 0,
              body,
              ap_km: Number(ap) || 0,
              pe_km: Number(pe) || 0,
              margin_alt_km: Number(marginAlt) || 0,
              lat: Number(lat) || 0,
              lon: Number(lon) || 0,
              margin_pos_deg: Number(marginPos) || 0,
            }
          : undefined,
      });

      if (job.state === "done") onCreated();
      else setError(job.message || "The server refused the contract.");
    } catch (e) {
      setError(e instanceof BridgeError ? e.message : "Could not send the contract.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <Card className="min-w-0 flex-1">
      <CardHeader className="flex-row items-center justify-between space-y-0 pb-3">
        <CardTitle className="flex items-center gap-2 text-base">
          <Rocket className="size-4" /> New contract
        </CardTitle>
        <Button variant="ghost" size="icon" onClick={onCancel} aria-label="Close">
          <X className="size-4" />
        </Button>
      </CardHeader>

      <CardContent className="space-y-5">
        <Field label="What kind of work?">
          <div className="grid gap-2 sm:grid-cols-2">
            {TYPES.map((t) => (
              <button
                key={t.id}
                onClick={() => {
                  setType(t.id);
                  if (!t.auctionable) setAuction(false);
                }}
                aria-pressed={type === t.id}
                className={cn(
                  "rounded-md border px-3 py-2 text-left transition-colors",
                  "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
                  type === t.id ? "border-primary bg-secondary" : "border-border hover:bg-muted/50"
                )}
              >
                <span className="block text-sm font-medium">{t.label}</span>
                <span className="block text-xs text-muted-foreground">{t.desc}</span>
              </button>
            ))}
          </div>
        </Field>

        {typeInfo.auctionable && (
          <button
            role="switch"
            aria-checked={auction}
            onClick={() => setAuction((v) => !v)}
            className="flex w-full items-start gap-3 rounded-md py-1 text-left"
          >
            <Gavel className="mt-0.5 size-4 shrink-0 text-muted-foreground" />
            <span className="min-w-0 flex-1">
              <span className="block text-sm font-medium">Open auction</span>
              <span className="block text-xs text-muted-foreground">
                No single recipient: anyone bids the price down in Discord and the lowest wins.
              </span>
            </span>
            <Switch on={auction} />
          </button>
        )}

        {isAuction ? (
          <p className="rounded-md border border-border bg-muted/40 px-3 py-2 text-sm text-muted-foreground">
            Open to everyone. The lowest bidder in Discord gets the contract.
          </p>
        ) : (
          <Field label={isRescue ? "Who should rescue them?" : "Who is this for?"}>
            <PlayerPicker value={recipient?.owner_id ?? ""} onChange={setRecipient} />
          </Field>
        )}

        <Field label="Mission">
          <textarea
            value={mission}
            onChange={(e) => setMission(e.target.value)}
            rows={3}
            placeholder="What do they have to do?"
            className="w-full resize-y rounded-md border border-border bg-transparent px-3 py-2 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
          />
        </Field>

        <div className="grid gap-3 sm:grid-cols-3">
          <Field
            label={
              isAuction
                ? `Start price (${currency}, min ${MIN_AUCTION_START})`
                : `Payment (${currency})`
            }
          >
            <NumberInput value={payment} onChange={setPayment} placeholder="0" />
            {profile && (
              <p className={cn("mt-1 text-xs", overBalance ? "text-destructive" : "text-muted-foreground")}>
                Balance {profile.balance.toLocaleString()}
              </p>
            )}
          </Field>
          <Field label={`Fine (${currency})`}>
            <NumberInput value={fine} onChange={setFine} placeholder="0" />
          </Field>
          <Field label="Due date">
            <input
              type="date"
              value={dueDate}
              onChange={(e) => setDueDate(e.target.value)}
              className="h-9 w-full rounded-md border border-border bg-transparent px-2 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            />
          </Field>
        </div>

        {isAuction && (
          <Field label="Auction runs for (hours)">
            <NumberInput value={duration} onChange={setDuration} placeholder="24" />
          </Field>
        )}

        {isRescue && (
          <>
            <Separator />
            <RescuePanel
              ctx={ctx}
              mode={rescueMode}
              setMode={setRescueMode}
              recovery={rescueRecovery}
              setRecovery={setRescueRecovery}
              minDv={minDv} setMinDv={setMinDv}
              body={body}
              setBody={setBody}
              ap={ap} setAp={setAp}
              pe={pe} setPe={setPe}
              marginAlt={marginAlt} setMarginAlt={setMarginAlt}
              lat={lat} setLat={setLat}
              lon={lon} setLon={setLon}
              marginPos={marginPos} setMarginPos={setMarginPos}
              confirmed={confirmRescue}
              setConfirmed={setConfirmRescue}
            />
          </>
        )}

        {!isRescue && type !== "flag_design" && (
          <Field label="Part restriction">
            <div className="space-y-1">
              {MODLIST_MODES.map((m) => {
                const unavailable = m.id === "janitor" && ctx !== null && !ctx.janitorsCloset;
                return (
                  <button
                    key={m.id}
                    disabled={unavailable}
                    onClick={() => setModlistMode(m.id)}
                    aria-pressed={modlistMode === m.id}
                    className={cn(
                      "flex w-full items-baseline gap-2 rounded-md px-2 py-1.5 text-left transition-colors",
                      "disabled:cursor-not-allowed disabled:opacity-50",
                      modlistMode === m.id ? "bg-secondary" : "hover:bg-muted/50"
                    )}
                  >
                    <span className="text-sm font-medium">{m.label}</span>
                    <span className="min-w-0 flex-1 truncate text-xs text-muted-foreground">
                      {unavailable ? "Janitor's Closet is not installed." : m.desc}
                    </span>
                  </button>
                );
              })}
            </div>
            {modlistMode === "janitor" && ctx && !ctx.editorFilterReadable && (
              <p className="mt-1 text-xs text-muted-foreground">
                The filter is only readable from the VAB/SPH, so open the editor before sending.
              </p>
            )}
          </Field>
        )}

        <Separator />

        {error && (
          <p className="flex items-start gap-2 text-sm text-destructive">
            <AlertTriangle className="mt-0.5 size-4 shrink-0" />
            {error}
          </p>
        )}

        <div className="flex items-center gap-3">
          <Button onClick={send} disabled={busy || !!problem}>
            {busy ? <Loader2 className="size-4 animate-spin" /> : <Send className="size-4" />}
            {busy ? "Sending…" : isAuction ? "Post auction" : "Send contract"}
          </Button>
          <Button variant="ghost" onClick={onCancel} disabled={busy}>
            Cancel
          </Button>
          {problem && !busy && <p className="text-xs text-muted-foreground">{problem}</p>}
        </div>
      </CardContent>
    </Card>
  );
}

function RescuePanel({
  ctx, mode, setMode, recovery, setRecovery, minDv, setMinDv, body, setBody,
  ap, setAp, pe, setPe, marginAlt, setMarginAlt,
  lat, setLat, lon, setLon, marginPos, setMarginPos,
  confirmed, setConfirmed,
}: {
  ctx: ContractContext | null;
  mode: "orbit" | "surface";
  setMode: (m: "orbit" | "surface") => void;
  recovery: "crew" | "vessel";
  setRecovery: (r: "crew" | "vessel") => void;
  minDv: string; setMinDv: (v: string) => void;
  body: string;
  setBody: (b: string) => void;
  ap: string; setAp: (v: string) => void;
  pe: string; setPe: (v: string) => void;
  marginAlt: string; setMarginAlt: (v: string) => void;
  lat: string; setLat: (v: string) => void;
  lon: string; setLon: (v: string) => void;
  marginPos: string; setMarginPos: (v: string) => void;
  confirmed: boolean;
  setConfirmed: (v: boolean) => void;
}) {
  if (!ctx) {
    return (
      <p className="flex items-center gap-2 text-sm text-muted-foreground">
        <Loader2 className="size-4 animate-spin" /> Reading the game…
      </p>
    );
  }

  if (!ctx.rescue.available) {
    return (
      <p className="flex items-start gap-2 text-sm text-muted-foreground">
        <AlertTriangle className="mt-0.5 size-4 shrink-0 text-primary" />
        A rescue hands over the vessel you are flying, so you have to be in flight on a
        crewed ship. {unavailableReason(ctx)}
      </p>
    );
  }

  return (
    <div className="space-y-4">
      <div className="rounded-md border border-border bg-muted/40 px-3 py-2 text-sm">
        <p className="font-medium">{ctx.rescue.vessel}</p>
        <p className="text-xs text-muted-foreground">
          {ctx.rescue.crew.join(", ")} ({ctx.rescue.crew.length} aboard)
        </p>
      </div>

      <Field label="Where should the rescuer find them?">
        <div className="flex gap-2">
          {(["orbit", "surface"] as const).map((m) => (
            <button
              key={m}
              onClick={() => setMode(m)}
              aria-pressed={mode === m}
              className={cn(
                "flex-1 rounded-md border px-3 py-1.5 text-sm capitalize transition-colors",
                mode === m ? "border-primary bg-secondary" : "border-border hover:bg-muted/50"
              )}
            >
              {m}
            </button>
          ))}
        </div>
      </Field>

      <Field label="Body">
        <select
          value={body}
          onChange={(e) => setBody(e.target.value)}
          className="h-9 w-full rounded-md border border-border bg-background px-2 text-sm"
        >
          {ctx.bodies.map((b) => (
            <option key={b.name} value={b.name}>
              {b.name}
              {b.modded ? " (modded)" : ""}
            </option>
          ))}
        </select>
      </Field>

      {mode === "orbit" ? (
        <div className="grid gap-3 sm:grid-cols-3">
          <Field label="Apoapsis (km)">
            <NumberInput value={ap} onChange={setAp} />
          </Field>
          <Field label="Periapsis (km)">
            <NumberInput value={pe} onChange={setPe} />
          </Field>
          <Field label={`Margin (km, min ${ctx.minMarginOrbitKm})`}>
            <NumberInput value={marginAlt} onChange={setMarginAlt} />
            <p className="mt-1.5 text-xs text-muted-foreground">
              How far off each of Ap and Pe may be (±km) and still count as delivered.
            </p>
          </Field>
        </div>
      ) : (
        <div className="grid gap-3 sm:grid-cols-3">
          <Field label="Latitude (°)">
            <NumberInput value={lat} onChange={setLat} />
            <p className="mt-1.5 text-xs text-muted-foreground">
              0° is the equator; +90° the north pole, −90° the south.
            </p>
          </Field>
          <Field label="Longitude (°)">
            <NumberInput value={lon} onChange={setLon} />
            <p className="mt-1.5 text-xs text-muted-foreground">
              −180° to 180° around the body's prime meridian; east is positive.
            </p>
          </Field>
          <Field label={`Margin (°, min ${ctx.minMarginSurfaceDeg})`}>
            <NumberInput value={marginPos} onChange={setMarginPos} />
            <p className="mt-1.5 text-xs text-muted-foreground">
              Radius around that spot that still counts; 1° ≈ 10.5 km on Kerbin.
            </p>
          </Field>
        </div>
      )}

      <Field label="What has to come back?">
        <div className="flex gap-2">
          {([
            ["crew", "Crew only"],
            ["vessel", "Crew + this vessel"],
          ] as const).map(([value, label]) => (
            <button
              key={value}
              onClick={() => setRecovery(value)}
              aria-pressed={recovery === value}
              className={cn(
                "flex-1 rounded-md border px-3 py-1.5 text-sm transition-colors",
                recovery === value ? "border-primary bg-secondary" : "border-border hover:bg-muted/50"
              )}
            >
              {label}
            </button>
          ))}
        </div>
        <p className="mt-1.5 text-xs text-muted-foreground">
          {recovery === "vessel"
            ? "A salvage job: they have to tow or fly this craft home too, not just the kerbals. Price it accordingly."
            : "They may strip or abandon this craft; only the kerbals have to arrive."}
        </p>
      </Field>

      <Field label="Δv they must have left (m/s, 0 = any)">
        <NumberInput value={minDv} onChange={setMinDv} />
        <p className="mt-1.5 text-xs text-muted-foreground">
          Checked on the craft that delivers the crew, so they aren't dropped somewhere they
          can't leave.
        </p>
      </Field>

      <p className="text-xs text-muted-foreground">
        Part restriction is automatic on a rescue: the rescuer needs your mods to load the wreck.
      </p>

      {/* Explicit, because sending this destroys the ship in your save and there is no
          undo. The classic window only warns after the fact. */}
      <label className="flex cursor-pointer items-start gap-2 rounded-md border border-destructive/40 bg-destructive/5 px-3 py-2 text-sm">
        <input
          type="checkbox"
          checked={confirmed}
          onChange={(e) => setConfirmed(e.target.checked)}
          className="mt-0.5 size-4 shrink-0"
        />
        <span>
          I understand <span className="font-medium">{ctx.rescue.vessel}</span> and its crew leave
          my save and become the rescuer's problem. This cannot be undone.
        </span>
      </label>
    </div>
  );
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div>
      <p className="mb-1.5 text-xs font-medium text-muted-foreground">{label}</p>
      {children}
    </div>
  );
}

/**
 * inputMode="decimal" rather than type="number": a number input silently reports "" for
 * anything it considers invalid, so a half-typed "-" or "1e" reads as empty and the
 * field appears to eat keystrokes.
 */
function NumberInput({
  value,
  onChange,
  placeholder,
}: {
  value: string;
  onChange: (v: string) => void;
  placeholder?: string;
}) {
  return (
    <input
      value={value}
      inputMode="decimal"
      placeholder={placeholder}
      onChange={(e) => onChange(e.target.value)}
      className="h-9 w-full rounded-md border border-border bg-transparent px-2 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
    />
  );
}

function Switch({ on }: { on: boolean }) {
  return (
    <span
      className={cn(
        "mt-0.5 flex h-5 w-9 shrink-0 items-center rounded-full p-0.5 transition-colors",
        on ? "bg-primary" : "bg-muted"
      )}
    >
      <span
        className={cn("size-4 rounded-full bg-background transition-transform", on && "translate-x-4")}
      />
    </span>
  );
}

function round(n: number): string {
  return Number.isFinite(n) ? String(Math.round(n * 100) / 100) : "0";
}

/** In flight but unavailable means the ship is there and empty, which is a different
 *  problem from being in the wrong scene — say which. */
function unavailableReason(ctx: ContractContext): string {
  if (ctx.scene === "FLIGHT") return "There is no crew aboard your current vessel.";
  switch (ctx.scene) {
    case "EDITOR":
      return "You are in the VAB/SPH.";
    case "SPACECENTER":
      return "You are at the Space Center.";
    case "TRACKSTATION":
      return "You are in the Tracking Station.";
    default:
      return "You are not in flight.";
  }
}
