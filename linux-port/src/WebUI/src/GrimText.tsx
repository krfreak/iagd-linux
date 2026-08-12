import { JSX } from 'preact';

/**
 * Grim Dawn marks up tooltip text with `^` plus a single letter, switching colour until the
 * next marker. The hook captures those lines verbatim, so an item name arrives as
 * `^PMythical ^BPlagueborne Revolver` and a stat as `27-40 ^LAcid Damage^S`.
 *
 * Stripping the codes loses real information — rarity and damage type are conveyed by
 * colour alone — so they are rendered rather than removed.
 */
const COLOURS: Record<string, string> = {
  k: '#101010', // black
  b: '#5599ff', // blue — epic
  g: '#33cc55', // green — magic affixes
  r: '#ee4444', // red
  y: '#ddcc44', // yellow
  w: '#e8e8e8', // white — base item name
  o: '#ee9944', // orange — legendary
  p: '#bb77ee', // purple — mythical
  m: '#88ddcc', // teal
  l: '#99dd66', // light green — damage types
  v: '#dd77bb', // violet
  s: '',        // reset to default
  n: '',        // reset to default
  x: '',        // reset to default
};

interface Segment {
  text: string;
  colour: string;
}

export function parseGrimText(input: string): Segment[] {
  const segments: Segment[] = [];
  let colour = '';
  let buffer = '';

  for (let i = 0; i < input.length; i++) {
    if (input[i] === '^' && i + 1 < input.length) {
      if (buffer) segments.push({ text: buffer, colour });
      buffer = '';
      colour = COLOURS[input[i + 1].toLowerCase()] ?? '';
      i++; // consume the colour letter
      continue;
    }
    buffer += input[i];
  }
  if (buffer) segments.push({ text: buffer, colour });

  return segments;
}

/** Renders a Grim Dawn markup string with its colours intact. */
export function GrimText({ text }: { text: string }): JSX.Element {
  const segments = parseGrimText(text);
  return (
    <>
      {segments.map((segment, index) => (
        <span key={index} style={segment.colour ? { color: segment.colour } : undefined}>
          {segment.text}
        </span>
      ))}
    </>
  );
}

/**
 * A captured tooltip line, rendered the way upstream renders it.
 *
 * Upstream does *not* colour these by the game's `^` codes: it drops them and colours the whole
 * line by its row type instead, keeping only `^E` and `^H`, which mark the stat label and the
 * number at the end of a line. Following that here rather than colouring every keyword is
 * deliberate — the two tools have to show the same item the same way, and upstream's rendering
 * is the one players know.
 */
export function ReplicaLine({ text }: { text: string }): JSX.Element {
  const parts: JSX.Element[] = [];
  let letter = '';
  let buffer = '';

  const flush = () => {
    if (!buffer) return;
    parts.push(
      <span key={parts.length} class={letter ? `letter-${letter}` : undefined}>{buffer}</span>,
    );
    buffer = '';
  };

  for (let i = 0; i < text.length; i++) {
    if (text[i] === '^' && i + 1 < text.length) {
      flush();
      const code = text[i + 1].toUpperCase();
      letter = code === 'E' || code === 'H' ? code : '';
      i++;
      continue;
    }
    buffer += text[i];
  }
  flush();

  return <>{parts}</>;
}

/** Plain text, for titles and anywhere colour would be noise. */
export function stripGrimText(input: string): string {
  return parseGrimText(input)
    .map((segment) => segment.text)
    .join('')
    .trim();
}
