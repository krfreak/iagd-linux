import { render } from 'preact';
import { useEffect, useState, useCallback, useRef } from 'preact/hooks';
import {
  api, subscribe, ItemSummary, ItemCard as ItemCardData, ItemDetail, HostStatus, HostEvent,
  ItemFilters, RARITIES, LANGUAGE_NAMES,
  CollectionEntry, SetEntry, Settings, FilterCatalogue, FilterGroup, ModInfo, TransferTarget,
  MergePreview, MergeProgressEvent, ItemStatLine,
  CloudStatus, Buddy, BackedUpCharacter, CharacterBackupState,
} from './api';
import { GrimText, StatLine, stripGrimText } from './GrimText';
import { Help } from './Help';
import { Components } from './Components';
import { Support } from './Support';
import './style.css';

const PAGE_SIZE = 60;

/**
 * What the level boxes start at, and go back to.
 *
 * Upstream's are never empty: its designer sets "0" and "110", and ClearFilters puts them back
 * (SplitSearchWindow). Both ends are meaningful — 0 means "no minimum", and 110 is above
 * anything the game requires, so the pair reads as "everything" while showing the shape of what
 * the boxes want.
 */
const DEFAULT_MIN_LEVEL = 0;
const DEFAULT_MAX_LEVEL = 110;

/**
 * A level box's value. Empty while typing rather than snapping to a number, so a box can be
 * cleared and retyped; the blur handler puts the default back if it was left that way.
 */
function levelFrom(raw: string): number | undefined {
  const digits = raw.replace(/\D/g, '');
  return digits === '' ? undefined : Number(digits);
}

/** Whether a level box is at its default, i.e. not narrowing anything. */
const isDefaultLevels = (filters: ItemFilters) =>
  (filters.minLevel ?? DEFAULT_MIN_LEVEL) === DEFAULT_MIN_LEVEL
  && (filters.maxLevel ?? DEFAULT_MAX_LEVEL) === DEFAULT_MAX_LEVEL;

/**
 * The window's own tabs, which upstream draws in WinForms around its embedded browser.
 *
 * "Grim Dawn" is upstream's name for the tab holding the game installation and its mod
 * databases; "Online" is cloud backup and buddy sharing, which this port does not implement yet
 * and which says so rather than being absent.
 */
type Tab = 'items' | 'online' | 'settings' | 'grimdawn';

/** The tabs inside the item view, which upstream draws in the web page itself. */
type View = 'items' | 'collections' | 'sets' | 'components' | 'help' | 'support';

function StatusBar({ status }: { status: HostStatus | null }) {
  if (!status) return <div class="status status--warn">Connecting to iagd-host…</div>;

  // First, because it outranks everything below it: without a prefix there is no channel to the
  // hook, so nothing is captured and nothing can be sent back, however healthy the rest looks.
  // This used to be a line on stderr and a 503 the page swallowed, leaving "Connecting to
  // iagd-host…" on screen for ever — the one state in this port with no words attached to it.
  if (status.setupWarning) {
    return (
      <div class="status status--warn">
        <span class="dot dot--warn" /> Loot cannot be captured
        <span class="status__detail">
          {status.setupWarning} Set the Proton prefix in Settings.
        </span>
      </div>
    );
  }

  // Same consequence, different cause, so it is worth its own sentence: the file is what puts
  // the hook into Wine mode, and without it the hook falls back to shared memory and captures
  // nothing while the game and this window both look perfectly well.
  if (status.hookWarning) {
    return (
      <div class="status status--warn">
        <span class="dot dot--warn" /> The hook could not be configured
        <span class="status__detail">{status.hookWarning}</span>
      </div>
    );
  }

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

/**
 * Upstream's ReplicaStatContainer, for the part that changes how a row is drawn.
 *
 * A set skill arrives as a run of type-80 rows — name, description, then one row per stat — and
 * 80 has no colour of its own, so upstream walks the run and re-types it: the first row becomes
 * a skill name (23), the second a description (21), and the rest stat rows (40). Without this
 * the whole block is drawn in the default colour, which in a dark theme is very nearly the
 * background.
 *
 * The counter is per item, and resets on any row that is not part of the run.
 */
function replicaRows(stats: ItemStatLine[]): ItemStatLine[] {
  let stage = 0;
  return stats.map((stat) => {
    if (stat.textClass !== 80) {
      stage = 0;
      return stat;
    }
    const textClass = stage === 0 ? 23 : stage === 1 ? 21 : 40;
    stage++;
    return { ...stat, textClass };
  });
}

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
  const body = replicaRows(stats.filter((s) => !HIDDEN_STAT_TYPES.has(s.textClass)));

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
            /* Type 0 is the game's blank line between blocks; upstream draws it as a break
               rather than as an empty row of text. A computed line has no row type at all. */
            stat.textClass === 0
              ? <li key={index} class="stat stat--break" />
              : (
                <li
                  key={index}
                  class={`stat stat--class-${stat.textClass} ${stat.section ? `stat--${stat.section}` : ''}`}
                >
                  <StatLine line={stat} />
                </li>
              )
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
 * Upstream's comparison modal (ItemComparer.tsx), reached from a card that stands for more than
 * one item.
 *
 * Identical copies share a card — upstream merges on base record plus prefix plus suffix, and so
 * does this port — but that key says nothing about what each copy *rolled*. Two greens with the
 * same affixes can differ by fifty points of health, so "send one of these" is a real choice, and
 * quietly sending the first row is the wrong answer to it. Upstream puts every copy on screen
 * with its own tooltip and its own transfer link; this does the same.
 *
 * The copies are fetched when the modal opens. Upstream never has to: its search result already
 * carries all of them. See api.details.
 *
 * Picking one closes the modal, which is upstream's behaviour too — there it falls out of the
 * item list changing under the dialogue (componentWillReceiveProps), here it is said outright.
 */
function ItemComparer({ card, transfers, onTransfer, onClose }: {
  card: ItemCardData;
  transfers: Record<number, TransferState>;
  onTransfer: (id: number) => void;
  onClose: () => void;
}) {
  // Null while the copies are in flight, so "loading" and "none left" stay distinguishable.
  const [copies, setCopies] = useState<ItemDetail[] | null>(null);
  const ids = card.duplicates.join(',');

  useEffect(() => {
    let live = true;
    setCopies(null);
    api.details(card.duplicates)
      .then((found) => { if (live) setCopies(found); })
      .catch(() => { if (live) setCopies([]); });
    return () => { live = false; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [ids]);

  return (
    // Clicking away closes it, as upstream's outside-click handler does.
    <div class="comparer" onClick={onClose}>
      <div class="comparer__box" onClick={(e) => e.stopPropagation()}>
        <header class="comparer__header">
          <h2>Item Comparison</h2>
          <span class="comparer__subject">
            {stripGrimText(card.item.name)} · {card.copies} copies
          </span>
          <button class="comparer__close" onClick={onClose} title="Close (Esc)" aria-label="Close">
            ×
          </button>
        </header>

        <div class="comparer__list">
          {copies === null && <div class="comparer__empty">Loading…</div>}
          {copies?.length === 0 && (
            <div class="comparer__empty">These copies are no longer in the collection.</div>
          )}
          {copies?.map((copy) => (
            <ItemCard
              key={copy.item.id}
              // One copy, so the card offers the single transfer link and no "transfer all" —
              // which is exactly what upstream's ReplicaItem shows.
              card={{
                item: copy.item, stats: copy.stats, skill: copy.skill,
                copies: 1, duplicates: [copy.item.id],
              }}
              selected={false}
              onSelect={() => {}}
              onTransfer={() => onTransfer(copy.item.id)}
              transferring={Boolean(transfers[copy.item.id]?.pending)}
            />
          ))}
        </div>
      </div>
    </div>
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

  /**
   * The comparisons other than this group's, which is what every edit to one starts from.
   *
   * Pure, and deliberately so: `patch` spreads the *current* props, so two patches in one event
   * handler make the second overwrite the first. That is not hypothetical — unchecking a box
   * with a comparison on it patched `has`, then patched `stat` from the unchanged filters, and
   * put `has` back exactly as it was. The box could not be unchecked.
   */
  const comparisonsWithout = (fields: string[]) =>
    (filters.stat ?? []).filter((s) => fieldsOf(s) !== fields.join('+'));

  const comparisons = (fields: string[], operator: string, value: string) => {
    const rest = comparisonsWithout(fields);
    const next = value.trim() === ''
      ? rest
      : [...rest, `${fields.join('+')}${operator}${value.trim()}`];
    return next.length ? next : undefined;
  };

  const setComparison = (fields: string[], operator: string, value: string) =>
    patch({ stat: comparisons(fields, operator, value) });

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
              // One patch, not two: upstream's numeric filter lives on the checkbox and goes
              // with it, and both changes have to land in the same update.
              const kept = comparisonsWithout(group.fields);
              patch({
                has: checked ? [...rest, group.fields] : rest.length ? rest : undefined,
                stat: checked ? filters.stat : (kept.length ? kept : undefined),
              });
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
          onChange={(e) => {
            const checked = (e.target as HTMLInputElement).checked;
            patch(checked ? { orderByLevel: true, orderByNewest: false } : { orderByLevel: false });
          }}
        />
        <span>Order By Level</span>
      </label>

      {/* Not upstream's — see ItemQuery.OrderByNewest. It wins over "Order By Level" when both
          are ticked, which is why ticking it unticks the other rather than leaving the pair
          saying two different things. */}
      <label class="toolbar__check">
        <input
          type="checkbox"
          checked={Boolean(filters.orderByNewest)}
          onChange={(e) => {
            const checked = (e.target as HTMLInputElement).checked;
            patch(checked ? { orderByNewest: true, orderByLevel: false } : { orderByNewest: false });
          }}
        />
        <span>Newest First</span>
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

      {/*
        Upstream's level boxes are never empty: they start at 0 and 110 and go back to those
        when its filters are cleared (SplitSearchWindow's designer and ClearFilters). Three
        digits, digits only, and an unparseable box falls back to the default when it loses
        focus — all upstream's, including that 0 means "no minimum" rather than level zero.
      */}
      <fieldset class="toolbar__level">
        <legend>Level</legend>
        <input
          type="text"
          inputMode="numeric"
          maxLength={3}
          value={filters.minLevel ?? ''}
          onInput={(e) => patch({ minLevel: levelFrom((e.target as HTMLInputElement).value) })}
          onBlur={(e) => levelFrom((e.target as HTMLInputElement).value) === undefined
                         && patch({ minLevel: DEFAULT_MIN_LEVEL })}
        />
        <input
          type="text"
          inputMode="numeric"
          maxLength={3}
          value={filters.maxLevel ?? ''}
          onInput={(e) => patch({ maxLevel: levelFrom((e.target as HTMLInputElement).value) })}
          onBlur={(e) => levelFrom((e.target as HTMLInputElement).value) === undefined
                         && patch({ maxLevel: DEFAULT_MAX_LEVEL })}
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
/**
 * Upstream's "Backups" tab: online backup, buddy sharing, and character saves.
 *
 * The service belongs to upstream's author and is run for free, so this panel is written to make
 * the *cost* of each switch visible rather than to sell the feature. "I play on more than one
 * PC" in particular is not a convenience toggle — it multiplies how often this client talks to
 * the server and opens a live socket — so it says so where it is switched on.
 *
 * Nothing here polls the service. It polls the local host, which answers from state the
 * background loops already hold.
 */
function OnlineView({ onToast }: { onToast: (text: string) => void }) {
  const [status, setStatus] = useState<CloudStatus | null>(null);
  const [buddies, setBuddies] = useState<Buddy[]>([]);
  const [characters, setCharacters] = useState<BackedUpCharacter[]>([]);
  const [charBackup, setCharBackup] = useState<CharacterBackupState | null>(null);
  /** Per-character download state, keyed by name: 'preparing', 'opened', or an error. */
  // Per character: what the last download attempt did, and the link itself when the host could
  // not open it for us.
  const [downloads, setDownloads] = useState<Record<string, { message: string; url?: string }>>({});
  const [unavailable, setUnavailable] = useState(false);
  const [buddyId, setBuddyId] = useState('');
  const [buddyName, setBuddyName] = useState('');
  const [busy, setBusy] = useState(false);
  const [confirmingDelete, setConfirmingDelete] = useState(false);

  const refresh = useCallback(async () => {
    try {
      const next = await api.cloud();
      setStatus(next);
      setUnavailable(false);
      if (next.state === 'authorized') {
        setBuddies(await api.buddies());
        const backups = await api.characters().catch(() => null);
        setCharacters(backups?.characters ?? []);
        setCharBackup(backups?.backup ?? null);
      } else {
        setBuddies([]);
        setCharacters([]);
        setCharBackup(null);
      }
    } catch {
      // A host built without online sync answers 503 here.
      setUnavailable(true);
    }
  }, []);

  // A login finishes in the browser with no callback, so the panel has to look rather than be
  // told. Five seconds is slow enough to be free and quick enough that a login feels finished.
  useEffect(() => {
    refresh();
    const timer = setInterval(refresh, 5000);
    return () => clearInterval(timer);
  }, [refresh]);

  if (unavailable) {
    return (
      <div class="tabpage">
        <h2>Online</h2>
        <p>Online sync is not running in this host.</p>
      </div>
    );
  }

  if (!status) {
    return (
      <div class="tabpage">
        <h2>Online</h2>
        <p>Checking…</p>
      </div>
    );
  }

  const authorized = status.state === 'authorized';

  const run = async (action: () => Promise<unknown>) => {
    setBusy(true);
    try { await action(); } finally { setBusy(false); await refresh(); }
  };

  const login = () => run(async () => {
    const result = await api.cloudLogin();
    if (result.error) { onToast(result.error); return; }
    if (result.loginUrl) {
      onToast('Finish signing in in your browser.');
      window.open(result.loginUrl, '_blank', 'noopener');
    }
  });

  const addBuddy = () => run(async () => {
    const id = Number(buddyId.replace(/\D/g, ''));
    if (!id) { onToast('A buddy id is six digits.'); return; }
    const result = await api.addBuddy(id, buddyName.trim());
    if (result.error) { onToast(result.error); return; }
    setBuddyId('');
    setBuddyName('');
    onToast(`Following ${buddyName.trim() || id}.`);
  });

  return (
    <div class="tabpage">
      <h2>Online</h2>

      {/* A development build must never be mistaken for one syncing a real collection. */}
      {status.environment === 'localdev' && (
        <p class="notice notice--warning">
          Pointed at <code>{status.host}</code>, not the live backup service.
        </p>
      )}

      <section class="settings">
        <h3>Backup</h3>

        {status.state === 'unknown' && (
          <p>
            The backup service could not be reached. Your items are safe here; nothing is being
            sent. This usually clears up by itself.
          </p>
        )}

        {status.state === 'unauthorized' && (
          <>
            <p>
              Signing in copies your collection to Item Assistant's backup service, so it survives
              this machine and can be shared with friends. It is optional, and everything works
              without it.
            </p>
            <button class="button" disabled={busy || status.optOutOfBackups} onClick={login}>
              Sign in
            </button>
            {status.pendingLoginUrl && (
              <p>
                Waiting for the browser… <a href={status.pendingLoginUrl} target="_blank" rel="noopener">
                  open the sign-in page again
                </a>
              </p>
            )}
          </>
        )}

        {authorized && (
          <>
            <p>
              Signed in as <strong>{status.user}</strong>.
              {status.pendingUploads > 0
                ? ` ${status.pendingUploads.toLocaleString()} item(s) still to upload.`
                : ' Everything is backed up.'}
              {status.pendingDeletions > 0
                && ` ${status.pendingDeletions.toLocaleString()} deletion(s) still to send.`}
            </p>
            <p class="hint">
              Uploads are paced by the server, so a large collection takes a while. Leaving this
              open is enough.
            </p>

            <label class="field field--check">
              <input
                type="checkbox"
                checked={status.usingDualComputer}
                disabled={busy}
                onChange={(e) => run(() => api.cloudSettings({
                  usingDualComputer: (e.target as HTMLInputElement).checked,
                }))}
              />
              <span>
                I play on more than one PC
                <small>
                  Syncs both ways far more often and keeps a live connection open, so items and
                  transfers cross within seconds. Leave it off on a single machine: it multiplies
                  how much this asks of the service.
                  {status.usingDualComputer && (status.liveSyncConnected
                    ? ' Live sync is connected.'
                    : ' Live sync is not connected right now.')}
                </small>
              </span>
            </label>

            <div class="field">
              <button class="button" disabled={busy} onClick={() => run(async () => {
                const result = await api.cloudLogout();
                onToast(result.message);
              })}>
                Sign out
              </button>
              <p class="hint">
                Your items stay in this collection. Buddy items are removed — they were never
                yours.
              </p>
            </div>

            {/* Destructive and irreversible on the server, so it asks twice and says what goes. */}
            <div class="field">
              {confirmingDelete ? (
                <>
                  <button class="button button--danger" disabled={busy} onClick={() => run(async () => {
                    const result = await api.cloudDeleteAccount();
                    onToast(result.error ?? result.message ?? 'Deleted.');
                    setConfirmingDelete(false);
                  })}>
                    Yes, delete my online backup
                  </button>
                  <button class="button" onClick={() => setConfirmingDelete(false)}>Cancel</button>
                  <p class="hint">
                    This deletes your account and every item in it from the server, for good.
                    The collection on this machine is not touched.
                  </p>
                </>
              ) : (
                <button class="button" onClick={() => setConfirmingDelete(true)}>
                  Delete my online backup…
                </button>
              )}
            </div>
          </>
        )}

        <label class="field field--check">
          <input
            type="checkbox"
            checked={status.optOutOfBackups}
            disabled={busy || authorized}
            onChange={(e) => run(() => api.cloudSettings({
              optOutOfBackups: (e.target as HTMLInputElement).checked,
            }))}
          />
          <span>
            I don't want any online features
            <small>Stops buddy sync and hides the sign-in. Sign out first to change this.</small>
          </span>
        </label>
      </section>

      {authorized && (
        <section class="settings">
          <h3>Buddies</h3>
          <p>
            A buddy id lets someone browse your items. Yours is{' '}
            <strong>{status.buddyId ?? '—'}</strong>. Sharing it is one-way: they see your
            collection, you see nothing of theirs unless they give you theirs too.
          </p>

          <div class="field field--row">
            <input
              class="input"
              placeholder="Buddy id"
              inputMode="numeric"
              maxLength={6}
              value={buddyId}
              onInput={(e) => setBuddyId((e.target as HTMLInputElement).value)}
            />
            <input
              class="input"
              placeholder="Nickname"
              value={buddyName}
              onInput={(e) => setBuddyName((e.target as HTMLInputElement).value)}
            />
            <button class="button" disabled={busy} onClick={addBuddy}>Follow</button>
          </div>

          {buddies.length === 0 ? (
            <p class="hint">Not following anyone.</p>
          ) : (
            <table class="table">
              <thead>
                <tr><th>Buddy</th><th>Items</th><th>In search</th><th /></tr>
              </thead>
              <tbody>
                {buddies.map((buddy) => (
                  <tr key={buddy.id}>
                    <td>{buddy.nickname || '—'} <small>({buddy.id})</small></td>
                    <td>{buddy.items.toLocaleString()}</td>
                    <td>
                      <button class="button button--small" disabled={busy} onClick={() => run(() =>
                        api.updateBuddy(buddy.id, { isHidden: !buddy.isHidden }))}>
                        {buddy.isHidden ? 'Hidden' : 'Shown'}
                      </button>
                    </td>
                    <td>
                      <button class="button button--small" disabled={busy} onClick={() => run(async () => {
                        await api.removeBuddy(buddy.id);
                        onToast(`Stopped following ${buddy.nickname || buddy.id}.`);
                      })}>
                        Unfollow
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </section>
      )}

      {authorized && (
        <section class="settings">
          <h3>Character backups</h3>
          <p class="hint">
            Grim Dawn's own save files, zipped and uploaded while the game is not running. This is
            the game's data rather than the collection — <code>iagd backup</code> covers the
            collection.
          </p>

          {charBackup && !charBackup.available && (
            <p>No Grim Dawn save folder was found, so there is nothing to back up.</p>
          )}

          {charBackup?.available && (
            <div class="field">
              <button
                class="button"
                disabled={busy || charBackup.running || charBackup.pausedForGame}
                onClick={() => run(async () => {
                  const result = await api.backupCharactersNow();
                  onToast(result.error ?? 'Backing up your character saves…');
                })}
              >
                {charBackup.running ? 'Backing up…' : 'Back up now'}
              </button>

              {/* Why the button is disabled, rather than a dead button and no explanation. */}
              {charBackup.pausedForGame && (
                <p class="hint">
                  Paused while Grim Dawn is running — a save the game is writing cannot be
                  archived safely. It resumes on its own once you close the game.
                </p>
              )}

              {charBackup.message && !charBackup.running && (
                <p class={charBackup.failed?.length ? 'notice notice--warning' : 'hint'}>
                  {charBackup.message}
                  {charBackup.failed?.length ? ` (${charBackup.failed.join(', ')})` : ''}
                </p>
              )}
            </div>
          )}

          {characters.length === 0 ? (
            <p class="hint">Nothing backed up yet.</p>
          ) : (
            <ul class="list">
              {characters.map((character) => {
                const state = downloads[character.name];
                return (
                  <li key={character.name}>
                    <button
                      class="button button--small"
                      disabled={state?.message === 'Preparing…'}
                      onClick={async () => {
                        // The link is signed and expires in five minutes, so it is fetched on
                        // the click. That is a round trip to the server, which is long enough
                        // that a button doing nothing visible reads as broken.
                        const set = (entry: { message: string; url?: string }) =>
                          setDownloads((current) => ({ ...current, [character.name]: entry }));

                        set({ message: 'Preparing…' });
                        try {
                          const result = await api.characterUrl(character.name);
                          if (!result.url) {
                            set({ message: result.error ?? 'No backup for that character.' });
                          } else if (result.opened) {
                            set({ message: 'Download started in your browser.' });
                          } else {
                            // The host has no desktop session to open it in, so this page is
                            // being viewed in a browser somewhere else. A link the user clicks
                            // beats window.open, which a popup blocker stops this long after
                            // the click that started it.
                            set({ message: 'Ready:', url: result.url });
                          }
                        } catch {
                          set({ message: 'Could not reach the backup service.' });
                        }
                      }}
                    >
                      {state?.message === 'Preparing…' ? 'Preparing…' : 'Download'}
                    </button>{' '}
                    {character.name}
                    {state && state.message !== 'Preparing…' && (
                      <small class="hint">
                        {' '}— {state.message}
                        {state.url && (
                          <>
                            {' '}
                            <a href={state.url} target="_blank" rel="noopener noreferrer">
                              save {character.name}
                            </a>
                          </>
                        )}
                      </small>
                    )}
                  </li>
                );
              })}
            </ul>
          )}
        </section>
      )}
    </div>
  );
}

function SettingsView({ onSaved, progress, status }: {
  onSaved: (message: string) => void;
  progress: MergeProgressEvent | null;
  /** The live status, for the two conditions this page is where you go to fix. */
  status: HostStatus | null;
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
      {status?.setupWarning && (
        <div class="settings__alert">
          <strong>No Proton prefix, so nothing can be looted.</strong> {status.setupWarning}{' '}
          The prefix is the folder Steam keeps Grim Dawn's Windows installation in, and the hook
          writes what it captures into a directory inside it — without one there is no channel
          between the game and this window. Set it under Paths below.
        </div>
      )}
      {status?.hookWarning && (
        <div class="settings__alert">
          <strong>The hook's settings file could not be written.</strong> {status.hookWarning}
          {' '}Until it can be, the hook falls back to the shared-memory channel that does not
          work under Proton, and captures nothing without saying so. Check that the prefix below
          is the right one and that it is writable.
        </div>
      )}
      {settings.hook !== null && !settings.hook.wineModeEnabled && (
        <div class="settings__alert">
          <strong>The hook is not in Wine mode.</strong> It will not capture anything. This is
          normally repaired automatically on startup — if it persists, the bridge settings file
          is not writable.
        </div>
      )}
      {settings.hook !== null && !settings.hook.gameDataParsed && (
        <div class="settings__alert">
          <strong>The hook has not been told Grim Dawn's data was read.</strong> It refuses to
          loot anything in that state, and says so only in the game — "Item not looted / Grim
          Dawn not parsed" over the stash. Reading the item database sets it: use Load Database
          on the Grim Dawn tab. Looting starts working straight away, without restarting the
          game.
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

        <PathSetting
          label="Proton prefix"
          value={settings.prefixDir ?? ''}
          placeholder={settings.resolvedPrefixDir ?? 'auto-discovered'}
          directory
          title="Choose the Proton prefix for Grim Dawn"
          canBrowse={canBrowse}
          disabled={saving}
          onChange={(path) => save({ prefixDir: path.trim() === '' ? null : path })}
        />

        <p class="settings__note settings__note--after">
          The Proton prefix is how the hook is reached — loot arrives through a folder inside it,
          and without one nothing can be captured. Discovery looks where Steam puts it
          (<code>steamapps/compatdata/219990</code>); name it here if the game runs from
          somewhere else. Either that folder or the <code>pfx</code> inside it will do.
        </p>

        <p class="settings__note settings__note--after">
          A changed collection or Proton prefix takes effect when the application restarts.
          {!canBrowse && ' Type paths here — a browser cannot open a file chooser on the host.'}
        </p>
      </div>

      <BackupFolder onOpened={onSaved} />

    </section>
  );
}

/**
 * Upstream's Settings tab has an "Open Data Folder" button (SettingsController.cs); this is its
 * counterpart, pointed at the one thing this port keeps there worth finding — a database backup
 * taken before every risky operation (import, merge, stash import, first opening someone else's
 * collection). There is no "View Logs" alongside it: this port logs to stdout/stderr rather
 * than a file, so there is nothing for that button to open.
 */
function BackupFolder({ onOpened }: { onOpened: (message: string) => void }) {
  return (
    <div class="settings__group">
      <h3>Backups</h3>
      <p class="settings__note">
        A copy of the collection database, saved before an import, a merge, a stash import, or
        opening an existing collection for the first time. <code>iagd backup</code> lists them
        from a terminal; this opens the folder they live in.
      </p>
      <button
        class="button"
        onClick={async () => {
          const result = await api.openFolder('backups');
          // No xdg-open, or no desktop session: there is no file manager to hand this to, so
          // the path is shown as text instead — the same fallback Support.tsx uses for links.
          if (!result.opened) {
            onOpened(result.path ? `Backups are in ${result.path}` : (result.error ?? 'Could not open the backup folder.'));
          }
        }}
      >
        Open backup folder
      </button>
    </div>
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
  const [filters, setFilters] = useState<ItemFilters>({
    mod: '', hardcore: false,
    minLevel: DEFAULT_MIN_LEVEL, maxLevel: DEFAULT_MAX_LEVEL,
  });
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
  // Bumped when the host finishes work that changes what an item looks like, to re-fetch a
  // list that would otherwise keep showing what it was given before the work ran.
  const [dataVersion, setDataVersion] = useState(0);
  // Pushed by the host while a merge runs. Held here rather than in the settings view because
  // that is where the event socket already is; a merge must not need a second connection.
  const [mergeProgress, setMergeProgress] = useState<MergeProgressEvent | null>(null);
  // Transfers outlive the panel and survive a reload, so they live here rather than in it.
  const [transfers, setTransfers] = useState<Record<number, TransferState>>({});
  // The card whose copies are being compared, or null. Held here rather than in the card so it
  // survives the list re-rendering underneath it.
  const [comparing, setComparing] = useState<ItemCardData | null>(null);
  const searchRef = useRef<HTMLInputElement>(null);
  const queryRef = useRef(query);
  queryRef.current = query;
  // The event handler below is registered once and cannot close over the list. Everything that
  // changes the list is a functional update; this is only read to decide what a change means.
  const itemsRef = useRef(items);
  itemsRef.current = items;
  // Whether anything beyond the search box is narrowing the list — used to decide if a newly
  // looted item can be folded into the grid without contradicting the filters.
  const filteredRef = useRef(false);
  // The level boxes always hold a number now, so "is anything narrowing the list" has to ask
  // whether they hold anything other than their defaults — otherwise an untouched client
  // reports itself as filtered and says "nothing matching those filters" on an empty
  // collection, where "no items yet" is the truth.
  filteredRef.current =
    !isDefaultLevels(filters)
    || Object.entries(filters).some(([key, value]) =>
      key !== 'minLevel' && key !== 'maxLevel'
      && value !== undefined && value !== '' && value !== false && value !== 0);

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
  }, [query, JSON.stringify(filters), tab, dataVersion]);

  /*
   * Reload once the host finishes reading Grim Dawn's data or analysing the collection.
   *
   * Both rewrite what a card is made of — names, icons, rarity colours, every computed stat
   * line — and a list fetched before they ran keeps whatever it was given. That is a stale
   * screen presenting as a bug: relics whose icons had just been read still showed the
   * missing-icon placeholder, while the collection tab, opened afterwards, showed them.
   *
   * Watching the transition rather than the flag, so a client that opens mid-parse still
   * refreshes when it ends.
   */
  const busy = Boolean(status?.parsingGameData || status?.analysing);
  const wasBusy = useRef(busy);
  useEffect(() => {
    if (wasBusy.current && !busy) {
      setDataVersion((v) => v + 1);
      // A parse can add a mod, and both passes change what the filters can match.
      api.mods().then(setMods).catch(() => {});
      api.filters().then(setCatalogue).catch(() => {});
    }
    wasBusy.current = busy;
  }, [busy]);

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

        case 'itemRemoved': {
          const removed = event.data.id;
          // What left is one *item*, which is not the same as one card: a card standing for
          // several identical copies keeps its place and reports one fewer. Upstream reduces
          // the same way, in App.reduceItemCount, and for the same reason — transferring one
          // of five must not take the other four off the screen.
          const card = itemsRef.current.find((c) => c.duplicates.includes(removed));
          const remaining = card ? card.duplicates.filter((id) => id !== removed) : [];

          setItems((current) => current.flatMap((c) => {
            if (!c.duplicates.includes(removed)) return [c];
            const duplicates = c.duplicates.filter((id) => id !== removed);
            if (duplicates.length === 0) return [];
            return [{
              ...c,
              copies: duplicates.length,
              duplicates,
              // A card is drawn from one specific row. When that row is the one that left, the
              // card now speaks for the survivors and has to become one of them — otherwise its
              // transfer link and its detail panel both point at a row that no longer exists.
              item: c.item.id === removed ? { ...c.item, id: duplicates[0] } : c.item,
            }];
          }));

          // Items always drop by one; cards only when the last copy of one is gone. A removal
          // for something not on screen says nothing about the card count — upstream does
          // nothing at all in that case (reduceItemCount logs and returns).
          setTotalItems((t) => Math.max(0, t - 1));
          if (card && remaining.length === 0) setTotal((t) => Math.max(0, t - 1));

          // The stat lines belong to the row that left, so the promoted copy brings its own.
          // Everything the merge key covers — name, level, icon, rarity — is shared across the
          // group, but the rolled values are exactly what is not.
          if (card && remaining.length > 0 && card.item.id === removed) {
            api.details([remaining[0]])
              .then(([copy]) => {
                if (!copy) return;
                setItems((current) => current.map((c) => (
                  c.item.id === copy.item.id
                    ? { ...c, item: copy.item, stats: copy.stats, skill: copy.skill }
                    : c
                )));
              })
              .catch(() => {});
          }

          setSelected((current) => (current === removed ? null : current));
          break;
        }

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
        // Innermost thing first: the comparison modal sits over the list, so Escape should
        // close that rather than the panel behind it.
        if (comparing) { setComparing(null); return; }
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
  }, [query, comparing]);

  useEffect(() => {
    if (!toast) return;
    const handle = setTimeout(() => setToast(null), 4000);
    return () => clearTimeout(handle);
  }, [toast]);

  const transferItems = async (ids: number[]) => {
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

  /**
   * What a card's transfer links do.
   *
   * "Transfer all" sends every copy the card stands for. The other link sends the item when the
   * card is one item, and otherwise opens the comparison modal — because a card standing for
   * several copies does not say which of them it would send, and they are not interchangeable:
   * same records, different rolls. Upstream draws exactly this fork, in Item.tsx, where the
   * label changes to "Compare & Transfer" once there is more than one.
   */
  const transferFromCard = (card: ItemCardData, all: boolean) => {
    if (all) transferItems(card.duplicates);
    else if (card.copies > 1) setComparing(card);
    else transferItems([card.item.id]);
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
                      ['components', 'Components'],
                      ['help', 'Help'],
                      ['support', 'Support'],
                    ] as [View, string][]).map(([value, label]) => (
                      <a
                        key={value}
                        class={view === value ? 'webnav--active' : ''}
                        onClick={() => setView(value)}
                      >
                        {label}
                      </a>
                    ))}
                    {/* Upstream's nav also carries Discord and Patreon links, and opens its
                        Components entry on its author's website. This port reproduces none of
                        the three and should not: it is an unaffiliated port, and those are
                        somebody else's community, funding and site. Sending this project's
                        users there would imply a connection that does not exist and put
                        support requests for this code in front of people who did not write it.
                        Components is a page here instead — the data is Grim Dawn's, and this
                        client already reads it. Do not add the links back. */}
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
                  {view === 'components' && <Components />}

                  {view === 'help' && <Help />}

                  {view === 'support' && <Support />}

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
                          onTransfer={(all) => transferFromCard(card, all)}
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

            {/* Which of several identical copies to send. Picking one closes it, as upstream's
                does; so does Escape, and a click outside. */}
            {comparing !== null && (
              <ItemComparer
                card={comparing}
                transfers={transfers}
                onClose={() => setComparing(null)}
                onTransfer={(id) => { setComparing(null); transferItems([id]); }}
              />
            )}

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

        {tab === 'settings' && (
          <SettingsView onSaved={setToast} progress={mergeProgress} status={status} />
        )}

        {tab === 'online' && <OnlineView onToast={setToast} />}

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
                    : !status?.gameDir
                      // The button is disabled here, and a disabled button with no reason beside
                      // it is the whole of what a tester saw: "Load Database cannot be clicked",
                      // templates at zero, and nothing on screen connecting the two.
                      ? 'Grim Dawn was not found — set the game folder in Settings first'
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
