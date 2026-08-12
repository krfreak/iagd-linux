// Transport to iagd-host.
//
// Upstream's UI reached its host through `chrome.webview.hostObjects.sync.core` — a
// synchronous WebView2 bridge — and received pushes via a global `window.message`. Neither
// exists here: the host is an ordinary HTTP server, so requests are fetch and pushes come
// over a WebSocket. Keeping that in one file means the UI never knows the difference.

export interface ItemSummary {
  id: number;
  name: string;
  baseRecord: string;
  seed: number;
  itemClass: string | null;
  quality: string | null;
  level: number;
  icon: string | null;
  isHardcore: boolean;
  /** IA's display colour ("Epic" is the game's Legendary); null until analysed. */
  rarity: string | null;
  /** Count of Rare affixes, not a rarity. */
  prefixRarity: number;
  /** How many are in the stack; 1 for anything that does not stack. */
  stackCount: number;
}

export interface ItemStatLine {
  textClass: number;
  text: string;
}

/** A skill the item grants. Null when it grants none. */
export interface ItemSkillInfo {
  name: string | null;
  description: string | null;
  level: number;
  /** Set when the skill fires by itself (a proc) rather than going on the action bar. */
  trigger: string | null;
  summonsPets: boolean;
}

export interface ItemDetail {
  item: ItemSummary;
  stats: ItemStatLine[];
  skill: ItemSkillInfo | null;
}

export interface ItemPage {
  items: ItemSummary[];
  total: number;
  skip: number;
  take: number;
}

export interface HostStatus {
  gameRunning: boolean;
  gameStartedAt: string | null;
  hookAttached: boolean;
  pendingLootFiles: number;
  itemCount: number;
  templateCount: number;
  gameDir: string | null;
  bridgeDir: string;
  databaseFile: string;
  /** Items the precompute pass has not seen; rarity and level filters cannot match them. */
  itemsNeedingStats: number;
  /** Why the parsed game data is out of date, or null when current. */
  gameDataStale: string | null;
  /** True while the host is attaching the hook to the running game. */
  attaching: boolean;
}

/** Immediate response to queueing a transfer; the outcome arrives as an event. */
export interface TransferTarget {
  /** Empty string is vanilla. */
  mod?: string;
  hardcore?: boolean;
}

export interface TransferQueued {
  transferId: string;
  itemId: number;
  queuedPath: string;
  message: string;
}

/** Returned when the host refuses to queue at all (game not running, no hook). */
export interface TransferRefused {
  collected: false;
  message: string;
  queuedPath: null;
}

async function json<T>(response: Response): Promise<T> {
  if (!response.ok && response.status !== 408 && response.status !== 409) {
    throw new Error(`${response.status} ${response.statusText}`);
  }
  return (await response.json()) as T;
}

/**
 * Search filters, mirroring upstream's ItemSearchRequest.
 *
 * `rarity` is a *display colour*, not the game's tier — Grim Dawn's Legendary is IA's "Epic".
 * `prefixRarity` is not a rarity at all: it is the minimum number of Rare affixes, so 2 means
 * a double-rare green. Both names are upstream's and are kept so the two stay diffable.
 */
export interface ItemFilters {
  q?: string;
  /**
   * Mod to search within. Empty string is vanilla; undefined means every mod, which the host
   * supports but the game does not — each mod has its own stash.
   */
  mod?: string;
  rarity?: string;
  prefixRarity?: number;
  /** Item classes; several because one UI slot can mean several ("two-handed"). */
  slot?: string[];
  slotInverse?: boolean;
  minLevel?: number;
  maxLevel?: number;
  socketed?: boolean;
  duplicates?: boolean;
  recent?: boolean;
  grantsSkill?: boolean;
  summoner?: boolean;
  retaliation?: boolean;
  /** Scope the stat filters to the item's pet rather than the player. */
  petScope?: boolean;
  /** Grants any pet bonus, without rescoping the other filters. */
  hasPetBonus?: boolean;
  /** Class ids the item grants skill bonuses to, e.g. "class03". */
  mastery?: string[];
  /** Stat-name groups: OR within a group, AND between groups. */
  has?: string[][];
  hardcore?: boolean;
}

/** A mod the player has items from, or whose item database has been parsed. */
export interface ModInfo {
  /** Empty string is vanilla, matching PlayerItem.Mod. */
  name: string;
  items: number;
}

/** One filter checkbox: a label and the stat fields it matches (any of them). */
export interface FilterGroup {
  label: string;
  fields: string[];
}

/**
 * The filter catalogue, served by the host rather than defined here.
 *
 * There is exactly one definition of what "Fire" means, checked against upstream's source by
 * scripts/verify-filter-groups.sh. This file used to carry its own copy, invented from the
 * shape of the stat names, and it was wrong — every field in it was real, none was what
 * upstream searches.
 */
export interface FilterCatalogue {
  damage: FilterGroup[];
  damageOverTime: FilterGroup[];
  resistances: FilterGroup[];
  misc: FilterGroup[];
  /** Masteries this installation defines, e.g. { id: "class03", name: "Occultist" }. */
  classes: { id: string; name: string }[];
}

/** IA's display colours, in the order the game ranks them. */
export const RARITIES = [
  { value: 'Epic', label: 'Legendary', hint: 'the game calls these Legendary' },
  { value: 'Blue', label: 'Epic', hint: 'the game calls these Epic' },
  { value: 'Green', label: 'Rare' },
  { value: 'Yellow', label: 'Magical' },
  { value: 'White', label: 'Common' },
];

function filterParams(filters: ItemFilters): URLSearchParams {
  const params = new URLSearchParams();
  const set = (key: string, value: unknown) => {
    if (value === undefined || value === null || value === '' || value === false) return;
    params.set(key, value === true ? '1' : String(value));
  };

  set('q', filters.q?.trim());
  // Sent even when empty, because "" means vanilla specifically and omitting it means "any mod".
  if (filters.mod !== undefined) params.set('mod', filters.mod);
  set('rarity', filters.rarity);
  set('prefixRarity', filters.prefixRarity);
  set('slotInverse', filters.slotInverse);
  set('minLevel', filters.minLevel);
  set('maxLevel', filters.maxLevel);
  set('socketed', filters.socketed);
  set('duplicates', filters.duplicates);
  set('recent', filters.recent);
  set('grantsSkill', filters.grantsSkill);
  set('summoner', filters.summoner);
  set('retaliation', filters.retaliation);
  set('petScope', filters.petScope);
  set('hasPetBonus', filters.hasPetBonus);
  if (filters.hardcore !== undefined) params.set('hardcore', filters.hardcore ? '1' : '0');

  // Repeatable parameters: the host reads each occurrence separately.
  for (const slot of filters.slot ?? []) params.append('slot', slot);
  for (const mastery of filters.mastery ?? []) params.append('mastery', mastery);
  for (const group of filters.has ?? []) params.append('has', group.join(','));

  return params;
}

export interface MergePreview {
  considered: number;
  imported: number;
  duplicates: number;
  rejected: number;
  dryRun: boolean;
  /** Filename of the safety copy taken before a real merge. */
  backup: string | null;
  /** Items whose values were rolled by the pass that follows a merge, or null if it did not run. */
  statsComputed: number | null;
  /** Why that pass did not run, or how it failed. */
  statsNote: string | null;
}

/** How far a running merge has got. Total is known before the first row is read. */
export interface MergeProgressEvent {
  done: number;
  total: number;
  imported: number;
  /** 'merge' while rows are read, 'stats' during the pass that follows. */
  stage: 'merge' | 'stats';
  /** What the stats pass is doing; absent while merging. */
  message: string | null;
}

export interface CollectionEntry {
  baseRecord: string;
  name: string | null;
  icon: string | null;
  quality: string;
  numOwnedSc: number;
  numOwnedHc: number;
}

export interface SetMember {
  baseRecord: string;
  name: string | null;
  icon: string | null;
  owned: boolean;
}

export interface SetEntry {
  setRecord: string;
  name: string;
  items: SetMember[];
  ownedCount: number;
  totalCount: number;
}

export interface Settings {
  stashToLootFrom: number;
  stashToDepositTo: number;
  language: string;
  gameDir: string | null;
  transferAnyMod: boolean;
  /** Attach the hook automatically when Grim Dawn is detected. */
  autoAttach: boolean;
  /** A chosen collection database, or null for the default location. */
  databaseFile: string | null;
  /** The database actually open, whichever way it was chosen. */
  databaseInUse: string;
  /** Language codes this installation ships archives for; the only ones worth offering. */
  availableLanguages: string[];
  /** The directory actually in use, whether configured or discovered. */
  resolvedGameDir: string | null;
  /**
   * What the hook will actually read, which is not necessarily what is saved: the bridge file
   * lives inside the Wine prefix and can be replaced by Steam or by the Windows tool. Null when
   * no prefix was found.
   */
  hook: {
    wineModeEnabled: boolean;
    stashToLootFrom: number;
    stashToDepositTo: number;
  } | null;
}

/**
 * Names for the language codes Grim Dawn uses. Only a label lookup — which languages are
 * *offered* comes from the install (Settings.availableLanguages), because the set varies by
 * release and by what the user downloaded.
 */
export const LANGUAGE_NAMES: Record<string, string> = {
  EN: 'English',
  DE: 'Deutsch',
  FR: 'Français',
  ES: 'Español',
  IT: 'Italiano',
  PL: 'Polski',
  RU: 'Русский',
  PT: 'Português',
  CS: 'Čeština',
  VI: 'Tiếng Việt',
  ZH: '中文',
  KO: '한국어',
  JA: '日本語',
};

export const api = {
  status: () => fetch('/api/status').then(json<HostStatus>),

  settings: () => fetch('/api/settings').then(json<Settings>),

  filters: () => fetch('/api/filters').then(json<FilterCatalogue>),

  /** Whether a native file chooser exists — false in a browser, where a page cannot pick a
   *  path on the host. */
  canBrowse: () => fetch('/api/browse').then(json<{ available: boolean }>).then((r) => r.available),

  /** Opens a native chooser and resolves to the chosen path, or null if cancelled. */
  browse: (options: { directory?: boolean; title?: string; path?: string }) =>
    fetch('/api/browse', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(options),
    }).then(json<{ path: string | null }>).then((r) => r.path),

  mods: () => fetch('/api/mods').then(json<ModInfo[]>),

  saveSettings: (settings: Partial<Settings>) =>
    fetch('/api/settings', {
      method: 'PUT',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(settings),
    }).then(json<{ settings: Settings; warning: string | null; message: string }>),

  items: (filters: ItemFilters, skip: number, take: number) => {
    const params = filterParams(filters);
    params.set('skip', String(skip));
    params.set('take', String(take));
    return fetch(`/api/items?${params}`).then(json<ItemPage>);
  },

  /** Every legendary and epic the game defines, against what is owned. */
  collection: (filters: ItemFilters) =>
    fetch(`/api/collection?${filterParams(filters)}`).then(json<CollectionEntry[]>),

  /**
   * Merges another collection in. Call with dryRun first — it reports what would happen without
   * writing, which is the only way to see the duplicate count before committing to it.
   */
  merge: (path: string, dryRun: boolean) =>
    fetch('/api/merge', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ path, dryRun }),
    }).then(json<MergePreview & { error?: string }>),

  sets: (query?: string) => {
    const params = new URLSearchParams();
    if (query?.trim()) params.set('q', query.trim());
    return fetch(`/api/sets?${params}`).then(json<SetEntry[]>);
  },

  item: (id: number) => fetch(`/api/items/${id}`).then(json<ItemDetail>),

  /**
   * Queues an item to go back into the game. Returns as soon as the file is written — the
   * hook only deposits while the player has the transfer stash open, so waiting for it here
   * would block for minutes and be lost on reload. The result arrives as a
   * `transferCompleted` event.
   *
   * Resolves to TransferQueued on success (202), or TransferRefused when the host declines.
   */
  transfer: async (id: number, target?: TransferTarget, timeoutSeconds = 300)
      : Promise<TransferQueued | TransferRefused> => {
    const response = await fetch(`/api/items/${id}/transfer`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({
        timeoutSeconds,
        keep: false,
        targetMod: target?.mod,
        targetHardcore: target?.hardcore,
      }),
    });
    return response.json();
  },

  cancelTransfer: (transferId: string) =>
    fetch(`/api/transfers/${transferId}`, { method: 'DELETE' })
      .then(json<{ cancelled: boolean; message: string }>),

  iconUrl: (icon: string | null) => (icon ? `/api/icons/${encodeURIComponent(icon)}` : null),
};

export type HostEvent =
  | { type: 'itemLooted'; data: ItemSummary }
  | { type: 'itemRemoved'; data: { id: number } }
  | { type: 'status'; data: HostStatus }
  | { type: 'message'; data: { text: string; level: string } }
  | { type: 'mergeProgress'; data: MergeProgressEvent }
  | { type: 'transferQueued'; data: { transferId: string; itemId: number } }
  | {
      type: 'transferCompleted';
      data: { transferId: string; itemId: number; collected: boolean; message: string };
    };

/**
 * Subscribes to host pushes, reconnecting if the host restarts. Returns a disposer.
 */
export function subscribe(onEvent: (event: HostEvent) => void): () => void {
  let socket: WebSocket | null = null;
  let closed = false;
  let retry: number | undefined;

  const connect = () => {
    if (closed) return;

    const protocol = location.protocol === 'https:' ? 'wss' : 'ws';
    socket = new WebSocket(`${protocol}://${location.host}/ws`);

    socket.onmessage = (event) => {
      try {
        onEvent(JSON.parse(event.data) as HostEvent);
      } catch {
        // A malformed frame should not take the stream down.
      }
    };

    socket.onclose = () => {
      if (!closed) retry = window.setTimeout(connect, 2000);
    };
    socket.onerror = () => socket?.close();
  };

  connect();

  return () => {
    closed = true;
    if (retry) clearTimeout(retry);
    socket?.close();
  };
}
