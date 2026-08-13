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
  // Reading Grim Dawn's data. The client starts this itself — at startup when the game has
  // been patched, and whenever the installation or language changes — so this is a progress
  // report, not an instruction.
  if (status.parsingGameData) {
    return (
      <div class="status status--warn">
        <span class="dot dot--warn" /> Reading Grim Dawn's data…
        <span class="status__detail">{status.parseStep ?? 'this takes a moment'}</span>
      </div>
    );
  }

  // Analysing the collection: rarity, levels, rolled values, and the game stat rows every
  // record-driven filter reads. Ten seconds or more, during which those filters legitimately
  // match nothing — so saying nothing here makes a working client look broken.
  if (status.analysing) {
    return (
      <div class="status status--warn">
        <span class="dot dot--warn" /> Analysing the collection…
        <span class="status__detail">
          {status.analysisStep ?? 'the filters fill in as it finishes'}
        </span>
      </div>
    );
  }

  if (status.gameDataStale) {
    return (
      <div class="status status--warn">
        <span class="dot dot--warn" /> {status.gameDataStale}
        <span class="status__detail">
          {status.gameDir
            ? 'reading it now — the Grim Dawn tab can start it again'
            : 'Grim Dawn was not found — set the game folder in Settings'}
        </span>
      </div>
    );
  }

  // Items still waiting to be described. The client does this itself — at startup, as items
  // are imported, and after a merge — so this reports progress rather than asking for a
  // command to be typed. The one case the user has to act on is a missing game folder, since
  // the numbers come out of Grim Dawn's own archives.
  if (status.itemsNeedingStats > 0) {
    return (
      <div class="status status--warn">
        <span class="dot dot--warn" />{' '}
        {status.gameDir
          ? `Analysing ${status.itemsNeedingStats.toLocaleString()} item(s)…`
          : `${status.itemsNeedingStats.toLocaleString()} item(s) cannot be analysed`}
        <span class="status__detail">
          {status.gameDir
            ? 'rarity and the level filters need it; the list works meanwhile'
            : 'Grim Dawn was not found — set the game folder in Settings'}
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
 * What the "Copy to clipboard" link puts on the clipboard.
 *
 * BBCode, one item per line, exactly as upstream builds it in
 * `ItemContainer.getClipboardContent` — a coloured forum link per item, pointing at its search
 * page. The point of the button is pasting a list of finds into a forum post, and plain names
 * (which is what this port copied before) are not that.
 *
 * The colour names are upstream's map from its internal rarities, and quotation marks are
 * stripped from the query for the same reason upstream strips them: they would terminate the
 * BBCode attribute.
 */
const CLIPBOARD_COLOURS: Record<string, string> = {
  Epic: 'DarkOrchid',
  Blue: 'RoyalBlue',
  Green: 'SeaGreen',
  Yellow: 'Yellow',
  Unknown: '',
};

function clipboardText(cards: ItemCardData[]): string {
  return cards
    .map((card) => {
      const name = stripGrimText(card.item.name);
      const colour = CLIPBOARD_COLOURS[card.item.rarity ?? 'Unknown'] ?? '';
      const query = name.replace('"', '');
      return `[URL="https://grimdawn.evilsoft.net/search/?query=${query}"]`
           + `[COLOR="${colour}"]${name}[/COLOR][/URL]`;
    })
    .join('\n');
}

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

  // A numeric comparison is stored as "fieldA+fieldB>=30", which is what the host parses. The
  // fields identify which checkbox it belongs to, so a group carries at most one.
  const fieldsOf = (comparison: string) => comparison.split(/[<>=]/, 1)[0];

  const comparisonFor = (fields: string[]) =>
    (filters.stat ?? []).find((s) => fieldsOf(s) === fields.join('+'));

  const setComparison = (fields: string[], operator: string, value: string) => {
    const rest = (filters.stat ?? []).filter((s) => fieldsOf(s) !== fields.join('+'));
    const next = value.trim() === ''
      ? rest
      : [...rest, `${fields.join('+')}${operator}${value.trim()}`];
    patch({ stat: next.length ? next : undefined });
  };

  const statBox = (group: FilterGroup) => {
    const on = activeGroups.has(key(group.fields));
    const comparison = comparisonFor(group.fields);
    const operator = comparison?.match(/>=|<=|>|<|=/)?.[0] ?? '>=';
    const threshold = comparison ? comparison.slice(fieldsOf(comparison).length + operator.length) : '';

    return (
      <div key={group.label} class="check-row">
        <label class="check" title={group.fields.join(', ')}>
          <input
            type="checkbox"
            checked={on}
            onChange={(e) => {
              const checked = (e.target as HTMLInputElement).checked;
              const rest = (filters.has ?? []).filter((g) => key(g) !== key(group.fields));
              patch({ has: checked ? [...rest, group.fields] : rest.length ? rest : undefined });
              // Upstream's numeric filter lives on the checkbox and goes with it.
              if (!checked) setComparison(group.fields, operator, '');
            }}
          />
          <span>{group.label}</span>
        </label>

        {/*
          Upstream puts a funnel button on a checked stat checkbox, opening a dialog that asks
          for a comparison and a number; the same two answers are asked for inline here. The
          value compared is the item's own rolled total for these fields, summed.
        */}
        {on && (
          <span class="check-row__filter">
            <select
              value={operator}
              title="Match items where this stat is:"
              onChange={(e) => setComparison(group.fields, (e.target as HTMLSelectElement).value, threshold)}
            >
              <option value=">=">&ge;</option>
              <option value=">">&gt;</option>
              <option value="<=">&le;</option>
              <option value="<">&lt;</option>
              <option value="=">=</option>
            </select>
            <input
              type="text"
              inputMode="decimal"
              placeholder="any"
              value={threshold}
              onInput={(e) => setComparison(group.fields, operator, (e.target as HTMLInputElement).value)}
            />
          </span>
        )}
      </div>
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

/** The dropdown value for the branch a set of filters is scoped to. */
const branchKey = (filters: ItemFilters) => `${filters.hardcore ? 'hc' : 'sc'}:${filters.mod ?? ''}`;

/** "No mod", "No mod (hardcore)", "Grimarillion" — upstream names vanilla "No mod". */
const branchLabel = (branch: ModInfo) =>
  `${branch.name || 'No mod'}${branch.hardcore ? ' (hardcore)' : ''}`;

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

      {/*
        The branch: one mod and one hardcore/softcore side, never both. Upstream's dropdown
        works the same way and is never empty, because the game gives each combination its own
        transfer stash and an item cannot move between them.
      */}
      <select
        class="toolbar__select"
        value={branchKey(filters)}
        onChange={(e) => {
          const [hardcore, ...rest] = (e.target as HTMLSelectElement).value.split(':');
          patch({ hardcore: hardcore === 'hc', mod: rest.join(':') });
        }}
        title="Items are partitioned by mod and by hardcore; each has its own stash in game"
      >
        {(mods.length ? mods : [{ name: '', hardcore: false, items: 0 }]).map((m) => (
          <option key={`${m.hardcore}:${m.name}`} value={`${m.hardcore ? 'hc' : 'sc'}:${m.name}`}>
            {branchLabel(m)}
          </option>
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
              {/*
                Both branches of every mod, whether or not the collection holds items there: a
                stash you have never used is still somewhere an item can be sent. The branch
                list arrives already split by hardcore, so the mod names are taken from it.
              */}
              {[...new Set([...mods.map((m) => m.name), ''])].flatMap((name) => [
                <option key={`sc:${name}`} value={`sc:${name}`}>
                  Softcore · {name === '' ? 'Vanilla' : name}
                </option>,
                <option key={`hc:${name}`} value={`hc:${name}`}>
                  Hardcore · {name === '' ? 'Vanilla' : name}
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
  // Vanilla softcore until the branch list arrives, which is where the game puts everything an
  // unmodded run loots. Upstream starts with a branch selected too; leaving it unset would
  // search every mod and both branches at once, which upstream cannot do and the game does not
  // mean — and the dropdown would then be showing a branch that was not being searched.
  const [filters, setFilters] = useState<ItemFilters>({ mod: '', hardcore: false });
  const [catalogue, setCatalogue] = useState<FilterCatalogue | null>(null);
  const [mods, setMods] = useState<ModInfo[]>([]);
  // Choosing a target stash is gated on the same setting upstream gates its stash picker with.
  const [allowRetarget, setAllowRetarget] = useState(false);
  const [items, setItems] = useState<ItemCardData[]>([]);
  const [total, setTotal] = useState(0);
  // Cards are what the list pages through; items are what the window reports. Identical items
  // share a card, so a collection of 7,483 items is 3,669 cards and saying "3,669 matching
  // items found" is simply wrong. Upstream reports items too.
  const [totalItems, setTotalItems] = useState(0);
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
    setTotalItems(page.totalItems);
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
    api.mods()
      .then((branches) => {
        setMods(branches);
        // A collection that holds nothing on the default branch would otherwise open empty with
        // no hint that the items are one dropdown entry away. Upstream picks the first entry for
        // the same reason (ModSelectionHandler.SetDefaultModIfAvailable).
        setFilters((current) => {
          const chosen = branchKey(current);
          if (branches.length === 0 || branches.some((b) => `${b.hardcore ? 'hc' : 'sc'}:${b.name}` === chosen)) {
            return current;
          }
          return { ...current, mod: branches[0].name, hardcore: branches[0].hardcore };
        });
      })
      .catch(() => {});
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
                      onClick={() => navigator.clipboard?.writeText(clipboardText(items))}
                    >
                      Copy to clipboard
                      {/* Items on both sides of the slash, as upstream counts them: it sums the
                          player items across the cards it has rendered. */}
                      <span>
                        Displaying {items.reduce((n, c) => n + c.copies, 0).toLocaleString()}/
                        {totalItems.toLocaleString()}
                      </span>
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
                          {/*
                            While the analysis runs, the record-driven filters — mastery, damage
                            type, slot — genuinely match nothing, because the rows they read are
                            being rewritten. "Nothing matching those filters" is true and
                            useless: it reads as a broken filter rather than one waiting.
                          */}
                          {status?.analysing
                            ? 'Analysing the collection — filters fill in as it finishes.'
                            : query || filteredRef.current
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
                          Load more ({items.length.toLocaleString()} of {total.toLocaleString()} cards)
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
              Item names, icons and levels come from the game's own archives. The client reads
              them when it starts if the game has been patched, and whenever you point it at a
              different installation — there is nothing to run by hand.
            </p>

            <div class="settings__row">
              <button
                class="button button--primary"
                disabled={!status?.gameDir || status?.parsingGameData}
                onClick={() => api.parse().then((r) => setToast(
                  r.error ?? `Reading Grim Dawn's data from ${r.gameDir}…`))}
              >
                {status?.parsingGameData ? 'Reading…' : 'Load Database'}
              </button>
              <em>
                {status?.parsingGameData
                  ? (status.parseStep ?? 'working')
                  : status?.analysing
                    ? (status.analysisStep ?? 'analysing the collection')
                    : 'reads item names, icons and skills, then analyses the collection'}
              </em>
            </div>
            <dl class="tabpage__facts">
              <dt>Installation</dt><dd>{status?.gameDir ?? 'not found'}</dd>
              <dt>Templates parsed</dt><dd>{status?.templateCount?.toLocaleString() ?? '0'}</dd>
              <dt>Items awaiting analysis</dt><dd>{status?.itemsNeedingStats?.toLocaleString() ?? '0'}</dd>
              {/* The second half of the same job, and the one with no item count behind it. */}
              <dt>Analysis</dt>
              <dd>{status?.analysing ? (status.analysisStep ?? 'running') : 'up to date'}</dd>
            </dl>
            <h3>Mods</h3>
            <table class="tabpage__table">
              <thead><tr><th>Mod</th><th>Items</th></tr></thead>
              <tbody>
                {mods.map((m) => (
                  <tr key={`${m.hardcore}:${m.name}`}>
                    <td>{m.name || 'Vanilla'}{m.hardcore ? ' (hardcore)' : ''}</td>
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
        <span>{tab === 'items' ? `${totalItems.toLocaleString()} matching items found` : ''}</span>
        <StatusBar status={status} />
      </footer>

      {toast && <div class="toast">{toast}</div>}
    </div>
  );
}

render(<App />, document.getElementById('app')!);
