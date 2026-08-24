import { useEffect, useMemo, useState } from "react";
import { Loader2, Search, Star, Users } from "lucide-react";
import { Button } from "@/components/ui/button";
import { type Corp, type Profile, type Settings, api, imageUrl } from "@/lib/bridge";
import { cn } from "@/lib/utils";

/**
 * Pick another player: avatar, name, corp, level, search, favorites.
 *
 * Shared by quicksend and contract creation. The two used to want "the same list" and
 * that is exactly the kind of thing that drifts — one grows a favorites filter, the
 * other keeps a bare <select>, and now starring somebody only works in one place.
 *
 * Favorites live in the mod's PluginData, not in this browser: the bridge binds a new
 * ephemeral port each session, so 127.0.0.1:<port> is a different origin on every KSP
 * launch and localStorage would silently start empty every time.
 */
export function PlayerPicker({
  value,
  onChange,
  emptyLabel = "No other players found.",
}: {
  value: string;
  onChange: (corp: Corp | null) => void;
  emptyLabel?: string;
}) {
  const [corps, setCorps] = useState<Corp[] | null>(null);
  const [me, setMe] = useState("");
  const [favorites, setFavorites] = useState<Set<string>>(new Set());
  const [query, setQuery] = useState("");
  const [favoritesOnly, setFavoritesOnly] = useState(false);
  // "Hide profile pictures and corp names" — either switched on by hand or turned on
  // by streamer mode spotting OBS. The mod folds the two into one answer so this list
  // and the in-game one cannot disagree about it.
  const [hideDetails, setHideDetails] = useState(false);

  useEffect(() => {
    api
      .get<{ corps: Corp[] }>("/api/v1/corps/list")
      .then((r) => setCorps(r.corps ?? []))
      .catch(() => setCorps([]));

    // Own id, so the list can drop self — there is no self-send and no self-contract,
    // and the classic windows filter the same way.
    api
      .get<Profile>("/api/v1/user/profile")
      .then((p) => setMe(p.user_id))
      .catch(() => setMe(""));

    api
      .get<{ favorites: string[] }>("/gk/favorites")
      .then((r) => setFavorites(new Set(r.favorites ?? [])))
      .catch(() => {
        /* favorites are a convenience; the picker works without them */
      });
  }, []);

  // Polled rather than read once, and at the same cadence the mod scans at: streamer
  // mode turns itself on when OBS starts, which is a change nobody made in this tab.
  // Setting a boolean means an unchanged answer costs no re-render.
  useEffect(() => {
    let alive = true;
    const read = () =>
      api
        .get<Settings>("/gk/settings")
        .then((s) => {
          if (alive) setHideDetails(s.playerDetailsHidden === true);
        })
        .catch(() => {
          /* an older bridge has no such field; showing the pictures is the old behaviour */
        });
    void read();
    const t = setInterval(read, 5000);
    return () => {
      alive = false;
      clearInterval(t);
    };
  }, []);

  async function toggleFavorite(id: string) {
    const next = !favorites.has(id);
    // Optimistic: the write is a local file, and reverting on failure keeps the star
    // honest without making every click wait on a frame of KSP's Update().
    setFavorites((prev) => {
      const s = new Set(prev);
      if (next) s.add(id);
      else s.delete(id);
      return s;
    });
    try {
      await api.post("/gk/favorites", { user_id: id, favorite: next });
    } catch {
      setFavorites((prev) => {
        const s = new Set(prev);
        if (next) s.delete(id);
        else s.add(id);
        return s;
      });
    }
  }

  const players = useMemo(() => {
    if (!corps) return [];
    const q = query.trim().toLowerCase();
    return corps
      .filter((c) => c.owner_id && c.owner_id !== me)
      .filter((c) => !favoritesOnly || favorites.has(c.owner_id))
      // The corp name is searchable only while it is shown: matching on a hidden
      // field answers a query with rows that look like they do not match it.
      .filter(
        (c) =>
          !q ||
          c.owner_name.toLowerCase().includes(q) ||
          (!hideDetails && (c.corp_name ?? "").toLowerCase().includes(q))
      )
      .sort((a, b) => {
        const fa = favorites.has(a.owner_id) ? 0 : 1;
        const fb = favorites.has(b.owner_id) ? 0 : 1;
        return fa - fb || a.owner_name.localeCompare(b.owner_name);
      });
  }, [corps, me, favorites, favoritesOnly, query, hideDetails]);

  return (
    <div className="space-y-2">
      <div className="flex gap-2">
        <div className="relative flex-1">
          <Search className="pointer-events-none absolute left-2 top-1/2 size-4 -translate-y-1/2 text-muted-foreground" />
          <input
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            placeholder="Search players…"
            className="h-9 w-full rounded-md border border-border bg-transparent pl-8 pr-2 text-sm"
          />
        </div>
        <Button
          size="sm"
          variant={favoritesOnly ? "secondary" : "outline"}
          onClick={() => setFavoritesOnly((v) => !v)}
          aria-pressed={favoritesOnly}
          title="Show favorites only"
        >
          <Star className={cn("size-4", favoritesOnly && "fill-current")} />
          {favorites.size > 0 ? favorites.size : ""}
        </Button>
      </div>

      <div className="max-h-72 space-y-1 overflow-y-auto rounded-md border border-border p-1">
        {corps === null ? (
          <Empty icon={Loader2} spin>
            Loading players…
          </Empty>
        ) : players.length === 0 ? (
          <Empty icon={Users}>
            {favoritesOnly
              ? "No favorites yet. Star someone to keep them here."
              : query
                ? `No player matches “${query}”.`
                : emptyLabel}
          </Empty>
        ) : (
          players.map((c) => (
            <PlayerRow
              key={c.owner_id}
              corp={c}
              hideDetails={hideDetails}
              selected={value === c.owner_id}
              favorite={favorites.has(c.owner_id)}
              onSelect={() => onChange(value === c.owner_id ? null : c)}
              onToggleFavorite={() => toggleFavorite(c.owner_id)}
            />
          ))
        )}
      </div>
    </div>
  );
}

/**
 * One player in the picker. The row is a div rather than a button because the star is
 * itself a button, and a button inside a button is invalid HTML that browsers resolve
 * unpredictably — so selection and starring are two sibling buttons instead.
 */
function PlayerRow({
  corp,
  hideDetails,
  selected,
  favorite,
  onSelect,
  onToggleFavorite,
}: {
  corp: Corp;
  hideDetails: boolean;
  selected: boolean;
  favorite: boolean;
  onSelect: () => void;
  onToggleFavorite: () => void;
}) {
  return (
    <div
      className={cn(
        "flex items-center gap-2 rounded-md px-2 py-1.5 transition-colors",
        selected ? "bg-secondary" : "hover:bg-muted/50"
      )}
    >
      <button
        onClick={onSelect}
        aria-pressed={selected}
        className="flex min-w-0 flex-1 items-center gap-3 rounded-md text-left focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
      >
        <Avatar corp={corp} hideDetails={hideDetails} />
        <span className="min-w-0 flex-1">
          <span className="block truncate text-sm font-medium">{corp.owner_name}</span>
          {!hideDetails && corp.corp_name && (
            <span className="block truncate text-xs text-muted-foreground">{corp.corp_name}</span>
          )}
        </span>
      </button>

      {!!corp.level && (
        <span className="shrink-0 rounded-full border border-border px-2 py-0.5 text-[11px] font-medium text-muted-foreground">
          Lv {corp.level}
        </span>
      )}

      <button
        onClick={onToggleFavorite}
        aria-label={favorite ? `Unstar ${corp.owner_name}` : `Star ${corp.owner_name}`}
        aria-pressed={favorite}
        className="shrink-0 rounded-md p-1.5 text-muted-foreground transition-colors hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
      >
        <Star className={cn("size-4", favorite && "fill-primary text-primary")} />
      </button>
    </div>
  );
}

/** Discord avatar through the mod's image proxy, with initials when there is none. */
function Avatar({ corp, hideDetails }: { corp: Corp; hideDetails: boolean }) {
  const [broken, setBroken] = useState(false);
  const initials = corp.owner_name.slice(0, 2).toUpperCase();

  // `hideDetails` first, so the <img> is never mounted and the proxy is never asked
  // for the picture — hidden should mean not fetched, not merely not painted. The
  // initials stand in, which keeps the row its normal height and gives nothing away:
  // they are the first letters of the name printed right beside them.
  if (hideDetails || !corp.avatar_url || broken) {
    return (
      <span className="flex size-9 shrink-0 items-center justify-center rounded-full bg-muted text-xs font-semibold text-muted-foreground">
        {initials}
      </span>
    );
  }

  return (
    <img
      src={imageUrl(corp.avatar_url)}
      alt=""
      onError={() => setBroken(true)}
      className="size-9 shrink-0 rounded-full bg-muted object-cover"
    />
  );
}

function Empty({
  icon: Icon,
  spin,
  children,
}: {
  icon: typeof Users;
  spin?: boolean;
  children: React.ReactNode;
}) {
  return (
    <p className="flex items-center gap-2 px-2 py-6 text-sm text-muted-foreground">
      <Icon className={cn("size-4 shrink-0", spin && "animate-spin")} />
      {children}
    </p>
  );
}
