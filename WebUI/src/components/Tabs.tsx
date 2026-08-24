import { useEffect, useState } from "react";
import { cn } from "@/lib/utils";

export const TABS = ["missions", "contracts", "tools", "profile", "settings"] as const;
export type Tab = (typeof TABS)[number];

const LABELS: Record<Tab, string> = {
  missions: "Missions",
  contracts: "Contracts",
  tools: "Tools",
  profile: "Profile",
  settings: "Settings",
};

/**
 * Hash routing rather than the History API. The mod serves this bundle from a plain
 * file server with no rewrite rules, so a real path would 404 on reload; a hash never
 * reaches the server at all. It also keeps the bundle host-agnostic, which is what
 * lets it run unchanged if it is ever embedded rather than served.
 */
export function useTab(): [Tab, (t: Tab) => void] {
  const [tab, setTab] = useState<Tab>(readHash);

  useEffect(() => {
    const onHash = () => setTab(readHash());
    window.addEventListener("hashchange", onHash);
    return () => window.removeEventListener("hashchange", onHash);
  }, []);

  return [
    tab,
    (t: Tab) => {
      window.location.hash = t;
      setTab(t);
    },
  ];
}

function readHash(): Tab {
  const h = window.location.hash.replace(/^#/, "") as Tab;
  return TABS.includes(h) ? h : "missions";
}

export function TabBar({
  tab,
  onChange,
  unread,
}: {
  tab: Tab;
  onChange: (t: Tab) => void;
  unread: number;
}) {
  return (
    <nav className="flex gap-1" role="tablist">
      {TABS.map((t) => (
        <button
          key={t}
          role="tab"
          aria-selected={tab === t}
          onClick={() => onChange(t)}
          className={cn(
            "relative rounded-md px-3 py-1.5 text-sm font-medium transition-colors",
            "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
            tab === t
              ? "bg-secondary text-secondary-foreground"
              : "text-muted-foreground hover:text-foreground"
          )}
        >
          {LABELS[t]}
          {t === "profile" && unread > 0 && (
            <span className="ml-1.5 inline-flex h-4 min-w-4 items-center justify-center rounded-full bg-primary px-1 text-[10px] font-semibold text-primary-foreground">
              {unread > 99 ? "99+" : unread}
            </span>
          )}
        </button>
      ))}
    </nav>
  );
}
