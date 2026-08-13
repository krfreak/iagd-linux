import { render } from 'preact';
import { useEffect, useState, useCallback, useRef } from 'preact/hooks';
import {
  api, subscribe, ItemSummary, ItemCard as ItemCardData, ItemDetail, HostStatus, HostEvent,
  ItemFilters, RARITIES, LANGUAGE_NAMES,
  CollectionEntry, SetEntry, Settings, FilterCatalogue, FilterGroup, ModInfo, TransferTarget,
  MergePreview, MergeProgressEvent,
} from './api';
import { GrimText, ReplicaLine, stripGrimText } from './GrimText';
import './style.css';

const PAGE_SIZE = 60;

/**
 * The window's own tabs, which upstream draws in WinForms around its embedded browser.
 *
 * "Grim Dawn" is upstream's name for the tab holding the game installation and its mod
 * databases; "Online" is cloud backup and buddy sharing, which this port does not implement yet
 * and which says so rather than being absent.
 */
type Tab = 'items' | 'online' | 'settings' | 'grimdawn';

/** The tabs inside the item view, which upstream draws in the web page itself. */
type View = 'items' | 'collections' | 'sets' | 'help';

function StatusBar({ status }: { status: HostStatus | null }) {
  if (!status) return <div class="status status--warn">Connecting to iagd-host…</div>;

  // A Grim Dawn patch, or a language change, invalidates every name, level and icon — and
  // nothing about that fails, so it has to be said out loud.
  if (status.gameDataStale) {
    return (
      <div class="status status--warn">
        <span class="dot dot--warn" /> {status.gameDataStale}
        <span class="status__detail">run <code>iagd parse</code></span>
      </div>
    );
  }

  // Rarity and level filters read columns the precompute pass fills in. Say so, because the
  // failure mode is "the filter returns nothing", which looks like a bug rather than a
  // missing step.
  if (status.itemsNeedingStats > 0) {
    return (
      <div class="status status--warn">
        <span class="dot dot--warn" /> {status.itemsNeedingStats} item(s) not analysed
        <span class="status__detail">
          run <code>iagd stats</code> — rarity and level filters need it
        </span>
      </div>
    );
  }

  // The two states worth surfacing are the ones that silently stop loot arriving.
  if (!status.gameRunning) {
    return (
      <div class="status">
        <span class="dot dot--idle" /> Grim Dawn is not running
        <span class="status__detail">{status.itemCount} items in collection</span>
      </div>
    );
  }
  if (!status.hookAttached) {
    // Attaching is the normal path now, so say what is happening rather than telling the user
    // to go and run a script.
    return (
      <div class="status status--warn">
        <span class="dot dot--warn" /> {status.attaching
          ? 'Game running — attaching the hook…'
          : 'Game running, but no hook attached'}
        <span class="status__detail">
          {status.attaching
            ? 'this can take a moment while the game loads'
            : 'retrying automatically; load a character if you are at the menu'}
        </span>
      </div>
    );
  }
  return (
    <div class="status status--ok">
      <span class="dot dot--ok" /> Hooked — looting live
      <span class="status__detail">{status.itemCount} items in collection</span>
    </div>
  );
}

/**
 * One item, rendered the way upstream renders it: icon, name in its rarity colour, the tooltip
 * the game itself produced, then the level requirement and the transfer links along the bottom.
 *
 * The stat text comes from the hook, which captures what Grim Dawn drew. An item that arrived
 * any other way — merged in, imported from GD Stash — has no captured tooltip, and shows its
 * name, rarity and level with the body left empty until the game renders it once.
 */
/**
 * Tooltip rows upstream does not draw: 3, 4, 5, 6, 64 and 77 are the name and slot lines its
 * header already shows, and 35 is "[Release Ctrl to Hide Details]", which is a prompt to the
 * player standing in front of the game rather than a property of the item.
 */
const HIDDEN_STAT_TYPES = new Set([3, 4, 5, 6, 35, 64, 77]);

function ItemCard({ card, selected, onSelect, onTransfer, transferring }: {
  card: ItemCardData;
  selected: boolean;
  onSelect: () => void;
  onTransfer: (all: boolean) => void;
  transferring: boolean;
}) {
  const { item, stats, skill, copies } = card;
  const icon = api.iconUrl(item.icon);
  const rarity = (item.rarity ?? 'unknown').toLowerCase();

  // Rows the tooltip repeats from the header, and the game's own "[Release Ctrl to Hide
  // Details]" prompt. Upstream hides exactly these (ReplicaStat.css) rather than showing a
  // card that says the item's name three times.
  const body = stats.filter((s) => !HIDDEN_STAT_TYPES.has(s.textClass));

  return (
    <article
      class={`item item--${rarity} ${selected ? 'item--selected' : ''}`}
      onClick={onSelect}
    >
      <div class="item__icon">
        {icon ? <img src={icon} alt="" loading="lazy" /> : <div class="item__icon--missing" />}
      </div>

      <div class="item__text">
        <div class={`item__name item__name--${rarity}`}>
          {/* Upstream strips the game's colour codes from the name and colours the whole of it
              by rarity, so a Mythical reads as one purple name rather than two colours. */}
          {stripGrimText(item.name)}
          {/* Upstream calls a green with two rare affixes a DoubleRare and it is the thing
              worth spotting in a list; one rare affix is unremarkable but still marked. */}
          {item.prefixRarity === 2 && <span class="item__rare"> (DoubleRare)</span>}
          {item.prefixRarity === 1 && <span class="item__rare"> (Rare)</span>}
        </div>

        {item.stackCount > 1 && <div class="item__stack">×{item.stackCount}</div>}

        <ul class="item__stats">
          {body.map((stat, index) => (
            <li key={index} class={`stat stat--class-${stat.textClass}`}>
              <ReplicaLine text={stat.text} />
            </li>
          ))}
        </ul>

        {skill && (
          <div class="item__skill">
            <div class="item__skill-name">{skill.name ?? 'Granted Skill'}</div>
            {skill.description && <div class="item__skill-text">{skill.description}</div>}
          </div>
        )}
      </div>

      {/* One row, so the links and the level cannot land on top of each other on a card
          narrow enough that both want the same space. */}
      <footer class="item__footer">
        <span class="item__links">
          {copies > 1 && (
            <a onClick={(e) => { e.stopPropagation(); onTransfer(true); }}>
              Transfer all ({copies})
            </a>
          )}
          <a onClick={(e) => { e.stopPropagation(); onTransfer(false); }}>
            {transferring ? 'Transferring…' : copies > 1 ? 'Compare & Transfer' : 'Transfer to Stash'}
          </a>
        </span>
        <span class="item__level">
          {item.level > 0 ? `Level Requirement: ${item.level}` : 'Level Requirement: Any'}
        </span>
      </footer>
    </article>
  );
}

/**
 * The filter sidebar, down the left of the item view.
 *
 * Upstream's five collapsible panels — Damage, Damage over Time, Misc, Resistances, Classes —
 * each a column of checkboxes. The contents come from the host so there is one definition of
 * what "Fire" means; see api.ts FilterCatalogue.
 */
function FilterSidebar({ filters, onChange, catalogue }: {
  filters: ItemFilters;
  onChange: (next: ItemFilters) => void;
  catalogue: FilterCatalogue | null;
}) {
  // Damage open, the rest closed: that is how upstream starts, and five open panels would be
  // longer than any window.
  const [open, setOpen] = useState<Record<string, boolean>>({ damage: true });
  const patch = (next: Partial<ItemFilters>) => onChange({ ...filters, ...next });

  // A stat group is on when its exact field set is already among the active groups. Matching on
  // the first field alone would confuse two groups that share one — "Attack speed" and "Run
  // speed" both carry characterTotalSpeedModifier.
  const key = (fields: string[]) => fields.join(',');
  const activeGroups = new Set((filters.has ?? []).map(key));

  const statBox = (group: FilterGroup) => {
    const on = activeGroups.has(key(group.fields));
    return (
      <label key={group.label} class="check" title={group.fields.join(', ')}>
        <input
          type="checkbox"
          checked={on}
          onChange={(e) => {
            const checked = (e.target as HTMLInputElement).checked;
            const rest = (filters.has ?? []).filter((g) => key(g) !== key(group.fields));
            patch({ has: checked ? [...rest, group.fields] : rest.length ? rest : undefined });
          }}
        />
        <span>{group.label}</span>
      </label>
    );
  };

  const toggleBox = (field: keyof ItemFilters, label: string, title?: string) => (
    <label key={field} class="check" title={title}>
      <input
        type="checkbox"
        checked={Boolean(filters[field])}
        onChange={(e) => patch({ [field]: (e.target as HTMLInputElement).checked })}
      />
      <span>{label}</span>
    </label>
  );

  const panel = (id: string, label: string, children: preact.ComponentChildren) => (
    <section class={`panel-group ${open[id] ? 'panel-group--open' : ''}`}>
      <button class="panel-group__head" onClick={() => setOpen((o) => ({ ...o, [id]: !o[id] }))}>
        <span>{label}</span>
        <span class="panel-group__chevron">{open[id] ? '\u2303' : '\u2304'}</span>
      </button>
      {open[id] && <div class="panel-group__body">{children}</div>}
    </section>
  );

  if (!catalogue) return <aside class="sidebar" />;

  return (
    <aside class="sidebar">
      {panel('damage', 'Damage', catalogue.damage.map(statBox))}
      {panel('dot', 'Damage over Time', catalogue.damageOverTime.map(statBox))}
      {panel('misc', 'Misc', (
        <>
          {catalogue.misc.map(statBox)}
          {/* Upstream keeps these in the same panel as the stat checkboxes above. */}
          {toggleBox('grantsSkill', 'Grants Skill')}
          {toggleBox('summoner', 'Grants Summon Skill')}
          {toggleBox('hasPetBonus', 'Has Pet Bonus')}
          {toggleBox('petScope', 'Pet Bonuses', 'Match the stat filters against the pet, not the player')}
          {toggleBox('retaliation', 'Retaliation')}
          {toggleBox('socketed', 'With components')}
          {toggleBox('duplicates', 'Duplicates Only')}
          {toggleBox('recent', 'Recent Only')}
        </>
      ))}
      {panel('resist', 'Resistances', catalogue.resistances.map(statBox))}
      {panel('classes', 'Classes', catalogue.classes.map((c) => {
        const on = (filters.mastery ?? []).includes(c.id);
        return (
          <label key={c.id} class="check">
            <input
              type="checkbox"
              checked={on}
              onChange={(e) => {
                const checked = (e.target as HTMLInputElement).checked;
                const rest = (filters.mastery ?? []).filter((m) => m !== c.id);
                patch({ mastery: checked ? [...rest, c.id] : rest.length ? rest : undefined });
              }}
            />
            <span>{c.name}</span>
          </label>
        );
      }))}
    </aside>
  );
}

/**
 * The row above the item list: search, ordering, the two dropdowns and the level range.
 *
 * Upstream draws this in WinForms above its browser control. The dropdown contents are the
 * host's copy of its UIHelper tables, checked by scripts/verify-slot-filters.sh.
 */
function Toolbar({ filters, onChange, catalogue, mods, query, onQuery, searchRef }: {
  filters: ItemFilters;
  onChange: (next: ItemFilters) => void;
  catalogue: FilterCatalogue | null;
  mods: ModInfo[];
  query: string;
  onQuery: (value: string) => void;
  searchRef: preact.RefObject<HTMLInputElement>;
}) {
  const patch = (next: Partial<ItemFilters>) => onChange({ ...filters, ...next });

  // The selected rarity is a (colour, rare-affix count) pair, since three entries share "Green".
  const rarityValue = `${filters.rarity ?? ''}/${filters.prefixRarity ?? 0}`;
  const slotValue = (filters.slot ?? []).join(',');

  return (
    <div class="toolbar">
      <input
        ref={searchRef}
        class="toolbar__search"
        type="search"
        value={query}
        onInput={(e) => onQuery((e.target as HTMLInputElement).value)}
      />

      <label class="toolbar__check">
        <input
          type="checkbox"
          checked={Boolean(filters.orderByLevel)}
          onChange={(e) => patch({ orderByLevel: (e.target as HTMLInputElement).checked })}
        />
        <span>Order By Level</span>
      </label>

      <select
        class="toolbar__select"
        value={rarityValue}
        onChange={(e) => {
          const [rarity, prefix] = (e.target as HTMLSelectElement).value.split('/');
          patch({ rarity: rarity || undefined, prefixRarity: Number(prefix) || undefined });
        }}
      >
        {(catalogue?.rarities ?? []).map((r) => (
          <option key={r.tag} value={`${r.rarity ?? ''}/${r.prefixRarity}`}>{r.label}</option>
        ))}
      </select>

      <select
        class="toolbar__select"
        value={slotValue}
        onChange={(e) => {
          const value = (e.target as HTMLSelectElement).value;
          const option = (catalogue?.slots ?? []).find((s) => s.itemClasses.join(',') === value);
          patch({
            slot: value ? value.split(',') : undefined,
            slotInverse: option?.inverse || undefined,
          });
        }}
      >
        {(catalogue?.slots ?? []).map((s) => (
          <option key={s.tag} value={s.itemClasses.join(',')}>{s.label}</option>
        ))}
      </select>

      <select
        class="toolbar__select"
        value={filters.mod ?? ''}
        onChange={(e) => patch({ mod: (e.target as HTMLSelectElement).value })}
        title="Items are partitioned by mod; each has its own stash in game"
      >
        <option value="">No mod</option>
        {mods.filter((m) => m.name).map((m) => (
          <option key={m.name} value={m.name}>{m.name}</option>
        ))}
      </select>

      <fieldset class="toolbar__level">
        <legend>Level</legend>
        <input
          type="text"
          inputMode="numeric"
          value={filters.minLevel ?? ''}
          onInput={(e) => patch({ minLevel: Number((e.target as HTMLInputElement).value) || undefined })}
        />
        <input
          type="text"
          inputMode="numeric"
          value={filters.maxLevel ?? ''}
          onInput={(e) => patch({ maxLevel: Number((e.target as HTMLInputElement).value) || undefined })}
        />
      </fieldset>
    </div>
  );
}

/**
 * Settings.
 *
 * The stash-tab fields are shown alongside what the hook will *actually* read. Those can
 * disagree — the bridge file lives inside the Wine prefix, and Steam rebuilding it or the
 * Windows tool rewriting it both silently revert the setting. The symptom is loot quietly not
 * being captured, so the disagreement is worth showing rather than hiding.
 */
function SettingsView({ onSaved, progress }: {
  onSaved: (message: string) => void;
  progress: MergeProgressEvent | null;
}) {
  const [settings, setSettings] = useState<Settings | null>(null);
  const [saving, setSaving] = useState(false);
  const [canBrowse, setCanBrowse] = useState(false);

  useEffect(() => {
    api.settings().then(setSettings).catch(() => setSettings(null));
    api.canBrowse().then(setCanBrowse).catch(() => setCanBrowse(false));
  }, []);

  if (!settings) return <div class="grid__empty">Loading settings…</div>;

  const save = async (next: Partial<Settings>) => {
    setSaving(true);
    try {
      const result = await api.saveSettings({ ...settings, ...next });
      setSettings(result.settings);
      onSaved(result.warning ?? result.message);
    } finally {
      setSaving(false);
    }
  };

  const stashHint = (value: number) => (value === 0 ? 'last tab' : `tab ${value}`);
  const hookDisagrees = settings.hook !== null && (
    settings.hook.stashToLootFrom !== settings.stashToLootFrom ||
    settings.hook.stashToDepositTo !== settings.stashToDepositTo);

  return (
    <section class="settings">
      {settings.hook !== null && !settings.hook.wineModeEnabled && (
        <div class="settings__alert">
          <strong>The hook is not in Wine mode.</strong> It will not capture anything. This is
          normally repaired automatically on startup — if it persists, the bridge settings file
          is not writable.
        </div>
      )}
      {hookDisagrees && (
        <div class="settings__alert settings__alert--warn">
          The hook is currently using tab {settings.hook!.stashToLootFrom} to loot from and
          tab {settings.hook!.stashToDepositTo} to deposit to. Saving again will re-apply these.
        </div>
      )}

      <div class="settings__group">
        <h3>Stash</h3>
        <p class="settings__note">
          Which shared-stash tab the hook takes deposited items from, and which it places
          transferred items into. 0 means the last tab.
        </p>

        <label class="settings__row">
          <span>Loot from</span>
          <input
            type="number" min="0" max="12" disabled={saving}
            value={settings.stashToLootFrom}
            onChange={(e) => save({ stashToLootFrom: Number((e.target as HTMLInputElement).value) })}
          />
          <em>{stashHint(settings.stashToLootFrom)}</em>
        </label>

        <label class="settings__row">
          <span>Deposit to</span>
          <input
            type="number" min="0" max="12" disabled={saving}
            value={settings.stashToDepositTo}
            onChange={(e) => save({ stashToDepositTo: Number((e.target as HTMLInputElement).value) })}
          />
          <em>{stashHint(settings.stashToDepositTo)}</em>
        </label>
      </div>

      <div class="settings__group">
        <h3>Language</h3>
        <p class="settings__note">
          Which of Grim Dawn's text archives item names are read from. Changing it needs a
          re-parse — run <code>iagd parse</code> — because names are resolved when the game
          database is read, not when items are shown.
        </p>
        <label class="settings__row">
          <span>Item names</span>
          <select
            class="filters__select" disabled={saving}
            value={settings.language}
            onChange={(e) => save({ language: (e.target as HTMLSelectElement).value })}
          >
            {settings.availableLanguages.map((code) => (
              <option key={code} value={code}>{LANGUAGE_NAMES[code] ?? code}</option>
            ))}
          </select>
        </label>
      </div>

      <MergeCollection canBrowse={canBrowse} progress={progress} />

      <div class="settings__group">
        <h3>Hook</h3>
        <p class="settings__note">
          The hook is what captures looted items. It cannot be attached when the game launches,
          so the host waits for Grim Dawn and attaches once it is ready — retrying while it is
          loading or at character select.
        </p>
        <label class="settings__row settings__row--check">
          <input
            type="checkbox" disabled={saving}
            checked={settings.autoAttach}
            onChange={(e) => save({ autoAttach: (e.target as HTMLInputElement).checked })}
          />
          <span>Attach automatically when Grim Dawn is running</span>
        </label>
      </div>

      <div class="settings__group">
        <h3>Transfers</h3>
        <label class="settings__row settings__row--check">
          <input
            type="checkbox" disabled={saving}
            checked={settings.transferAnyMod}
            onChange={(e) => save({ transferAnyMod: (e.target as HTMLInputElement).checked })}
          />
          <span>Allow transferring items into a different mod than they were looted from</span>
        </label>
      </div>

      <div class="settings__group">
        <h3>Paths</h3>
        <p class="settings__note">
          The collection can live anywhere — including an existing Item Assistant database from a
          Windows install, since this port uses the same schema. A copy is saved before an
          existing collection is opened for the first time. Leave a field empty for the default.
        </p>

        <PathSetting
          label="Collection"
          value={settings.databaseFile ?? ''}
          placeholder={settings.databaseInUse}
          title="Choose a collection database"
          canBrowse={canBrowse}
          disabled={saving}
          onChange={(path) => save({ databaseFile: path.trim() === '' ? null : path })}
        />

        <PathSetting
          label="Grim Dawn"
          value={settings.gameDir ?? ''}
          placeholder={settings.resolvedGameDir ?? 'auto-discovered'}
          directory
          title="Choose the Grim Dawn folder"
          canBrowse={canBrowse}
          disabled={saving}
          onChange={(path) => save({ gameDir: path.trim() === '' ? null : path })}
        />

        <p class="settings__note settings__note--after">
          A changed collection takes effect when the application restarts.
          {!canBrowse && ' Type paths here — a browser cannot open a file chooser on the host.'}
        </p>
      </div>

    </section>
  );
}

/**
 * A path setting: type it, or pick it.
 *
 * The Choose button appears only when the host can show a native dialog — that is true in the
 * desktop window and false in a browser, where a page cannot choose a path on the machine
 * running the host. Typing always works, so the browser case is not a dead end.
 */
function PathSetting({ label, value, placeholder, directory, title, canBrowse, onChange, disabled }: {
  label: string;
  value: string;
  placeholder?: string;
  directory?: boolean;
  title: string;
  canBrowse: boolean;
  onChange: (path: string) => void;
  disabled?: boolean;
}) {
  const [draft, setDraft] = useState(value);
  useEffect(() => setDraft(value), [value]);

  return (
    <div class="settings__path">
      <span>{label}</span>
      <input
        type="text"
        value={draft}
        placeholder={placeholder}
        disabled={disabled}
        onInput={(e) => setDraft((e.target as HTMLInputElement).value)}
        onBlur={() => { if (draft !== value) onChange(draft); }}
        onKeyDown={(e) => { if (e.key === 'Enter') onChange((e.target as HTMLInputElement).value); }}
      />
      {canBrowse && (
        <button
          class="button"
          disabled={disabled}
          onClick={async () => {
            const picked = await api.browse({ directory, title, path: draft || undefined });
            if (picked) { setDraft(picked); onChange(picked); }
          }}
        >
          Choose…
        </button>
      )}
    </div>
  );
}

/**
 * Merging another collection in.
 *
 * Always previews first. The interesting number is how many are duplicates, and that cannot be
 * known without reading both collections — so the flow is choose, look, then commit, rather than
 * a button that silently changes the collection size.
 */
function MergeCollection({ canBrowse, progress }: {
  canBrowse: boolean;
  progress: MergeProgressEvent | null;
}) {
  const [path, setPath] = useState('');
  const [preview, setPreview] = useState<MergePreview | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [done, setDone] = useState<MergePreview | null>(null);

  // Only trust progress that arrived during this run. The frame from a finished merge stays in
  // the App's state, so without this the bar would jump to full the moment the next run starts;
  // holding the frame that was current at the start makes "not yet reported" tell itself apart.
  const before = useRef<MergeProgressEvent | null>(null);
  const live = busy && progress !== before.current ? progress : null;
  // The stats pass reports what it is doing rather than how far along it is, so the bar sweeps
  // for that stage — as it does before the first frame of the merge itself.
  const bar = live && live.stage === 'merge' && live.total > 0
    ? Math.min(1, live.done / live.total)
    : null;
  const label = !live ? 'Reading…'
    : live.stage === 'stats' ? (live.message ?? 'Computing values…')
    : `${live.done.toLocaleString()} of ${live.total.toLocaleString()} item(s)`;

  const run = async (dryRun: boolean) => {
    before.current = progress;
    setBusy(true);
    setError(null);
    try {
      const result = await api.merge(path, dryRun);
      if (result.error) { setError(result.error); setPreview(null); return; }
      if (dryRun) setPreview(result); else { setDone(result); setPreview(null); }
    } catch (e) {
      setError(String(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <div class="settings__group">
      <h3>Merge another collection</h3>
      <p class="settings__note">
        Adds the items from another Item Assistant database to this one, skipping exact
        duplicates. The other collection is only read, never changed, and a copy of this one is
        saved first. Their rolled values, rarity and pet bonuses are computed afterwards, so the
        new items are searchable straight away.
      </p>

      <div class="settings__path">
        <span>Database</span>
        <input
          type="text"
          value={path}
          placeholder="/path/to/userdata.db"
          disabled={busy}
          onInput={(e) => { setPath((e.target as HTMLInputElement).value); setPreview(null); setDone(null); }}
        />
        {canBrowse && (
          <button
            class="button"
            disabled={busy}
            onClick={async () => {
              const picked = await api.browse({ title: 'Choose a collection to merge in' });
              if (picked) { setPath(picked); setPreview(null); setDone(null); }
            }}
          >
            Choose…
          </button>
        )}
      </div>

      <div class="settings__row">
        <button class="button" disabled={busy || path.trim() === ''} onClick={() => run(true)}>
          {busy ? 'Reading…' : 'Preview'}
        </button>
        {preview && preview.imported > 0 && (
          <button class="button button--primary" disabled={busy} onClick={() => run(false)}>
            Merge {preview.imported.toLocaleString()} item{preview.imported === 1 ? '' : 's'}
          </button>
        )}
      </div>

      {busy && (
        <div class="progress">
          <div
            class="progress__bar"
            style={{ width: `${bar === null ? 100 : Math.round(bar * 100)}%` }}
            data-indeterminate={bar === null ? 'true' : 'false'}
          />
          <span class="progress__label">{label}</span>
        </div>
      )}

      {error && <div class="settings__alert">{error}</div>}

      {preview && (
        <div class="settings__alert settings__alert--warn">
          {preview.imported > 0
            ? `${preview.imported.toLocaleString()} of ${preview.considered.toLocaleString()} item(s) would be added; `
            : `Nothing to add — all ${preview.considered.toLocaleString()} item(s) are already here; `}
          {preview.duplicates.toLocaleString()} already present
          {preview.rejected > 0 && `, ${preview.rejected.toLocaleString()} unusable`}.
        </div>
      )}

      {done && (
        <div class="settings__alert settings__alert--warn">
          Added {done.imported.toLocaleString()} item(s).{done.backup && ` A copy was saved as ${done.backup}.`}
          {done.statsComputed !== null
            && ` Values computed for ${done.statsComputed.toLocaleString()} item(s).`}
          {done.statsNote && ` ${done.statsNote}`}
        </div>
      )}
    </div>
  );
}

/** The "what am I missing" checklist: every legendary and epic, against what is owned. */
function CollectionView({ filters }: { filters: ItemFilters }) {
  const [entries, setEntries] = useState<CollectionEntry[] | null>(null);

  useEffect(() => {
    setEntries(null);
    const handle = setTimeout(() => {
      api.collection(filters).then(setEntries).catch(() => setEntries([]));
    }, 200);
    return () => clearTimeout(handle);
  }, [JSON.stringify(filters)]);

  if (!entries) return <div class="grid__empty">Loading collection…</div>;
  if (entries.length === 0) {
    return <div class="grid__empty">Nothing matching. Run <code>iagd parse</code> if this is empty.</div>;
  }

  const owned = entries.filter((e) => e.numOwnedSc + e.numOwnedHc > 0).length;

  return (
    <section class="collection">
      <div class="collection__summary">
        {owned} of {entries.length} owned ({Math.round((owned / entries.length) * 100)}%)
      </div>
      <div class="grid">
        {entries.map((entry) => {
          const count = entry.numOwnedSc + entry.numOwnedHc;
          const icon = api.iconUrl(entry.icon);
          return (
            <div key={entry.baseRecord} class={`card card--static ${count ? '' : 'card--missing'}`}>
              <div class="card__icon">
                {icon ? <img src={icon} alt="" loading="lazy" /> : <div class="card__icon--missing" />}
              </div>
              <div class="card__body">
                <div class="card__name">{entry.name}</div>
                <div class="card__meta">
                  <span class={`tier tier--${entry.quality.toLowerCase()}`}>{entry.quality}</span>
                  {count > 0 && <span>×{count}</span>}
                </div>
              </div>
            </div>
          );
        })}
      </div>
    </section>
  );
}

/** Item sets and how much of each is complete. */
function SetsView({ query }: { query: string }) {
  const [sets, setSets] = useState<SetEntry[] | null>(null);

  useEffect(() => {
    setSets(null);
    const handle = setTimeout(() => {
      api.sets(query).then(setSets).catch(() => setSets([]));
    }, 200);
    return () => clearTimeout(handle);
  }, [query]);

  if (!sets) return <div class="grid__empty">Loading sets…</div>;
  if (sets.length === 0) return <div class="grid__empty">No sets matching.</div>;

  return (
    <section class="sets">
      {sets.map((set) => (
        <div key={set.setRecord} class="set">
          <header class="set__header">
            <h3>{set.name}</h3>
            <span class={`set__progress ${set.ownedCount === set.totalCount ? 'set__progress--complete' : ''}`}>
              {set.ownedCount} / {set.totalCount}
            </span>
          </header>
          <div class="set__items">
            {set.items.map((member) => {
              const icon = api.iconUrl(member.icon);
              return (
                <div key={member.baseRecord}
                     class={`set__item ${member.owned ? '' : 'set__item--missing'}`}
                     title={member.name ?? member.baseRecord}>
                  {icon ? <img src={icon} alt="" loading="lazy" /> : <div class="card__icon--missing" />}
                  <span>{member.name}</span>
                </div>
              );
            })}
          </div>
        </div>
      ))}
    </section>
  );
}

interface TransferState {
  transferId: string | null;
  message: string;
  pending: boolean;
}

function ItemPanel({ id, transfer, onSend, onCancel, onClose, mods, allowRetarget }: {
  id: number;
  transfer: TransferState | undefined;
  onSend: (id: number, target?: TransferTarget) => void;
  onCancel: (transferId: string) => void;
  onClose: () => void;
  mods: ModInfo[];
  allowRetarget: boolean;
}) {
  const [detail, setDetail] = useState<ItemDetail | null>(null);
  // Undefined means "wherever the item came from", which is what upstream does when its
  // stash picker is disabled.
  const [target, setTarget] = useState<TransferTarget | undefined>(undefined);

  useEffect(() => {
    setDetail(null);
    api.item(id).then(setDetail).catch(() => setDetail(null));
  }, [id]);

  if (!detail) return <aside class="panel"><div class="panel__empty">Loading…</div></aside>;

  const icon = api.iconUrl(detail.item.icon);

  return (
    <aside class="panel">
      <header class="panel__header">
        {icon && <img class="panel__icon" src={icon} alt="" />}
        <h2><GrimText text={detail.item.name} /></h2>
        <button class="panel__close" onClick={onClose} title="Close (Esc)" aria-label="Close">
          ×
        </button>
      </header>

      <div class="panel__facts">
        {detail.item.level > 0 && <span>Level {detail.item.level}</span>}
        {detail.item.itemClass && <span>{detail.item.itemClass}</span>}
        {detail.item.isHardcore && <span class="panel__hc">Hardcore</span>}
      </div>

      <div class="panel__stats">
        {/* Not the stat lines: the card behind this panel already shows them, and printing the
            same tooltip twice on one screen is how the old overlay earned its reputation. What
            is here is what the card cannot do — choosing where an item goes, and following the
            transfer once it is queued. */}
        {detail.skill && (
          <div class="skill">
            <div class="skill__head">
              <span class="skill__name">{detail.skill.name ?? 'Grants a skill'}</span>
              {detail.skill.level > 0 && <span class="skill__level">level {detail.skill.level}</span>}
            </div>
            <div class="skill__kind">
              {/* A trigger means the game fires it; without one it goes on the action bar. */}
              {detail.skill.trigger ? 'Triggered automatically' : 'Activated'}
              {detail.skill.summonsPets && ' · summons a pet'}
            </div>
            {detail.skill.description && (
              <div class="skill__description">{detail.skill.description}</div>
            )}
          </div>
        )}
      </div>

      <footer class="panel__footer">
        {/* The record and seed together identify the exact roll, which is what anyone asking
            for help in a forum thread needs to paste. */}
        <button
          class="panel__record"
          title="Copy record and seed"
          onClick={() => navigator.clipboard?.writeText(
            `${detail.item.baseRecord} seed=${detail.item.seed}`)}
        >
          seed {detail.item.seed} — copy record
        </button>

        {allowRetarget && !transfer?.pending && (
          <label class="panel__target">
            <span>Send to</span>
            <select
              class="filters__select"
              value={target ? `${target.hardcore ? 'hc' : 'sc'}:${target.mod ?? ''}` : ''}
              onChange={(e) => {
                const raw = (e.target as HTMLSelectElement).value;
                if (!raw) { setTarget(undefined); return; }
                const [branch, ...rest] = raw.split(':');
                setTarget({ hardcore: branch === 'hc', mod: rest.join(':') });
              }}
            >
              <option value="">Where it came from</option>
              {(mods.length > 0 ? mods : [{ name: '', items: 0 }]).flatMap((m) => [
                <option key={`sc:${m.name}`} value={`sc:${m.name}`}>
                  Softcore · {m.name === '' ? 'Vanilla' : m.name}
                </option>,
                <option key={`hc:${m.name}`} value={`hc:${m.name}`}>
                  Hardcore · {m.name === '' ? 'Vanilla' : m.name}
                </option>,
              ])}
            </select>
          </label>
        )}

        {transfer?.pending ? (
          <button class="button" onClick={() => transfer.transferId && onCancel(transfer.transferId)}>
            Cancel transfer
          </button>
        ) : (
          <button class="button button--primary" onClick={() => onSend(id, target)}>
            Send to game
          </button>
        )}

        {transfer?.message && (
          <div class={`panel__message ${transfer.pending ? 'panel__message--pending' : ''}`}>
            {transfer.message}
          </div>
        )}
      </footer>
    </aside>
  );
}

function App() {
  const [status, setStatus] = useState<HostStatus | null>(null);
  const [query, setQuery] = useState('');
  const [tab, setTab] = useState<Tab>('items');
  const [view, setView] = useState<View>('items');
  const [filters, setFilters] = useState<ItemFilters>({});
  const [catalogue, setCatalogue] = useState<FilterCatalogue | null>(null);
  const [mods, setMods] = useState<ModInfo[]>([]);
  // Choosing a target stash is gated on the same setting upstream gates its stash picker with.
  const [allowRetarget, setAllowRetarget] = useState(false);
  const [items, setItems] = useState<ItemCardData[]>([]);
  const [total, setTotal] = useState(0);
  // Deep link: #item=7 opens that item directly, so a specific item can be linked to or
  // reopened after a reload.
  const [selected, setSelected] = useState<number | null>(() => {
    const match = /(?:^|[#&])item=(\d+)/.exec(location.hash);
    return match ? Number(match[1]) : null;
  });
  const [toast, setToast] = useState<string | null>(null);
  // Pushed by the host while a merge runs. Held here rather than in the settings view because
  // that is where the event socket already is; a merge must not need a second connection.
  const [mergeProgress, setMergeProgress] = useState<MergeProgressEvent | null>(null);
  // Transfers outlive the panel and survive a reload, so they live here rather than in it.
  const [transfers, setTransfers] = useState<Record<number, TransferState>>({});
  const searchRef = useRef<HTMLInputElement>(null);
  const queryRef = useRef(query);
  queryRef.current = query;
  // Whether anything beyond the search box is narrowing the list — used to decide if a newly
  // looted item can be folded into the grid without contradicting the filters.
  const filteredRef = useRef(false);
  filteredRef.current = Object.values(filters).some(
    (value) => value !== undefined && value !== '' && value !== false && value !== 0);

  const load = useCallback(async (search: ItemFilters, append = false) => {
    const page = await api.items(search, append ? items.length : 0, PAGE_SIZE);
    setTotal(page.total);
    setItems((current) => (append ? [...current, ...page.items] : page.items));
  }, [items.length]);

  const search: ItemFilters = { ...filters, q: query };

  // Debounced search: typing should not fire a request per keystroke.
  useEffect(() => {
    if (tab !== 'items') return;
    const handle = setTimeout(() => { load(search).catch(() => {}); }, 200);
    return () => clearTimeout(handle);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [query, JSON.stringify(filters), tab]);

  useEffect(() => {
    api.status().then(setStatus).catch(() => {});
    // The filter definitions live on the host so there is one copy of them; fetched once.
    api.filters().then(setCatalogue).catch(() => {});
    api.mods().then(setMods).catch(() => {});
    api.settings().then((s) => setAllowRetarget(s.transferAnyMod)).catch(() => {});

    return subscribe((event: HostEvent) => {
      switch (event.type) {
        case 'status':
          setStatus(event.data);
          break;

        case 'itemLooted': {
          setStatus((s) => (s ? { ...s, itemCount: s.itemCount + 1 } : s));
          setToast(`Looted ${stripGrimText(event.data.item.name)}`);
          // Only fold it into the visible list when nothing is narrowing it; otherwise the
          // grid would contradict its own filters. The host does not re-run the query per
          // event, so a match cannot be tested here.
          if (!queryRef.current.trim() && !filteredRef.current) {
            setItems((current) => [event.data, ...current]);
            setTotal((t) => t + 1);
          }
          break;
        }

        case 'itemRemoved':
          setItems((current) => current.filter((c) => c.item.id !== event.data.id));
          setTotal((t) => Math.max(0, t - 1));
          setSelected((current) => (current === event.data.id ? null : current));
          break;

        case 'transferCompleted': {
          const { itemId, collected, message } = event.data;
          setTransfers((current) => ({
            ...current,
            [itemId]: { transferId: null, message, pending: false },
          }));
          setToast(message);
          break;
        }

        case 'mergeProgress':
          setMergeProgress(event.data);
          break;

        case 'message':
          setToast(event.data.text);
          break;
      }
    });
  }, []);

  useEffect(() => {
    const hash = selected === null ? '' : `#item=${selected}`;
    if (location.hash !== hash) history.replaceState(null, '', location.pathname + hash);
  }, [selected]);

  // Keyboard: this sits next to a fullscreen game and gets alt-tabbed into for a few seconds
  // at a time, so reaching for the mouse to search is the wrong shape.
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      const target = event.target as HTMLElement | null;
      const typing = target?.tagName === 'INPUT' || target?.tagName === 'SELECT';

      if (event.key === 'Escape') {
        if (typing && query) { setQuery(''); return; }
        setSelected(null);
        return;
      }
      // "/" is the search key everywhere else; Ctrl+F is muscle memory from the browser.
      if (!typing && (event.key === '/' || (event.key === 'f' && (event.ctrlKey || event.metaKey)))) {
        event.preventDefault();
        searchRef.current?.focus();
        searchRef.current?.select();
      }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [query]);

  useEffect(() => {
    if (!toast) return;
    const handle = setTimeout(() => setToast(null), 4000);
    return () => clearTimeout(handle);
  }, [toast]);

  const transferItem = async (card: ItemCardData, all: boolean) => {
    // "Transfer all" sends every copy the card stands for; otherwise just the one it shows.
    const ids = all ? card.duplicates : [card.item.id];
    for (const id of ids) {
      setTransfers((c) => ({ ...c, [id]: { transferId: null, message: 'Queueing…', pending: true } }));
      const result = await api.transfer(id);
      setTransfers((c) => ({
        ...c,
        [id]: 'transferId' in result
          ? { transferId: result.transferId, message: 'Queued — open the transfer stash in game.', pending: true }
          : { transferId: null, message: result.message, pending: false },
      }));
      if (!('transferId' in result)) { setToast(result.message); break; }
    }
  };

  return (
    <div class="app">
      {/* The window's own tabs, which upstream draws around its embedded browser. */}
      <nav class="tabstrip">
        {([
          ['items', 'Items'],
          ['online', 'Online'],
          ['settings', 'Settings'],
          ['grimdawn', 'Grim Dawn'],
        ] as [Tab, string][]).map(([value, label]) => (
          <button
            key={value}
            class={`tabstrip__tab ${tab === value ? 'tabstrip__tab--active' : ''}`}
            onClick={() => setTab(value)}
          >
            {label}
          </button>
        ))}
      </nav>

      <div class="app__body">
        {tab === 'items' && (
          <div class={`workspace ${selected !== null ? 'workspace--panel' : ''}`}>
            <FilterSidebar filters={filters} onChange={setFilters} catalogue={catalogue} />

            <div class="workspace__main">
              <Toolbar
                filters={filters}
                onChange={setFilters}
                catalogue={catalogue}
                mods={mods}
                query={query}
                onQuery={setQuery}
                searchRef={searchRef}
              />

              {/* Below here is what upstream renders in the browser control. */}
              <div class="webview">
                <header class="webnav">
                  <nav>
                    {([
                      ['items', 'Items'],
                      ['collections', 'Collections'],
                      ['sets', 'Sets'],
                      ['help', 'Help'],
                    ] as [View, string][]).map(([value, label]) => (
                      <a
                        key={value}
                        class={view === value ? 'webnav--active' : ''}
                        onClick={() => setView(value)}
                      >
                        {label}
                      </a>
                    ))}
                    {/* Upstream's nav also carries Discord and Patreon links. This port does not
                        reproduce them and should not: it is an unaffiliated port, and those are
                        somebody else's community and somebody else's funding. Sending this
                        project's users there would imply a connection that does not exist and
                        put support requests for this code in front of people who did not write
                        it. Do not add them back. */}
                    <a onClick={() => window.open('https://grimdawn.evilsoft.net/enchantments/', '_blank')}>
                      Components
                    </a>
                  </nav>

                  {view === 'items' && (
                    <button
                      class="webnav__clipboard"
                      onClick={() => navigator.clipboard?.writeText(
                        items.map((c) => stripGrimText(c.item.name)).join('\n'))}
                    >
                      Copy to Clipboard
                      <span>Displaying {items.length}/{total}</span>
                    </button>
                  )}
                </header>

                <main class="webview__body">
                  {view === 'collections' && <CollectionView filters={search} />}
                  {view === 'sets' && <SetsView query={query} />}
                  {view === 'help' && (
                    <div class="help">
                      <h2>Help</h2>
                      <p>
                        Items are captured by a hook inside Grim Dawn, which the port attaches
                        once the game is running. The status line at the bottom says whether that
                        has happened.
                      </p>
                      <p>
                        Transfers go the other way: an item is queued here and appears the next
                        time you open the transfer stash in game.
                      </p>
                      <p>
                        Upstream's help page is served from the internet and is not reproduced
                        here.
                      </p>
                    </div>
                  )}

                  {view === 'items' && (
                    <section class="items">
                      {items.length === 0 && (
                        <div class="items__empty">
                          {query || filteredRef.current
                            ? 'Nothing matching those filters.'
                            : 'No items yet — loot something into your stash.'}
                        </div>
                      )}
                      {items.map((card) => (
                        <ItemCard
                          key={card.item.id}
                          card={card}
                          selected={selected === card.item.id}
                          onSelect={() => setSelected(card.item.id)}
                          onTransfer={(all) => transferItem(card, all)}
                          transferring={Boolean(transfers[card.item.id]?.pending)}
                        />
                      ))}
                      {items.length < total && (
                        <button class="button items__more" onClick={() => load(search, true)}>
                          Load more ({items.length} of {total})
                        </button>
                      )}
                    </section>
                  )}
                </main>
              </div>
            </div>

            {selected !== null && (
              <ItemPanel
                id={selected}
                onClose={() => setSelected(null)}
                mods={mods}
                allowRetarget={allowRetarget}
                transfer={transfers[selected]}
                onSend={async (id, target) => {
                  setTransfers((c) => ({
                    ...c, [id]: { transferId: null, message: 'Queueing…', pending: true },
                  }));
                  const result = await api.transfer(id, target);
                  if ('transferId' in result) {
                    setTransfers((c) => ({
                      ...c,
                      [id]: {
                        transferId: result.transferId,
                        message: 'Queued — open the transfer stash in game.',
                        pending: true,
                      },
                    }));
                  } else {
                    // Refused outright (game not running, no hook): nothing was queued.
                    setTransfers((c) => ({
                      ...c, [id]: { transferId: null, message: result.message, pending: false },
                    }));
                  }
                }}
                onCancel={async (transferId) => {
                  const result = await api.cancelTransfer(transferId);
                  setTransfers((c) => ({
                    ...c,
                    [selected]: { transferId: null, message: result.message, pending: false },
                  }));
                }}
              />
            )}
          </div>
        )}

        {tab === 'settings' && <SettingsView onSaved={setToast} progress={mergeProgress} />}

        {tab === 'online' && (
          <div class="tabpage">
            <h2>Online</h2>
            <p>
              Upstream keeps cloud backup and buddy sharing here. Neither is implemented in this
              port yet, so nothing is being sent anywhere.
            </p>
            <p>
              Your collection lives in one file, and <code>iagd backup</code> copies it. The
              Settings tab can merge another collection into this one.
            </p>
          </div>
        )}

        {tab === 'grimdawn' && (
          <div class="tabpage">
            <h2>Grim Dawn Database</h2>
            <p>
              Item names, icons and levels are read from the game's own archives by
              <code>iagd parse</code>, then rolled per item by <code>iagd stats</code>.
            </p>
            <dl class="tabpage__facts">
              <dt>Installation</dt><dd>{status?.gameDir ?? 'not found'}</dd>
              <dt>Templates parsed</dt><dd>{status?.templateCount?.toLocaleString() ?? '0'}</dd>
              <dt>Items awaiting analysis</dt><dd>{status?.itemsNeedingStats?.toLocaleString() ?? '0'}</dd>
            </dl>
            <h3>Mods</h3>
            <table class="tabpage__table">
              <thead><tr><th>Mod</th><th>Items</th></tr></thead>
              <tbody>
                {mods.map((m) => (
                  <tr key={m.name}>
                    <td>{m.name || 'Vanilla'}</td>
                    <td>{m.items.toLocaleString()}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>

      {/* The window's status bar, which upstream uses for the result count and its version. */}
      <footer class="statusbar">
        <span>{tab === 'items' ? `${total.toLocaleString()} matching items found` : ''}</span>
        <StatusBar status={status} />
      </footer>

      {toast && <div class="toast">{toast}</div>}
    </div>
  );
}

render(<App />, document.getElementById('app')!);
