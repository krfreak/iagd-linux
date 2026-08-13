import { api } from './api';

/**
 * Where to send anyone who wants to support the work.
 *
 * Item Assistant is Marius Andersen's. This is an unaffiliated Linux port of it, and whoever
 * maintains the port did not write the tool — so the support this page points at is his, not
 * theirs. That is the whole content of the page, and the reason it exists.
 *
 * The Discord is deliberately absent, and that is not an oversight: sending a Linux port's users
 * into upstream's community puts support requests for code its maintainer did not write in front
 * of him, which a page called Support would make more likely rather than less.
 *
 * Links go through the host (POST /api/open, allowlisted to exactly these three) because the app
 * window is a WebKitGTK view with no external-link handling — an ordinary anchor would navigate
 * the client itself onto the page and leave no way back. The address is shown as text either
 * way, so it can be read, copied, or ignored.
 */

interface Link {
  url: string;
  label: string;
  detail: string;
}

const LINKS: Link[] = [
  {
    url: 'https://grimdawn.evilsoft.net',
    label: 'Item Assistant',
    detail: 'The original tool, its documentation and its downloads.',
  },
  {
    url: 'https://github.com/marius00/iagd',
    label: 'The source on GitHub',
    detail: 'Everything this port is a port of. Issues about Grim Dawn Item Assistant itself belong here.',
  },
  {
    url: 'https://www.patreon.com/itemassistant',
    label: 'Support the author',
    detail: 'If this client has been useful to you, the person to thank for it is Marius.',
  },
];

export function Support() {
  const open = (url: string) => {
    api.open(url).then((result) => {
      // No xdg-open, or no desktop session: let the browser have a go instead of doing nothing.
      if (!result.opened) window.open(url, '_blank', 'noreferrer');
    }).catch(() => window.open(url, '_blank', 'noreferrer'));
  };

  return (
    <div class="support">
      <h2>This is a port. The tool is somebody else's work.</h2>

      <p>
        Grim Dawn Item Assistant was written by <strong>Marius Andersen</strong>. Everything this
        client does — the item hook, the stat engine, the filters, the schema your collection is
        stored in — is his design, ported to Linux here. This project is not affiliated with him,
        speaks for him nowhere, and takes no support of its own.
      </p>

      <p>
        So if you get value out of this, send it upstream. The porting work is freely given and
        wants nothing back; the tool it is built on took years of someone else's evenings.
      </p>

      <div class="support__links">
        {LINKS.map((link) => (
          <button key={link.url} class="support__link" onClick={() => open(link.url)}>
            <span class="support__label">{link.label}</span>
            <span class="support__detail">{link.detail}</span>
            <span class="support__url">{link.url}</span>
          </button>
        ))}
      </div>

      <p class="support__note">
        Bugs in <em>this</em> client — anything Linux, Proton or Wine — are the port's problem and
        should not go to him. Nothing on this page reports anything anywhere; the links open in
        your browser and that is all they do.
      </p>
    </div>
  );
}
