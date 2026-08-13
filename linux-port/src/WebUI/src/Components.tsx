import { useEffect, useState } from 'preact/hooks';
import { api, ComponentEntry } from './api';
import { StatLine } from './GrimText';

/**
 * Every component in the game, with what it grants and where it goes.
 *
 * **Upstream has no such page.** Its "Components" nav entry opens
 * grimdawn.evilsoft.net/enchantments/ in a browser — the same author's website, which this port
 * does not send anyone to, for the reason its nav carries no Discord or Patreon link either.
 *
 * Everything the page needs is in Grim Dawn's own data, which this client already reads, so it
 * is built here rather than linked away. The stat lines go through the renderer an item's card
 * uses, so a component reads the same way an item does.
 */

/** The game's slot names, in the order a player reads their character sheet. */
const SLOT_LABELS: Record<string, string> = {
  head: 'Helm',
  shoulders: 'Shoulders',
  chest: 'Chest',
  hands: 'Gloves',
  waist: 'Belt',
  legs: 'Pants',
  feet: 'Boots',
  amulet: 'Amulet',
  medal: 'Medal',
  ring: 'Ring',
  offhand: 'Off-hand',
  shield: 'Shield',
  axe: 'Axe',
  axe2h: 'Axe (2h)',
  dagger: 'Dagger',
  mace: 'Mace',
  mace2h: 'Mace (2h)',
  scepter: 'Scepter',
  sword: 'Sword',
  sword2h: 'Sword (2h)',
  spear2h: 'Spear (2h)',
  ranged1h: 'Gun (1h)',
  ranged2h: 'Gun (2h)',
};

const SLOT_ORDER = Object.keys(SLOT_LABELS);

function slotNames(slots: string[]) {
  return [...slots]
    .sort((a, b) => SLOT_ORDER.indexOf(a) - SLOT_ORDER.indexOf(b))
    .map((slot) => SLOT_LABELS[slot] ?? slot);
}

export function Components() {
  const [query, setQuery] = useState('');
  const [entries, setEntries] = useState<ComponentEntry[]>([]);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    // Debounced, like the item search: typing should not fire a request per keystroke.
    const handle = setTimeout(() => {
      setLoading(true);
      api.components(query)
        .then(setEntries)
        .catch(() => setEntries([]))
        .finally(() => setLoading(false));
    }, 200);
    return () => clearTimeout(handle);
  }, [query]);

  return (
    <div class="components">
      <input
        class="components__search"
        type="search"
        placeholder="Search components…"
        value={query}
        onInput={(e) => setQuery((e.target as HTMLInputElement).value)}
      />

      {!loading && entries.length === 0 && (
        <p class="components__empty">
          {query
            ? `Nothing matching “${query}”.`
            : "No components yet — they are read from Grim Dawn's data when the client parses it."}
        </p>
      )}

      <div class="components__grid">
        {entries.map((component) => (
          <article class="component" key={component.baseRecord}>
            <div class="component__head">
              <div class="component__icon">
                {component.icon
                  ? <img src={api.iconUrl(component.icon)!} alt="" loading="lazy" />
                  : <div class="component__icon--missing" />}
              </div>
              <div>
                <div class="component__name">{component.name}</div>
                <div class="component__meta">
                  {component.levelRequirement > 0 && <>Level {component.levelRequirement}</>}
                  {component.numOwned > 0 && (
                    <span class="component__owned">{component.numOwned} socketed</span>
                  )}
                </div>
              </div>
            </div>

            <ul class="component__stats">
              {component.stats.map((stat, index) => (
                <li key={index} class={`stat ${stat.section ? `stat--${stat.section}` : ''}`}>
                  <StatLine line={stat} />
                </li>
              ))}
            </ul>

            {component.skill && (
              <div class="component__skill">
                <div class="component__skill-name">
                  {component.skill.name ?? 'Grants a skill'}
                </div>
                {component.skill.description && (
                  <div class="component__skill-text">{component.skill.description}</div>
                )}
              </div>
            )}

            {/* What the record itself says it may go into, rather than its FileDescription,
                which is developer text and sometimes describes a rename. */}
            <div class="component__slots">
              {slotNames(component.slots).map((slot) => (
                <span key={slot} class="component__slot">{slot}</span>
              ))}
            </div>
          </article>
        ))}
      </div>
    </div>
  );
}
