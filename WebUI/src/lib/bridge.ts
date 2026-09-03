/**
 * bridge.ts — the only place this app talks to the mod.
 *
 * Two things worth understanding before changing anything here:
 *
 * 1. There is no API token in this app, and there must never be one. The page calls
 *    same-origin `/api/...`; the mod's C# proxy attaches the real session token before
 *    forwarding upstream. Nothing here can leak an account.
 *
 * 2. Auth is a one-time nonce in the launch URL, exchanged once for an HttpOnly cookie
 *    plus a CSRF token we hold in memory only. The nonce is single-use and expires in
 *    15 seconds, so a reload cannot repeat the handshake — which is why the cookie does
 *    the work from then on. Never persist `csrf` to localStorage: keeping it in memory
 *    is what makes an XSS bug non-durable.
 */

let csrf: string | null = null;

export class BridgeError extends Error {
  constructor(
    message: string,
    readonly status: number,
    readonly code?: string
  ) {
    super(message);
  }
}

/**
 * Exchanges the launch nonce for a session. Safe to call when there is no nonce in the
 * URL: on a reload the cookie is already set and the existing session still works.
 */
export async function openSession(): Promise<void> {
  const params = new URLSearchParams(window.location.search);
  const k = params.get("k");
  if (!k) return;

  // Drop the nonce from the address bar and history before anything can read it —
  // it is dead after this call anyway, but it does not belong in a shareable URL.
  window.history.replaceState(null, "", window.location.pathname);

  const res = await fetch("/gk/session", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ k }),
  });

  if (!res.ok) throw new BridgeError("Could not start a session.", res.status);
  csrf = (await res.json()).csrf;
}

async function request<T>(path: string, init: RequestInit = {}): Promise<T> {
  const headers = new Headers(init.headers);
  // A cross-origin page cannot set this header without a preflight, and the bridge
  // answers every preflight with 405. This is the CSRF defence.
  if (csrf) headers.set("X-GK-CSRF", csrf);
  if (init.body) headers.set("Content-Type", "application/json");

  let res: Response;
  try {
    res = await fetch(path, { ...init, headers });
  } catch {
    // fetch only rejects on a transport failure — the game closed, or the bridge died.
    throw new BridgeError("The game is not responding.", 0, "bridge_down");
  }

  if (res.status === 204) return undefined as T;

  const text = await res.text();
  let body: unknown = null;
  try {
    body = text ? JSON.parse(text) : null;
  } catch {
    /* upstream returned something that is not JSON; fall through to the status */
  }

  if (!res.ok) {
    const detail = body as { error?: string; detail?: string | { code?: string } } | null;
    // The bot API writes a deliberate refusal as a plain-string `detail` ("You've
    // already reported this contract; the mods have it"), and the mod relays the body
    // verbatim. That sentence is the entire answer, so it wins over the per-status
    // text — which would otherwise flatten it to "Request failed (409)" and send the
    // player back to press the same button again. A *structured* detail is the device
    // gate or a suspension: a code, not prose, and it keeps the old path.
    const prose = typeof detail?.detail === "string" ? detail.detail : undefined;
    const code =
      detail?.detail && typeof detail.detail === "object" ? detail.detail.code : undefined;
    throw new BridgeError(
      prose ?? messageForStatus(res.status, detail?.error),
      res.status,
      detail?.error ?? code
    );
  }
  return body as T;
}

function messageForStatus(status: number, code?: string): string {
  if (code === "transmission_blocked")
    return "Data sharing is turned off in KSP, so the mod is not contacting the server.";
  switch (status) {
    // The /gk/* routes answer a rejected value with a sentence a player can act on
    // ("Use just the host and port, with no path after it"), while the bot API answers
    // with a code word. A space is the cheapest way to tell those apart.
    case 400:
      return code && /\s/.test(code) ? code : "The mod could not use that value.";
    case 401:
      return "This session expired. Reopen the interface from the toolbar button in KSP.";
    case 403:
      return "The mod refused that request.";
    // The bridge's job queue is drained by KSP's Update(), which does not run during a
    // scene load — so a timeout here usually means "loading a save", not "broken".
    case 504:
      return "KSP is busy loading. This will recover on its own.";
    case 502:
      return "The mod could not reach the Boundless Missions server.";
    default:
      return `Request failed (${status}).`;
  }
}

export const api = {
  get: <T,>(path: string) => request<T>(path),
  post: <T,>(path: string, body?: unknown) =>
    request<T>(path, { method: "POST", body: body ? JSON.stringify(body) : undefined }),
  del: <T,>(path: string) => request<T>(path, { method: "DELETE" }),
};

/**
 * Live events pushed from the game.
 *
 * EventSource cannot set custom headers, so this authenticates on the SameSite=Strict
 * cookie alone — see BridgeAuth.IsAuthorizedCookieOnly on the C# side. It reconnects by
 * itself; the returned function is for unmount.
 */
export function subscribe(
  handlers: Record<string, (data: unknown) => void>,
  onStatus?: (connected: boolean) => void
): () => void {
  const es = new EventSource("/gk/events");

  es.onopen = () => onStatus?.(true);
  es.onerror = () => onStatus?.(false);

  for (const [name, fn] of Object.entries(handlers)) {
    es.addEventListener(name, (e) => {
      try {
        fn(JSON.parse((e as MessageEvent).data));
      } catch {
        /* a malformed frame must not kill the stream */
      }
    });
  }

  return () => es.close();
}

// ── Shapes ────────────────────────────────────────────────────────────────
// Deliberately partial: the bot API returns far more than this screen uses, and
// declaring only what is read keeps a server-side addition from breaking the build.

export interface GameState {
  version: string;
  scene: string;
  linked: boolean;
  username: string;
  serverUrl: string;
  consent: boolean;
  dataGathering: boolean;
  updateRequired: boolean;
  unread: number;
}

/** `/gk/settings` — the mod's own configuration, not the account's. */
export interface Settings {
  official: boolean;
  officialUrl: string;
  customUrl: string;
  serverUrl: string;
  linked: boolean;
  username: string;
  notifications: boolean;
  checkpointPhotos: boolean;
  // Whether the milestone-photo feature is available at all, decided by the mod
  // (ApiClient.CheckpointPhotosAvailable) and not by the player. False means don't
  // draw the switch: the preference above is still stored, it just governs nothing.
  // Optional because an older bridge does not send it — treat a missing value as
  // unavailable, since a switch that silently does nothing is the failure being avoided.
  checkpointPhotosAvailable?: boolean;
  dataGathering: boolean;
  // Privacy. `hidePlayerDetails` and `streamerMode` are the two stored switches;
  // `playerDetailsHidden` is the folded answer the player lists actually draw from
  // (either switch can produce it) and `broadcastApp` is what the streamer-mode scan
  // currently sees, "" for nothing. All optional: an older bridge sends none of them,
  // and absent must read as "not hiding" rather than as hiding for no stated reason.
  hidePlayerDetails?: boolean;
  streamerMode?: boolean;
  playerDetailsHidden?: boolean;
  broadcastApp?: string;
  updateRequired: boolean;
  modVersion: string;
  // True when the POST that returned this actually moved the mod to a different
  // server, which invalidates every piece of account data the page is holding.
  serverChanged: boolean;
}

export interface Profile {
  user_id: string;
  username: string;
  guild_id: string;
  xp: number;
  level: number;
  balance: number;
  messages: number;
  unlocked_levels: number[];
  currency_name: string;
  /** Unpaid contract fines. 0 for almost everyone. */
  debt: number;
  /** Share of earnings currently going to those fines; 0 when nothing is owed. */
  debt_garnish_percent: number;
  /**
   * Whether the bot @-mentions this account when it posts to their corp channel.
   * An account preference rather than a mod setting — the mention is written by
   * the server, so settings.cfg cannot turn it off. Written via
   * POST /api/v1/user/preferences.
   */
  corp_pings: boolean;
}

export interface Notification {
  id: string;
  type: string;
  title: string;
  message: string;
  timestamp: string;
  read: boolean;
  data?: Record<string, unknown> | null;
}

export interface NotificationsResponse {
  notifications: Notification[];
  unread_count: number;
}

export interface Mission {
  id: number;
  desc_en: string;
  desc_tr: string;
  difficulty: number;
  category: string;
  xp: number;
  coins: number;
  fine: number;
  mission_type: string;
  required_situation?: string | null;
  required_body?: string | null;
}

export interface WeeklyMissions {
  week_key: string;
  missions: Mission[];
  is_locked: boolean;
  closes_at: string;
}

export interface MissionSelectResult {
  success: boolean;
  contract_id?: string | null;
  message: string;
}

export interface ContractSummary {
  contract_id: string;
  mission: string;
  issuer_name: string;
  contractor_name: string;
  /** The two parties' immutable account ids. Names are display-only — deciding crew
   *  ownership on them let anyone take a victim's display name and have the victim's
   *  own kerbals adopted onto an arriving vessel. Empty from an older server. */
  issuer_id?: string;
  contractor_id?: string;
  payment: number;
  fine: number;
  due_date: string;
  status: string;
  created_at?: string | null;
  is_bot_issued: boolean;
  is_outgoing: boolean;
  modlist?: string | null;
  mission_type: string;
  required_situation?: string | null;
  required_body?: string | null;
  constraints?: Record<string, unknown> | null;
  flag_preview_url?: string | null;
  rescue_kerbals: string[];
  pending_request?: PendingRequest | null;
  /** When the fine collects itself if the dispute goes unresolved (ISO, UTC). */
  auto_fine_at?: string | null;
  /** The contractor has spent their one extension request for this dispute. */
  more_time_used?: boolean;
}

/**
 * An open ask from the contractor that only the issuer can answer. Settle and more-time
 * change nothing until they do, so while this is set a disputed contract is waiting on
 * the issuer, not on the contractor.
 */
export interface PendingRequest {
  kind: "settle" | "more_time";
  new_date?: string | null;
  requested_at?: string | null;
  requested_by?: string | null;
}

export interface ContractListResponse {
  contracts: ContractSummary[];
}

export interface Corp {
  owner_id: string;
  owner_name: string;
  corp_name: string;
  /**
   * The player's claimed Boundless username — the line the pickers draw under the
   * display name, where they used to draw `corp_name`. A corp is auto-named
   * "{display name} Space Agency", so that line was the name above it repeated,
   * while the one handle that identifies a player across every server was nowhere
   * in the list. "" (or absent, from an older bot) for a player with no username
   * yet, which draws as no second line rather than as a corp name — a fallback
   * would make the same column mean two different things on adjacent rows.
   */
  username?: string;
  // Both are best-effort from the bot: avatar_url is null when the member is not in
  // Discord's cache, and level is 0 for anyone with no economy record yet.
  avatar_url?: string | null;
  level?: number;
}

/**
 * `/api/v1/friends` — who this player may quicksend to.
 *
 * A friendship is mutual, explicit and guild-independent, and it is keyed on the
 * ACCOUNT id: a Discord snowflake for most players, "a_…" for a website-only
 * Boundless account. Nothing here distinguishes the two, and nothing may — the
 * whole point of the friend list is that both kinds of player are reachable.
 */
export interface Friend {
  user_id: string;
  name: string;
  /** The permanent Boundless username — what someone else types to add them. */
  username?: string;
  avatar_url?: string | null;
  level?: number;
  /** Epoch seconds: when the friendship began, or when the request was sent. */
  at?: number;
  /** Presentation only. A friendship never depends on this. */
  discord?: boolean;
}

export interface FriendList {
  friends: Friend[];
  incoming: Friend[];
  outgoing: Friend[];
  max_friends?: number;
}

export interface FriendActionResult {
  success: boolean;
  message: string;
  state?: string;
}

/** `/gk/contract/context` — what the create form needs from the running game. */
export interface ContractContext {
  scene: string;
  janitorsCloset: boolean;
  // The Janitor's Closet filter lives on the editor's part list, which only exists in
  // the VAB/SPH — so "installed" and "readable right now" are two different questions.
  editorFilterReadable: boolean;
  minMarginOrbitKm: number;
  minMarginSurfaceDeg: number;
  bodies: { name: string; modded: boolean }[];
  rescue: {
    available: boolean;
    vessel: string;
    crew: string[];
    body: string;
    apKm: number;
    peKm: number;
    lat: number;
    lon: number;
  };
}

export interface SubmissionPreview {
  vessel_name?: string;
  images?: { url: string }[];
  telemetry_urls?: string[];
  telemetry_url?: string;
  [k: string]: unknown;
}

/**
 * Routes a remote image through the mod. The page cannot load these hosts directly:
 * CSP is img-src 'self', deliberately, so a compromised bundle has no way to beacon
 * out. The C# side host-checks and content-sniffs before re-serving.
 */
export function imageUrl(remote: string): string {
  return `/api/img?u=${encodeURIComponent(remote)}`;
}

export interface Job {
  id: string;
  state: "running" | "done" | "error";
  message: string;
}

/**
 * Job completions arrive on the single app-wide SSE connection, but the code that
 * cares is whatever component started the job. This is the hand-off: App feeds
 * emitJob from its subscription, runJob listens for its own id.
 */
const jobListeners = new Set<(j: Job) => void>();

export function onJob(fn: (j: Job) => void): () => void {
  jobListeners.add(fn);
  return () => jobListeners.delete(fn);
}

export function emitJob(j: Job): void {
  jobListeners.forEach((fn) => fn(j));
}

/**
 * Starts a long game operation and resolves when it finishes.
 *
 * These make several network round trips and spawn vessels, so the bridge answers 202
 * immediately rather than holding the request open past its 30s timeout. Completion
 * normally arrives as an SSE "job" event; the poll is the fallback for a dropped stream,
 * which is also what covers a scene load happening mid-install.
 */
export async function runJob(path: string, body: unknown): Promise<Job> {
  const { job_id } = await api.post<{ job_id: string }>(path, body);

  return new Promise<Job>((resolve) => {
    let settled = false;
    const finish = (j: Job) => {
      if (settled || j.state === "running") return;
      settled = true;
      unsubscribe();
      clearInterval(timer);
      resolve(j);
    };

    const unsubscribe = onJob((j) => {
      if (j.id === job_id) finish(j);
    });

    const timer = setInterval(() => {
      api
        .get<Job>(`/gk/jobs/${encodeURIComponent(job_id)}`)
        .then(finish)
        .catch(() => {
          /* keep polling; a transient failure is not an outcome */
        });
    }, 2000);
  });
}
