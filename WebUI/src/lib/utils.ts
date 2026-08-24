import { clsx, type ClassValue } from "clsx";
import { twMerge } from "tailwind-merge";

export function cn(...inputs: ClassValue[]) {
  return twMerge(clsx(inputs));
}

/**
 * KSP's Vessel.Situations enum, made readable: SUB_ORBITAL -> "sub orbital".
 * The API passes these through verbatim, and they are not player-facing text.
 */
export function situationLabel(s: string): string {
  return s.toLowerCase().replace(/_/g, " ");
}
