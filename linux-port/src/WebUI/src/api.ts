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

/**
 * One tooltip line, in whichever of upstream's two shapes it has.
 *
 * A line Grim Dawn drew carries the row type the game gave it, and upstream colours it from that
 * type alone (ReplicaStat.css). A computed line has no such type: upstream renders those through
 * ItemStat.tsx, splitting each into a leading value and the rest, coloured differently per list.
 */
export interface ItemStatLine {
  textClass: number;
  text: string;
  /** 'header', 'body' or 'pet' on a computed line; null on a captured one. */
  section: string | null;
  /** The leading value, e.g. "+162%". Null on a captured line. */
  modifier: string | null;
  label: string | null;
  /** A skill the line modifies, drawn apart from the label in its own colour. */
  skill: string | null;
  /** That skill's tooltip, e.g. "Tier 3 Occultist". */
  extras: string | null;
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
  items: ItemCard[];
  /** Cards matching, which is what paging walks. */
  total: number;
  /** Items matching — the larger number, since identical items share a card. */
  totalItems: number;
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
  /** The hook's directory, or null when no Proton prefix could be resolved. */
  bridgeDir: string | null;
  databaseFile: string;
  /** Items the precompute pass has not seen; rarity and level filters cannot match them. */
  itemsNeedingStats: number;
  /** Why the parsed game data is out of date, or null when current. */
  gameDataStale: string | null;
  /** True while Grim Dawn's data is being read. */
  parsingGameData: boolean;
  /** What that parse is doing, for the status line. */
  parseStep: string | null;
  /**
   * True while the collection is being analysed. Distinct from itemsNeedingStats, which counts
   * items waiting: a re-parse, or a change to what the pass writes, invalidates the rows for a
   * collection whose items are all described, and that pass has to be visible too.
   */
  analysing: boolean;
  analysisStep: string | null;
  /** True while the host is attaching the hook to the running game. */
  attaching: boolean;
  /**
   * Why this installation cannot capture loot at all — no Steam, no Proton prefix, or a
   * configured prefix that is not one. Null when there is nothing wrong.
   */
  setupWarning: string | null;
  /** Why the hook's own settings file could not be written. Null when it could. */
  hookWarning: string | null;
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
 * Like `json`, but keeps the body on a refusal instead of throwing.
 *
 * For endpoints whose failures are things the user did — no backup stored for that character,
 * already signed in — where the host answers with a status *and* an `error` the panel should
 * show. Throwing there loses the only sentence worth reading and leaves the UI to invent a
 * vaguer one.
 */
async function jsonOrError<T extends { error?: string }>(response: Response): Promise<T> {
  try {
    return (await response.json()) as T;
  } catch {
    return { error: `${response.status} ${response.statusText}` } as T;
  }
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
  /**
   * Numeric comparisons on the summed value of a stat group, as "fieldA+fieldB>=30".
   * Upstream attaches one of these to a checked stat checkbox through its funnel button, and
   * offers >=, >, <=, < and =.
   */
  stat?: string[];
  hardcore?: boolean;
  /** Upstream's "Order By Level" checkbox: level first, then name. */
  orderByLevel?: boolean;
  /** Newest first, by when the item was added. This port's own; upstream orders by name. */
  orderByNewest?: boolean;
}

/**
 * One branch a search can be scoped to. Upstream's mod dropdown lists these and always has one
 * selected: the game keeps a separate transfer stash per mod and per hardcore branch, and no
 * item crosses between them.
 */
export interface ModInfo {
  /** Empty string is vanilla, matching PlayerItem.Mod. */
  name: string;
  hardcore: boolean;
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
  /** The slot dropdown, straight from upstream's UIHelper. */
  slots: SlotOption[];
  /** The rarity dropdown. Labels are upstream's colour names, not the game's. */
  rarities: RarityOption[];
}

export interface SlotOption {
  tag: string;
  label: string;
  itemClasses: string[];
  /** True only for "Other", which matches everything the named slots do not. */
  inverse: boolean;
}

export interface RarityOption {
  tag: string;
  label: string;
  rarity: string | null;
  /** Rare-affix count, not a rarity: three entries share "Green" and differ only here. */
  prefixRarity: number;
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
  set('orderByLevel', filters.orderByLevel);
  set('orderByNewest', filters.orderByNewest);
  if (filters.hardcore !== undefined) params.set('hardcore', filters.hardcore ? '1' : '0');

  // Repeatable parameters: the host reads each occurrence separately.
  for (const slot of filters.slot ?? []) params.append('slot', slot);
  for (const mastery of filters.mastery ?? []) params.append('mastery', mastery);
  for (const group of filters.has ?? []) params.append('has', group.join(','));
  for (const comparison of filters.stat ?? []) params.append('stat', comparison);

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

/**
 * One card in the item list.
 *
 * Identical items arrive as one card: the host groups them by base record plus prefix plus
 * suffix, as upstream's MergeStackSize does, and `copies` is what its "Transfer all (N)" counts.
 */
export interface ItemCard {
  item: ItemSummary;
  stats: ItemStatLine[];
  skill: ItemSkillInfo | null;
  copies: number;
  duplicates: number[];
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

/**
 * One component, and what it does.
 *
 * Upstream has no components page: its nav entry opens the author's website. This one is built
 * from Grim Dawn's own data, which the client already reads.
 */
export interface ComponentEntry {
  baseRecord: string;
  name: string | null;
  icon: string | null;
  levelRequirement: number;
  /** What the record says it can be socketed into: 'chest', 'sword2h', … */
  slots: string[];
  skill: ItemSkillInfo | null;
  stats: ItemStatLine[];
  /** How many the player has socketed into something. */
  numOwned: number;
}

export interface Settings {
  stashToLootFrom: number;
  stashToDepositTo: number;
  language: string;
  gameDir: string | null;
  /** A Proton prefix set by hand, or null to let discovery find it. */
  prefixDir: string | null;
  transferAnyMod: boolean;
  /** Attach the hook automatically when Grim Dawn is detected. */
  autoAttach: boolean;
  /** Leave an item's granted-skill block off its card and detail panel. */
  hideSkills: boolean;
  /** Fade a notification on its own rather than leaving it until dismissed. */
  autoDismissNotifications: boolean;
  /** Wait 200ms after a keystroke before searching, rather than searching immediately. */
  preferDelayedSearch: boolean;
  /** A chosen collection database, or null for the default location. */
  databaseFile: string | null;
  /** The database actually open, whichever way it was chosen. */
  databaseInUse: string;
  /** Language codes this installation ships archives for; the only ones worth offering. */
  availableLanguages: string[];
  /** The directory actually in use, whether configured or discovered. */
  resolvedGameDir: string | null;
  /** The prefix the hook is actually reached through, or null when there is none. */
  resolvedPrefixDir: string | null;
  /**
   * What the hook will actually read, which is not necessarily what is saved: the bridge file
   * lives inside the Wine prefix and can be replaced by Steam or by the Windows tool. Null when
   * no prefix was found.
   */
  hook: {
    wineModeEnabled: boolean;
    stashToLootFrom: number;
    stashToDepositTo: number;
    /**
     * Whether the hook believes Grim Dawn's data has been read. It refuses to loot anything
     * until this is true, saying so only as a message over the game.
     */
    gameDataParsed: boolean;
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

/**
 * Online backup, as the panel sees it.
 *
 * `state` has three values, not two. "unknown" means the service could not be reached — which is
 * not the same as being logged out, and must not be drawn as one: offering a login button for a
 * network problem invites someone to re-authenticate over something that will pass by itself.
 */
export interface CloudStatus {
  state: 'authorized' | 'unauthorized' | 'unknown';
  user: string | null;
  /** This account's own six-digit id, to hand to a friend. */
  buddyId: number | null;
  usingDualComputer: boolean;
  optOutOfBackups: boolean;
  liveSyncConnected: boolean;
  /** Set while a login is waiting for the browser round trip to finish. */
  pendingLoginUrl: string | null;
  pendingUploads: number;
  pendingDeletions: number;
  /** "cloud" or "localdev". Shown so a development build cannot be mistaken for a real one. */
  environment: string | null;
  host: string | null;
}

export interface Buddy {
  id: number;
  nickname: string | null;
  /** Hidden buddies keep their items but drop out of search results. */
  isHidden: boolean;
  items: number;
  lastSync: number;
}

export interface BackedUpCharacter {
  name: string;
  createdAt: string;
  updatedAt: string;
}

/**
 * The state of character backup itself, not of any one character.
 *
 * Separate from the list because an empty list has several very different causes — no Proton
 * prefix, the game is open, nothing has changed since the last run — and showing the same blank
 * space for all of them is what makes a working feature look broken.
 */
export interface CharacterBackupState {
  /** False when no Grim Dawn save folder was found, so there is nothing to back up. */
  available: boolean;
  running: boolean;
  /** Passes are suspended: a save the game is writing cannot be archived safely. */
  pausedForGame: boolean;
  lastRunUtc: string | null;
  message: string | null;
  /** Names that did not upload. They are retried automatically. */
  failed: string[] | null;
}

export interface CharacterBackups {
  characters: BackedUpCharacter[];
  backup: CharacterBackupState;
}

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

  /** Upstream's "Load Database": read Grim Dawn's data again. */
  parse: (gameDir?: string) =>
    fetch('/api/parse', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ gameDir }),
    }).then(json<{ started?: boolean; gameDir?: string; error?: string }>),

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

  components: (query?: string) => {
    const params = new URLSearchParams();
    if (query?.trim()) params.set('q', query.trim());
    return fetch(`/api/components?${params}`).then(json<ComponentEntry[]>);
  },

  /**
   * Asks the host to open one of the Support page's links in the user's browser.
   *
   * The host allowlists them: the app window is a WebKitGTK view, so this is the only way out
   * of it, and an endpoint that opens arbitrary URLs on the user's desktop would be reachable
   * by any page their browser has open while the client is running.
   */
  open: (url: string) =>
    fetch('/api/open', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ url }),
    }).then(json<{ opened: boolean; error?: string }>),

  item: (id: number) => fetch(`/api/items/${id}`).then(json<ItemDetail>),

  /**
   * Several items at once, each with its own tooltip — what the comparison view shows.
   *
   * A card stands for every identical copy, and identical there means the records the items are
   * made of, not the values they rolled. Upstream's search result carries every copy already;
   * this port sends one card per group so a page is not a thousand tooltips, and fetches the
   * copies when the player asks to compare them.
   *
   * Ids that no longer exist come back missing rather than as an error, so the result can be
   * shorter than the request.
   */
  details: (ids: number[]) =>
    ids.length === 0
      ? Promise.resolve([] as ItemDetail[])
      : fetch(`/api/items/details?ids=${ids.join(',')}`).then(json<ItemDetail[]>),

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

  // --- Online backup. Every one of these is inert until the user logs in.

  cloud: () => fetch('/api/cloud').then(json<CloudStatus>),

  /** Starts a login and returns the address to open. The rest happens in the browser. */
  cloudLogin: () =>
    fetch('/api/cloud/login', { method: 'POST' })
      .then(jsonOrError<{ loginUrl?: string; error?: string }>),

  cloudLogout: () =>
    fetch('/api/cloud/logout', { method: 'POST' }).then(json<{ message: string }>),

  /** Irreversible on the server. The UI asks twice. */
  cloudDeleteAccount: () =>
    fetch('/api/cloud/account', { method: 'DELETE' })
      .then(jsonOrError<{ message?: string; error?: string }>),

  cloudSettings: (settings: { usingDualComputer?: boolean; optOutOfBackups?: boolean }) =>
    fetch('/api/cloud/settings', {
      method: 'PUT',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(settings),
    }).then(json<CloudStatus>),

  buddies: () => fetch('/api/cloud/buddies').then(json<Buddy[]>),

  addBuddy: (id: number, nickname: string) =>
    fetch('/api/cloud/buddies', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ id, nickname }),
    }).then(jsonOrError<{ added?: number; error?: string }>),

  updateBuddy: (id: number, changes: { nickname?: string; isHidden?: boolean }) =>
    fetch(`/api/cloud/buddies/${id}`, {
      method: 'PUT',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(changes),
    }).then(json<{ updated?: number; error?: string }>),

  removeBuddy: (id: number) =>
    fetch(`/api/cloud/buddies/${id}`, { method: 'DELETE' })
      .then(json<{ removed: number }>),

  characters: () => fetch('/api/cloud/characters').then(json<CharacterBackups>),

  /** Runs a backup pass now. Returns at once; watch `characters()` for the outcome. */
  backupCharactersNow: () =>
    fetch('/api/cloud/characters/backup', { method: 'POST' })
      .then(jsonOrError<{ started?: boolean; error?: string }>),

  /**
   * A short-lived link to one character's backup archive, which the host also hands to the
   * desktop browser — `opened` says whether that worked. It cannot when the host is headless,
   * and then the page is being viewed in a real browser anyway and can offer the link itself.
   */
  characterUrl: (name: string) =>
    fetch(`/api/cloud/characters/${encodeURIComponent(name)}`)
      .then(jsonOrError<{ url?: string; opened?: boolean; error?: string }>),

  iconUrl: (icon: string | null) => (icon ? `/api/icons/${encodeURIComponent(icon)}` : null),
};

export type HostEvent =
  | { type: 'itemLooted'; data: ItemCard }
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
