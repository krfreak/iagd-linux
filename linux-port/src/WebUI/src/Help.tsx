import { useState } from 'preact/hooks';

/**
 * Upstream's help page.
 *
 * The entries are upstream's, read out of the pinned submodule at build time by
 * scripts/extract-help.py and written to src/generated/help.json — generated, gitignored, and
 * regenerated whenever upstream's page changes. This repository carries the porting work and
 * the editorial layer (help-notes.json), not somebody else's prose.
 *
 * Three kinds of entry end up here:
 *
 *   * upstream's, unchanged;
 *   * upstream's with a note, where the answer holds but the path or the button does not;
 *   * this port's own, for the questions only it can answer.
 *
 * Sixteen of upstream's thirty are left out, because their answer would be wrong here — "run IA
 * as administrator", Windows anti-virus, and the features this port has not implemented. A help
 * page that confidently answers the wrong question is worse than a short one.
 *
 * Upstream ends its page with a link to its Discord. This port does not reproduce it: it is an
 * unaffiliated port, and that is somebody else's community.
 */

interface HelpEntry {
  tag: string;
  title: string;
  type: 'help' | 'informational' | 'none';
  html: string;
  note?: string | null;
  own?: boolean;
}

// Written by the build (scripts/extract-help.py, run from `make ui`). Always present, and empty
// when the submodule holding upstream's page is not checked out — the page then says so rather
// than the build failing.
import generated from './generated/help.json';

const entries = generated as HelpEntry[];

/** Upstream's search: match the title, the tag, or anything in the body. */
function matches(entry: HelpEntry, needle: string) {
  if (!needle.trim()) return true;
  const haystack = `${entry.title} ${entry.tag} ${entry.html} ${entry.note ?? ''}`;
  return haystack.toLowerCase().includes(needle.trim().toLowerCase());
}

export function Help() {
  const [search, setSearch] = useState('');
  const [open, setOpen] = useState<string | null>(null);

  const shown = entries.filter((entry) => matches(entry, search));

  if (entries.length === 0) {
    return (
      <div class="help">
        <p class="help__empty">
          The help page is built from Grim Dawn Item Assistant's own, which is not part of this
          repository. Run <code>make ui</code> with the submodules checked out to generate it.
        </p>
      </div>
    );
  }

  return (
    <div class="help">
      <input
        class="help__search"
        type="search"
        placeholder="Search help…"
        value={search}
        onInput={(e) => setSearch((e.target as HTMLInputElement).value)}
      />

      {shown.length === 0 && <p class="help__empty">Nothing matching “{search}”.</p>}

      {shown.map((entry) => {
        const expanded = open === entry.tag || search.trim() !== '';
        return (
          <section key={entry.tag} class="help__entry">
            <button
              class="help__head"
              onClick={() => setOpen(open === entry.tag ? null : entry.tag)}
            >
              <span class="help__title">{entry.title}</span>
              {entry.own && <span class="help__badge help__badge--port">This port</span>}
              {entry.type === 'help' && <span class="help__badge help__badge--help">Help</span>}
              {entry.type === 'informational' && (
                <span class="help__badge help__badge--info">Informational</span>
              )}
            </button>

            {expanded && (
              <div class="help__body">
                {/* Upstream's own markup, converted at build time from its JSX. */}
                <div dangerouslySetInnerHTML={{ __html: entry.html }} />
                {entry.note && (
                  <p class="help__note">
                    <strong>On Linux:</strong> {entry.note}
                  </p>
                )}
              </div>
            )}
          </section>
        );
      })}
    </div>
  );
}
