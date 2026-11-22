/**
 * One limit on the text of every tool result, applied once in the server's `run()`, so no
 * formatter needs its own cap and a large answer cannot flood Claude's context.
 */

export const TEXT_BUDGET = 60_000;

/**
 * `text` unchanged when it fits; otherwise cut at the last line end that leaves room for a
 * closing line saying how much was cut and `hint` (how to narrow the call).
 */
export function fitText(text: string, hint: string, max: number = TEXT_BUDGET): string {
  if (text.length <= max) return text;
  const tail = (kept: number) => `\n… cut at ${kept} of ${text.length} characters. ${hint}`;
  // `kept` is at most `max`, so this is the longest the tail can be.
  const room = Math.max(0, max - tail(max).length);
  let cut = text.lastIndexOf("\n", room);
  if (cut <= 0) {
    cut = room;
    // Do not split a surrogate pair.
    const c = text.charCodeAt(cut - 1);
    if (c >= 0xd800 && c <= 0xdbff) cut--;
  }
  return text.slice(0, cut) + tail(cut);
}
