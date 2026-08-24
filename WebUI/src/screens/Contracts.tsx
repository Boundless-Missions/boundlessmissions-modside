import { useCallback, useEffect, useState } from "react";
import { ArrowLeft, Download, Flag, ImageIcon, Loader2, Plus, Upload, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Card, CardContent } from "@/components/ui/card";
import { Separator } from "@/components/ui/separator";
import {
  BridgeError,
  type ContractListResponse,
  type ContractSummary,
  type SubmissionPreview,
  api,
  imageUrl,
  runJob,
} from "@/lib/bridge";
import { CreateContract } from "@/screens/CreateContract";
import { cn, situationLabel } from "@/lib/utils";

type Filter = "all" | "incoming" | "outgoing";

/** Mirrors data/contracts.py so the two never drift apart. */
const STATUS_STYLE: Record<string, string> = {
  pending: "border-amber-500/40 text-amber-400",
  active: "border-sky-500/40 text-sky-400",
  submitted: "border-violet-500/40 text-violet-400",
  completed: "border-emerald-500/40 text-emerald-400",
  disputed: "border-red-500/40 text-red-400",
  mod_review: "border-orange-500/40 text-orange-400",
  cancelled: "border-border text-muted-foreground",
};

/**
 * Whether this contract is waiting on *you* specifically.
 *
 * Exported and shared with <Actions> below so the inbox marker and the actual buttons
 * can never disagree — a "needs you" dot on a contract with no available action would
 * be worse than no dot at all.
 */
export function needsAction(c: ContractSummary): boolean {
  switch (c.status) {
    case "pending":
      return true; // issuer can withdraw, contractor can accept or decline
    case "active":
      return !c.is_outgoing;
    case "submitted":
      return c.is_outgoing; // the issuer reviews
    case "disputed":
      return !c.is_outgoing; // the contractor resolves
    default:
      return false;
  }
}

/** Newest first. created_at is optional on the model, so fall back to the due date. */
function sortKey(c: ContractSummary): number {
  const t = Date.parse(c.created_at ?? "") || Date.parse(c.due_date ?? "") || 0;
  return t;
}

export function Contracts({ refreshKey }: { refreshKey: number }) {
  const [contracts, setContracts] = useState<ContractSummary[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [filter, setFilter] = useState<Filter>("all");
  const [openId, setOpenId] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);

  const load = useCallback(async () => {
    try {
      const res = await api.get<ContractListResponse>("/api/v1/contracts/active");
      setContracts(res.contracts ?? []);
      setError(null);
    } catch (e) {
      setError(e instanceof BridgeError ? e.message : "Could not load contracts.");
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load, refreshKey]);

  const open = contracts.find((c) => c.contract_id === openId) ?? null;

  // The API returns these in storage order, which interleaves finished contracts with
  // live ones. Sort newest-first so it reads like an inbox rather than a dump.
  const shown = contracts
    .filter((c) => (filter === "all" ? true : filter === "outgoing" ? c.is_outgoing : !c.is_outgoing))
    .slice()
    .sort((a, b) => sortKey(b) - sortKey(a));

  const pending = shown.filter(needsAction).length;

  if (loading) {
    return (
      <div className="flex items-center gap-2 py-10 text-sm text-muted-foreground">
        <Loader2 className="size-4 animate-spin" /> Loading contracts…
      </div>
    );
  }

  if (error) return <p className="py-8 text-sm text-muted-foreground">{error}</p>;

  const onActed = () => {
    // The contract's status just changed, so the open detail is stale — clear it and
    // reload rather than leaving buttons up that no longer apply.
    setOpenId(null);
    void load();
  };

  return (
    // Master-detail once there is room: on a 16:9 screen, replacing the list with the
    // detail wastes half the width and loses your place in the inbox. Below xl it falls
    // back to push-navigation, which is why Detail keeps its Back button.
    <div className="flex flex-col gap-4 xl:flex-row xl:items-start">
      <div
        className={cn(
          "min-w-0 space-y-3 xl:w-[26rem] xl:shrink-0",
          (open || creating) && "hidden xl:block"
        )}
      >
      <div className="flex flex-wrap items-center gap-1">
        {(["all", "incoming", "outgoing"] as Filter[]).map((f) => (
          <button
            key={f}
            onClick={() => setFilter(f)}
            className={cn(
              "rounded-md px-3 py-1 text-xs font-medium capitalize transition-colors",
              filter === f
                ? "bg-secondary text-secondary-foreground"
                : "text-muted-foreground hover:text-foreground"
            )}
          >
            {f}
          </button>
        ))}
        {pending > 0 && (
          <span className="ml-2 text-xs text-muted-foreground">
            {pending} waiting on you
          </span>
        )}
        <Button
          size="sm"
          variant="outline"
          className="ml-auto"
          onClick={() => {
            // The form takes the detail slot, so an open contract would be hidden
            // behind it with no way back.
            setOpenId(null);
            setCreating(true);
          }}
        >
          <Plus className="size-4" /> New
        </Button>
      </div>

      {shown.length === 0 ? (
        <Card>
          <CardContent className="py-10 text-center text-sm text-muted-foreground">
            Nothing here. Accept a mission to start a contract.
          </CardContent>
        </Card>
      ) : (
        <Card className="overflow-hidden">
          <ul className="divide-y divide-border">
            {shown.map((c) => (
              <li key={c.contract_id}>
                <button
                  onClick={() => {
                    setCreating(false); // both live in the detail slot
                    setOpenId(c.contract_id);
                  }}
                  aria-current={openId === c.contract_id}
                  className={cn(
                    "flex w-full items-start gap-3 px-4 py-3 text-left transition-colors hover:bg-muted/40",
                    openId === c.contract_id && "bg-muted/60"
                  )}
                >
                  <span
                    aria-hidden
                    title={needsAction(c) ? "Waiting on you" : undefined}
                    className={cn(
                      "mt-2 size-2 shrink-0 rounded-full",
                      needsAction(c) ? "bg-primary" : "bg-transparent"
                    )}
                  />
                  <div className="min-w-0 flex-1">
                    <div className="mb-1 flex flex-wrap items-center gap-2">
                      <StatusBadge status={c.status} />
                      <span className="text-xs text-muted-foreground">
                        {c.is_outgoing ? `to ${c.contractor_name}` : `from ${c.issuer_name}`}
                      </span>
                    </div>
                    <p className="truncate text-sm">{c.mission}</p>
                    <div className="mt-1 flex flex-wrap gap-x-4 text-xs text-muted-foreground">
                      <span>{c.payment.toLocaleString()} KCoins</span>
                      <span>due {formatDate(c.due_date)}</span>
                    </div>
                  </div>
                </button>
              </li>
            ))}
          </ul>
        </Card>
      )}
      </div>

      {creating ? (
        <CreateContract
          onCancel={() => setCreating(false)}
          onCreated={() => {
            setCreating(false);
            void load();
          }}
        />
      ) : open ? (
        <div className="min-w-0 flex-1">
          <Detail contract={open} onBack={() => setOpenId(null)} onActed={onActed} />
        </div>
      ) : (
        <div className="hidden flex-1 items-center justify-center rounded-lg border border-dashed border-border py-24 text-sm text-muted-foreground xl:flex">
          Select a contract to see its details.
        </div>
      )}
    </div>
  );
}

function Detail({
  contract,
  onBack,
  onActed,
}: {
  contract: ContractSummary;
  onBack: () => void;
  onActed: () => void;
}) {
  const [preview, setPreview] = useState<SubmissionPreview | null>(null);
  const [loadingPreview, setLoadingPreview] = useState(false);
  const [zoom, setZoom] = useState<string | null>(null);

  // Only submitted-or-later contracts have anything to preview, so don't spend a
  // round trip (which goes through KSP's main thread) on the ones that cannot.
  const hasSubmission = ["submitted", "completed", "disputed", "mod_review"].includes(
    contract.status
  );

  useEffect(() => {
    if (!hasSubmission) return;
    setLoadingPreview(true);
    api
      .get<SubmissionPreview>(`/api/v1/contracts/${encodeURIComponent(contract.contract_id)}/submission`)
      .then(setPreview)
      .catch(() => setPreview(null))
      .finally(() => setLoadingPreview(false));
  }, [contract.contract_id, hasSubmission]);

  const images = [
    ...(preview?.images?.map((i) => i.url) ?? []),
    ...(preview?.telemetry_urls ?? []),
    ...(preview?.telemetry_url ? [preview.telemetry_url] : []),
  ];

  return (
    <div className="space-y-4">
      {/* Only meaningful in the stacked layout — on xl the list is still on screen. */}
      <Button variant="ghost" size="sm" onClick={onBack} className="xl:hidden">
        <ArrowLeft className="size-4" /> Back
      </Button>

      <Card className="animate-fade-up">
        <CardContent className="space-y-4 py-5">
          <div className="flex flex-wrap items-center gap-2">
            <StatusBadge status={contract.status} />
            {contract.is_bot_issued && (
              <Badge variant="secondary" className="text-xs">Weekly mission</Badge>
            )}
            {contract.mission_type === "craft_build" && (
              <Badge variant="secondary" className="text-xs">Craft build</Badge>
            )}
            {contract.mission_type === "rescue" && (
              <Badge variant="secondary" className="text-xs">Rescue</Badge>
            )}
          </div>

          <p className="text-sm leading-relaxed">{contract.mission}</p>

          <Separator />

          <dl className="grid grid-cols-2 gap-x-6 gap-y-3 text-sm sm:grid-cols-3">
            <Field label="Issuer" value={contract.issuer_name} />
            <Field label="Contractor" value={contract.contractor_name} />
            <Field label="Payment" value={`${contract.payment.toLocaleString()} KCoins`} />
            <Field label="Fine" value={contract.fine.toLocaleString()} />
            <Field label="Due" value={formatDate(contract.due_date)} />
            {contract.required_body && <Field label="Body" value={contract.required_body} />}
            {contract.required_situation && (
              <Field label="Situation" value={situationLabel(contract.required_situation)} />
            )}
          </dl>

          {contract.rescue_kerbals.length > 0 && (
            <>
              <Separator />
              <div>
                <Label>Kerbals to recover</Label>
                <p className="text-sm">{contract.rescue_kerbals.join(", ")}</p>
              </div>
            </>
          )}

          {contract.modlist && (
            <>
              <Separator />
              <div>
                <Label>Mods allowed</Label>
                <p className="break-words font-mono text-xs text-muted-foreground">
                  {contract.modlist}
                </p>
              </div>
            </>
          )}
        </CardContent>
      </Card>

      {contract.flag_preview_url && (
        <Card>
          <CardContent className="py-5">
            <Label>Flag preview</Label>
            <img
              src={imageUrl(contract.flag_preview_url)}
              alt="Flag preview"
              className="mt-2 max-h-48 rounded-md border border-border"
            />
          </CardContent>
        </Card>
      )}

      {hasSubmission && (
        <Card>
          <CardContent className="py-5">
            <Label>
              Submission{preview?.vessel_name ? `: ${preview.vessel_name}` : ""}
            </Label>

            {loadingPreview ? (
              <div className="flex items-center gap-2 py-6 text-sm text-muted-foreground">
                <Loader2 className="size-4 animate-spin" /> Loading images…
              </div>
            ) : images.length === 0 ? (
              <p className="py-4 text-sm text-muted-foreground">No images were submitted.</p>
            ) : (
              <div className="mt-3 grid grid-cols-2 gap-3 sm:grid-cols-3">
                {images.map((url) => (
                  <button
                    key={url}
                    onClick={() => setZoom(url)}
                    className="group relative aspect-video overflow-hidden rounded-md border border-border bg-muted"
                  >
                    <img
                      src={imageUrl(url)}
                      alt=""
                      loading="lazy"
                      className="size-full object-cover transition-transform group-hover:scale-105"
                    />
                    <ImageIcon className="absolute bottom-1.5 right-1.5 size-3.5 text-white/70" />
                  </button>
                ))}
              </div>
            )}
          </CardContent>
        </Card>
      )}

      {contract.status === "completed" && contract.is_outgoing && <InstallCraft contract={contract} />}

      <Actions contract={contract} onActed={onActed} />

      {zoom && <Lightbox url={zoom} onClose={() => setZoom(null)} />}
    </div>
  );
}

/**
 * Which buttons exist is driven by two things: the contract's status, and whether you
 * are the issuer or the contractor. `is_outgoing` means "I issued this". The mapping
 * mirrors DrawContractDetail exactly — if the two ever disagree, players will be able
 * to press something in one UI that the server rejects for the other.
 */
function Actions({
  contract: c,
  onActed,
}: {
  contract: ContractSummary;
  onActed: () => void;
}) {
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [confirming, setConfirming] = useState<string | null>(null);
  const [newDate, setNewDate] = useState("");
  const [pickingDate, setPickingDate] = useState(false);
  const [handedOff, setHandedOff] = useState<string | null>(null);
  const [reporting, setReporting] = useState(false);
  const [reportText, setReportText] = useState("");
  const [reportNote, setReportNote] = useState<string | null>(null);

  const id = encodeURIComponent(c.contract_id);

  /**
   * Report the other party to the moderators.
   *
   * Not `act()`: that reloads the list on success, and a report leaves the contract
   * exactly as it was — the row would flicker and nothing would have changed. The
   * server's own sentence is what gets shown, in both directions: the ticket name on
   * success, and on refusal ("you have already reported this") the reason, which the
   * bridge now preserves rather than flattening to a status number.
   */
  async function sendReport() {
    const reason = reportText.trim();
    if (!reason) return;
    setBusy("report");
    setError(null);
    try {
      const r = await api.post<{ success: boolean; message: string }>(
        `/api/v1/contracts/${id}/report`,
        { reason }
      );
      setReporting(false);
      setReportText("");
      setReportNote(r?.message || "Reported. A moderator will pick it up in Discord.");
    } catch (e) {
      setError(e instanceof BridgeError ? e.message : "Could not file that report.");
    } finally {
      setBusy(null);
    }
  }

  async function act(key: string, path: string, body?: unknown) {
    setBusy(key);
    setError(null);
    try {
      await api.post(`/api/v1/contracts/${id}/${path}`, body);
      onActed();
    } catch (e) {
      setError(e instanceof BridgeError ? e.message : "That action failed.");
      setBusy(null);
      setConfirming(null);
    }
  }

  /**
   * Submission is the one thing that cannot happen in a browser: it waits for physics
   * to settle, measures live distance between vessels, and captures a screenshot with
   * the HUD hidden — all of which need the game focused, not this tab. So the button
   * raises the real in-game window and tells the player to switch to KSP.
   */
  async function openSubmit() {
    setBusy("submit");
    setError(null);
    try {
      const job = await runJob("/gk/actions/open-submit", { contract_id: c.contract_id });
      if (job.state === "done") setHandedOff(job.message);
      else setError(job.message || "Could not open the submit window.");
    } catch (e) {
      setError(e instanceof BridgeError ? e.message : "Could not open the submit window.");
    } finally {
      setBusy(null);
    }
  }

  /** Two-click confirm for anything that costs money or forfeits work. */
  function danger(key: string, label: string, confirmLabel: string, run: () => void) {
    const armed = confirming === key;
    return (
      <Button
        variant={armed ? "default" : "outline"}
        size="sm"
        disabled={busy !== null}
        onClick={() => (armed ? run() : setConfirming(key))}
        className={armed ? "bg-destructive text-destructive-foreground hover:bg-destructive/90" : ""}
      >
        {busy === key && <Loader2 className="size-4 animate-spin" />}
        {armed ? confirmLabel : label}
      </Button>
    );
  }

  let buttons: React.ReactNode = null;
  // Rendered under the button row: forms that belong to an action rather than being one.
  let extra: React.ReactNode = null;

  if (c.status === "pending") {
    buttons = c.is_outgoing ? (
      danger("cancel", "Withdraw offer", "Confirm withdraw", () => act("cancel", "cancel"))
    ) : (
      <>
        <Button size="sm" disabled={busy !== null} onClick={() => act("accept", "accept")}>
          {busy === "accept" && <Loader2 className="size-4 animate-spin" />}
          Accept contract
        </Button>
        {danger("decline", "Decline", "Confirm decline", () => act("decline", "cancel"))}
      </>
    );
  } else if (c.status === "active" && !c.is_outgoing) {
    buttons = (
      <>
        <Button size="sm" disabled={busy !== null} onClick={openSubmit}>
          {busy === "submit" ? (
            <Loader2 className="size-4 animate-spin" />
          ) : (
            <Upload className="size-4" />
          )}
          Submit in KSP
        </Button>
        {danger("give_up", "Give up", "Confirm: you pay the fine", () =>
          act("give_up", "give_up")
        )}
      </>
    );
  } else if (c.status === "submitted") {
    buttons = c.is_outgoing ? (
      <>
        <Button
          size="sm"
          disabled={busy !== null}
          onClick={() => act("approve", "review", { approve: true })}
        >
          {busy === "approve" && <Loader2 className="size-4 animate-spin" />}
          Approve &amp; pay
        </Button>
        {danger("refuse", "Refuse", "Confirm refuse", () =>
          act("refuse", "review", { approve: false })
        )}
      </>
    ) : (
      <Waiting>Waiting for {c.issuer_name} to review your submission.</Waiting>
    );
  } else if (c.status === "disputed") {
    const req = c.pending_request;
    if (c.is_outgoing) {
      // Settle and more-time are asks, not actions — nothing has changed yet and the
      // contract is waiting on this player. Answering them lived only in a Discord DM
      // until Phase 6a, which stalled the dispute for anyone playing without Discord.
      //
      // "Accept it after all" sits alongside either state, because changing your mind
      // about a refusal is the one way out of a dispute that favours the contractor —
      // settle needs this player anyway, sue spends moderator time, and the auto-fine
      // clock only ever ends it against them.
      const acceptAnyway = (
        <Button
          size="sm"
          variant="outline"
          disabled={busy !== null}
          onClick={() => act("accept_anyway", "review", { approve: true })}
        >
          {busy === "accept_anyway" && <Loader2 className="size-4 animate-spin" />}
          Accept the submission after all
        </Button>
      );

      buttons = req ? (
        <>
          <span className="text-sm">
            {req.kind === "settle"
              ? `${c.contractor_name} asks to settle: no payment, no fine.`
              : `${c.contractor_name} asks to move the deadline to ${req.new_date}.`}
          </span>
          <Button
            size="sm"
            disabled={busy !== null}
            onClick={() =>
              act("approve_request", `${req.kind}_response`, { approve: true })
            }
          >
            {busy === "approve_request" && <Loader2 className="size-4 animate-spin" />}
            Approve
          </Button>
          <Button
            size="sm"
            variant="outline"
            disabled={busy !== null}
            onClick={() =>
              act("refuse_request", `${req.kind}_response`, { approve: false })
            }
          >
            {busy === "refuse_request" && <Loader2 className="size-4 animate-spin" />}
            Refuse
          </Button>
          {acceptAnyway}
        </>
      ) : (
        <>
          <Waiting>Waiting for {c.contractor_name} to resolve the dispute.</Waiting>
          {acceptAnyway}
        </>
      );
    } else if (req) {
      buttons = (
        <Waiting>
          {req.kind === "settle"
            ? `Waiting for ${c.issuer_name} to answer your settlement request.`
            : `Waiting for ${c.issuer_name} to answer your extension request (${req.new_date}).`}
        </Waiting>
      );
    } else {
      buttons = (
      <>
        <Button
          size="sm"
          variant="outline"
          disabled={busy !== null}
          onClick={() => act("settle", "dispute", { action: "settle" })}
        >
          Settle
        </Button>

        {/* One request per dispute, so once it is spent the control goes away rather
            than sitting there to be pressed and refused. The server enforces it too. */}
        {!c.more_time_used && (
          <Button
            size="sm"
            variant="outline"
            disabled={busy !== null}
            onClick={() => {
              // A bot issuer has nobody to ask, so it extends on its own schedule and
              // needs no date — the picker would be a decision with no effect.
              if (c.is_bot_issued) act("more_time", "dispute", { action: "more_time" });
              else setPickingDate((v) => !v);
            }}
          >
            {busy === "more_time" && <Loader2 className="size-4 animate-spin" />}
            Ask for more time
          </Button>
        )}

        {danger("pay_fine", "Pay the fine", `Confirm: ${c.fine.toLocaleString()} KCoins`, () =>
          act("pay_fine", "dispute", { action: "pay_fine" })
        )}
        {danger("sue", "Sue", "Confirm: send to moderators", () =>
          act("sue", "dispute", { action: "sue" })
        )}
      </>
      );

      // The picker used to sit inline in the button row, where it read as a filter
      // rather than as part of an action, and pressing "Ask for more time" with an
      // empty date silently did nothing. It is a small form of its own now, below the
      // row, submitted from inside itself — and since the ask is one-shot, it says so.
      extra = pickingDate ? (
        <div className="rounded-md border border-border bg-muted/30 p-3">
          <label className="block text-xs font-medium" htmlFor="more-time-date">
            New due date
          </label>
          <p className="mt-1 text-xs text-muted-foreground">
            You get one extension request per dispute, so pick carefully.{" "}
            {c.issuer_name} still has to agree to it.
          </p>
          <div className="mt-2 flex flex-wrap items-center gap-2">
            <input
              id="more-time-date"
              type="date"
              value={newDate}
              min={tomorrow()}
              onChange={(e) => setNewDate(e.target.value)}
              className="h-8 rounded-md border border-border bg-transparent px-2 text-xs"
            />
            <Button
              size="sm"
              disabled={busy !== null || !newDate}
              onClick={() =>
                act("more_time", "dispute", { action: "more_time", new_date: newDate })
              }
            >
              {busy === "more_time" && <Loader2 className="size-4 animate-spin" />}
              Send request
            </Button>
            <Button
              size="sm"
              variant="ghost"
              disabled={busy !== null}
              onClick={() => setPickingDate(false)}
            >
              Cancel
            </Button>
          </div>
        </div>
      ) : null;
    }
  }

  // A report is not a move in the contract, so it is offered in every state — an
  // abusive mission text is still abusive once the deal is finished. Never for a
  // weekly mission: the bot is not a person a moderator could take it up with, and
  // a broken mission is a bug report (the Tools tab files those).
  const canReport = !c.is_bot_issued;
  const otherParty = c.is_outgoing ? c.contractor_name : c.issuer_name;

  // The card used to disappear entirely once no action was left. It now survives for
  // the report row alone, which is exactly the state most reports are written in.
  if (!buttons && !canReport) return null;

  return (
    <Card>
      <CardContent className="space-y-3 py-4">
        {buttons && <div className="flex flex-wrap items-center gap-2">{buttons}</div>}
        {extra}
        {c.status === "disputed" && c.auto_fine_at && (
          <p className="text-xs text-muted-foreground">
            Unresolved disputes settle themselves: the {c.fine.toLocaleString()} KCoin
            fine is collected automatically on {formatDeadline(c.auto_fine_at)}.
          </p>
        )}
        {confirming && (
          <p className="text-xs text-muted-foreground">Click again to confirm, or reload to cancel.</p>
        )}
        {handedOff && (
          <p className="flex items-start gap-2 text-sm text-muted-foreground">
            <Upload className="mt-0.5 size-4 shrink-0 text-primary" />
            {handedOff}
          </p>
        )}
        {error && <p className="text-sm text-destructive">{error}</p>}

        {canReport &&
          (reporting ? (
            <div className="rounded-md border border-border bg-muted/30 p-3">
              <label className="block text-xs font-medium" htmlFor="report-reason">
                What went wrong with {otherParty}?
              </label>
              <textarea
                id="report-reason"
                autoFocus
                rows={4}
                maxLength={1500}
                value={reportText}
                onChange={(e) => setReportText(e.target.value)}
                placeholder="Abusive text, a deal they refuse to honour, a contract written to collect the fine…"
                className="mt-2 w-full resize-y rounded-md border border-border bg-transparent px-2 py-1.5 text-xs"
              />
              <p className="mt-1 text-xs text-muted-foreground">
                This opens a private ticket in Discord with a moderator pinged. The
                contract and both accounts are attached to it. A late delivery or a
                disagreement about the work is not a report — the dispute buttons are
                for those.
              </p>
              <div className="mt-2 flex flex-wrap items-center gap-2">
                <Button
                  size="sm"
                  disabled={busy !== null || !reportText.trim()}
                  onClick={sendReport}
                  className="bg-destructive text-destructive-foreground hover:bg-destructive/90"
                >
                  {busy === "report" && <Loader2 className="size-4 animate-spin" />}
                  Send report
                </Button>
                <Button
                  size="sm"
                  variant="ghost"
                  disabled={busy !== null}
                  onClick={() => setReporting(false)}
                >
                  Never mind
                </Button>
              </div>
            </div>
          ) : reportNote ? (
            <p className="flex items-start gap-2 text-sm text-muted-foreground">
              <Flag className="mt-0.5 size-4 shrink-0 text-destructive" />
              {reportNote}
            </p>
          ) : (
            <button
              onClick={() => {
                setError(null);
                setReporting(true);
              }}
              className="flex items-center gap-1.5 text-xs text-muted-foreground transition-colors hover:text-destructive"
            >
              <Flag className="size-3.5" />
              Report {otherParty}
            </button>
          ))}
      </CardContent>
    </Card>
  );
}

function Waiting({ children }: { children: React.ReactNode }) {
  return <p className="text-sm text-muted-foreground">{children}</p>;
}

/** Earliest date the server will accept for an extension — it must be in the future. */
function tomorrow(): string {
  const d = new Date();
  d.setDate(d.getDate() + 1);
  return d.toISOString().slice(0, 10);
}

/**
 * The auto-fine instant, in the player's own timezone. The server sends UTC because the
 * policy is measured in days from when the dispute opened, and a player reading "the
 * 14th" while their clock says the 13th would reasonably think they had another day.
 */
function formatDeadline(iso: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return iso;
  return d.toLocaleString(undefined, {
    dateStyle: "medium",
    timeStyle: "short",
  });
}

/**
 * Installs the craft a completed contract delivered.
 *
 * Only offered to the issuer (`is_outgoing`) — the contractor built it, the issuer is
 * the one who paid to receive it. Gated on the KSP scene because importing a vessel node
 * spawns into the save: the mod does that safely from the Space Center, and the game is
 * the thing that knows where the player is, so we read it from /gk/state rather than
 * guessing here.
 */
function InstallCraft({ contract }: { contract: ContractSummary }) {
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<{ ok: boolean; message: string } | null>(null);
  const [scene, setScene] = useState<string>("");

  useEffect(() => {
    api
      .get<{ scene: string }>("/gk/state")
      .then((s) => setScene(s.scene))
      .catch(() => setScene(""));
  }, []);

  const sceneOk = scene === "SPACECENTER" || scene === "FLIGHT" || scene === "TRACKSTATION";

  async function install() {
    setBusy(true);
    setResult(null);
    try {
      const job = await runJob("/gk/actions/install-craft", {
        contract_id: contract.contract_id,
        owner_name: contract.contractor_name,
      });
      setResult({ ok: job.state === "done", message: job.message });
    } catch (e) {
      setResult({
        ok: false,
        message: e instanceof BridgeError ? e.message : "Install failed.",
      });
    } finally {
      setBusy(false);
    }
  }

  return (
    <Card>
      <CardContent className="space-y-3 py-4">
        <div className="flex flex-wrap items-center gap-3">
          <Button size="sm" onClick={install} disabled={busy || !sceneOk}>
            {busy ? <Loader2 className="size-4 animate-spin" /> : <Download className="size-4" />}
            {busy ? "Installing…" : "Install craft into my save"}
          </Button>
          {!sceneOk && (
            <span className="text-xs text-muted-foreground">
              Go to the Space Center in KSP first.
            </span>
          )}
        </div>
        {result && (
          <p className={cn("text-sm", result.ok ? "text-primary" : "text-destructive")}>
            {result.message}
          </p>
        )}
      </CardContent>
    </Card>
  );
}

function Lightbox({ url, onClose }: { url: string; onClose: () => void }) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && onClose();
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);

  return (
    <div
      className="fixed inset-0 z-50 flex items-center justify-center bg-black/85 p-6"
      onClick={onClose}
      role="dialog"
      aria-modal="true"
    >
      <img
        src={imageUrl(url)}
        alt=""
        className="max-h-full max-w-full rounded-md"
        onClick={(e) => e.stopPropagation()}
      />
      <Button
        variant="ghost"
        size="icon"
        onClick={onClose}
        className="absolute right-4 top-4"
        aria-label="Close"
      >
        <X className="size-5" />
      </Button>
    </div>
  );
}

function StatusBadge({ status }: { status: string }) {
  return (
    <Badge variant="outline" className={cn("text-xs", STATUS_STYLE[status] ?? "")}>
      {status.replace(/_/g, " ")}
    </Badge>
  );
}

function Field({ label, value }: { label: string; value: string }) {
  return (
    <div>
      <Label>{label}</Label>
      <dd className="text-sm">{value}</dd>
    </div>
  );
}

function Label({ children }: { children: React.ReactNode }) {
  return (
    <dt className="text-xs uppercase tracking-wide text-muted-foreground">{children}</dt>
  );
}

function formatDate(s: string): string {
  const d = new Date(s);
  return Number.isNaN(d.getTime()) ? s : d.toLocaleDateString();
}
