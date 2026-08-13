#!/usr/bin/env python3
"""Turns upstream's help page into data this port's Help tab can render.

Upstream keeps its help as a 500-line TSX file: thirty entries, each a title, a tag, a
Help/Informational badge and a body of small JSX. This reads that file out of the pinned
submodule and writes JSON.

Generated, never committed — the same rule the hook and the injector follow. What this
repository carries is the porting work and the editorial layer (help-notes.json): which entries
apply to a Linux client, and what to add where upstream's answer points at a Windows path or a
button this port does not have.

    extract-help.py <Help.tsx> <notes.json> <out.json> [asset-dir]

The body conversion is deliberately narrow. Upstream's bodies use a closed set of constructs —
<br/>, <i>, <b>, <span className="attention">, <img>, a numbered-list helper and one shared
fragment — so each is handled explicitly and anything unrecognised is reported rather than
guessed at. A silent mistranslation would put wrong instructions in front of a user.
"""

import json
import re
import shutil
import sys
from pathlib import Path


def fail(message):
    print(f"error: {message}", file=sys.stderr)
    sys.exit(1)


def numbered_list(block):
    """Upstream's toNumberedList: a backtick block, one list item per line."""
    items = [line.strip() for line in block.strip().split("\n") if line.strip()]
    return "<ol>" + "".join(f"<li>{escape(item)}</li>" for item in items) + "</ol>"


def escape(text):
    return text.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def find_entries(source):
    """Each entry of the helpEntries array, as (title, tag, type, body).

    Sliced on `title:` boundaries rather than matched as a whole. A single pattern cannot do it:
    the bodies contain braces of their own — `{toNumberedList(...)}` — so any expression ending
    at a closing brace stops inside the body, and the badge that follows is then lost. Which is
    how every "Help" badge silently became "none" the first time.
    """
    starts = [m.start() for m in re.finditer(r"^\s*title:\s*`", source, re.MULTILINE)]
    if not starts:
        return []

    entries = []
    bounds = starts + [source.index("] as IHelpEntry[]")
                       if "] as IHelpEntry[]" in source else len(source)]

    for index, start in enumerate(starts):
        slice_ = source[start:bounds[index + 1]]

        title = re.search(r"title:\s*`([^`]*)`", slice_)
        tag = re.search(r"tag:\s*'([^']*)'", slice_)
        body_at = re.search(r"body:\s*\(\)\s*=>\s*", slice_)
        if not (title and tag and body_at):
            continue

        type_at = re.search(r",\s*type:\s*IHelpEntryType\.(Informational|Help)", slice_)
        body_end = type_at.start() if type_at else len(slice_)
        body = slice_[body_at.end():body_end].rstrip().rstrip(",").rstrip()
        # Trailing "}," of the object literal, when the entry carried no badge.
        body = re.sub(r"\}\s*,?\s*$", "", body).rstrip() if not type_at else body

        entries.append({
            "title": title.group(1).strip(),
            "tag": tag.group(1),
            "type": (type_at.group(1) if type_at else "none").lower(),
            "body": body,
        })

    return entries


def convert(body, shared, images):
    """JSX body source -> HTML, one construct at a time."""
    html = body.strip()

    # The shared "close GD, load the database" fragment, inlined where it is referenced.
    html = html.replace("{typicalParseDbMessage}", shared)

    # {toNumberedList(`...`)} -> <ol>
    html = re.sub(
        r"\{toNumberedList\(`(.*?)`\)\}",
        lambda m: numbered_list(m.group(1)),
        html,
        flags=re.DOTALL,
    )

    # Images live beside upstream's Help.tsx and are copied next to the generated JSON.
    def image(match):
        name = match.group(1).lstrip("./")
        images.add(name)
        return f'<img src="/help/{name}" alt="">'

    html = re.sub(r'<img\s+src="([^"]+)"\s*/?>', image, html)

    # The outer <div> wrapper carries no meaning.
    html = re.sub(r"^<div>", "", html)
    html = re.sub(r"</div>$", "", html.strip())

    html = html.replace('<span className="attention">', '<span class="attention">')
    html = re.sub(r"<br\s*/?>", "<br>", html)

    # Anything left in braces is JSX this converter does not understand.
    leftover = re.search(r"\{[^}]*\}", html)
    if leftover:
        return None, f"unhandled JSX expression: {leftover.group(0)[:60]}"

    # A hidden span of search keywords; the styling does not survive, so drop the element.
    html = re.sub(r'<span style="display: none;">.*?</span>', "", html, flags=re.DOTALL)

    unknown = set(re.findall(r"<([a-zA-Z]+)", html)) - {
        "br", "i", "b", "ol", "ul", "li", "span", "img", "u", "em", "strong", "p", "h2", "h3"
    }
    if unknown:
        return None, f"unhandled tag(s): {', '.join(sorted(unknown))}"

    # Collapse the source's indentation without touching the markup.
    html = re.sub(r"\s*\n\s*", " ", html)
    return re.sub(r"\s{2,}", " ", html).strip(), None


def main():
    if len(sys.argv) < 4:
        fail("usage: extract-help.py <Help.tsx> <notes.json> <out.json> [asset-dir]")

    help_tsx, notes_file, out_file = (Path(p) for p in sys.argv[1:4])
    asset_dir = Path(sys.argv[4]) if len(sys.argv) > 4 else None

    # A tree without the submodule still has to build. The page then says where its content
    # comes from and how to get it, which beats failing the whole UI build over documentation.
    if not help_tsx.is_file():
        out_file.parent.mkdir(parents=True, exist_ok=True)
        out_file.write_text("[]\n", encoding="utf-8")
        print(f"help: upstream's page not found at {help_tsx}", file=sys.stderr)
        print("help: run 'git submodule update --init --recursive' to include it",
              file=sys.stderr)
        return

    source = help_tsx.read_text(encoding="utf-8")
    notes = json.loads(notes_file.read_text(encoding="utf-8")) if notes_file.is_file() else {}
    excluded = notes.get("exclude", {})
    added = notes.get("notes", {})

    # The shared fragment, converted once.
    shared_match = re.search(
        r"const typicalParseDbMessage = <Fragment>\{toNumberedList\(`(.*?)`\)\}(.*?)</Fragment>",
        source, re.DOTALL)
    if not shared_match:
        fail("could not find typicalParseDbMessage — upstream's help page has changed shape")
    shared = numbered_list(shared_match.group(1)) + shared_match.group(2).strip()

    matches = find_entries(source)
    if not matches:
        fail("no help entries found — upstream's help page has changed shape")

    images, entries, skipped, problems = set(), [], [], []

    for match in matches:
        tag = match["tag"]
        if tag in excluded:
            skipped.append(tag)
            continue

        html, problem = convert(match["body"], shared, images)
        if problem:
            problems.append(f"{tag}: {problem}")
            continue

        entries.append({
            "tag": tag,
            "title": match["title"],
            "type": match["type"],
            "html": html,
            "note": added.get(tag),
        })

    # This port's own entries, for the questions upstream answers with a Windows path or a
    # button that is not here. Ours, so they carry no upstream text at all.
    for own in notes.get("own", []):
        entries.append({**own, "type": own.get("type", "none"), "own": True})

    if asset_dir and images:
        asset_dir.mkdir(parents=True, exist_ok=True)
        for name in sorted(images):
            source_image = help_tsx.parent / name
            if source_image.is_file():
                shutil.copy2(source_image, asset_dir / name)
            else:
                problems.append(f"image not found: {name}")

    out_file.parent.mkdir(parents=True, exist_ok=True)
    out_file.write_text(json.dumps(entries, indent=1, ensure_ascii=False) + "\n",
                        encoding="utf-8")

    print(f"help: {len(entries)} entries "
          f"({len(skipped)} not applicable, {len(images)} image(s))")

    # A body this cannot translate is a missing answer, not a broken build: the rest of the
    # page is still worth having, and the message says exactly which entry to look at.
    for problem in problems:
        print(f"help: skipped {problem}", file=sys.stderr)


if __name__ == "__main__":
    main()
