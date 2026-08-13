import { JSX } from 'preact';
import { ItemStatLine } from './api';

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

/**
 * One tooltip line, drawn the way upstream draws that kind of line.
 *
 * Upstream has two renderers and picks between them by where the line came from, which is the
 * whole of the colour scheme:
 *
 *   * a line **Grim Dawn drew** goes through ReplicaStat.tsx and is coloured by its row type —
 *     type 34 is the "Granted Skills" heading, 19 the level requirement, 27 a component name.
 *   * a line **computed from the game database** goes through ItemStat.tsx, which has no type to
 *     work with and splits the line instead: the leading value in one colour, what it applies to
 *     in another, and a modified skill's name in a third.
 *
 * This port had only the first, so every computed line — which is nearly every line, since only
 * items looted with the hook attached have a captured tooltip — came out one flat colour.
 */
export function StatLine({ line }: { line: ItemStatLine }): JSX.Element {
  if (line.modifier === null && line.label === null) {
    return <ReplicaLine text={line.text} />;
  }

  return (
    <>
      {line.modifier && <span class="stat__modifier">{line.modifier}</span>}
      {line.modifier && ' '}
      {line.label && <span class="stat__label">{line.label}</span>}
      {/*
        Upstream draws the skill apart from the label and hangs its tier on a tooltip. The
        separating space is explicit: upstream inherits one from the placeholder it replaced,
        and the label is trimmed here, so without this the line reads "+2 toSigil of Consumption".
      */}
      {line.skill && line.label && ' '}
      {line.skill && (
        <span class="stat__skill" title={line.extras ?? undefined}>{line.skill}</span>
      )}
    </>
  );
}

/** Plain text, for titles and anywhere colour would be noise. */
export function stripGrimText(input: string): string {
  return parseGrimText(input)
    .map((segment) => segment.text)
    .join('')
    .trim();
}
