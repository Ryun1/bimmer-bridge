import { readFileSync, readdirSync, statSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import { DEFAULT_FIXTURES, scrubRecording } from "../scrub-fixtures.ts";

// Every committed recording must already be clean: if the scrubber would change anything in
// one, an identifier was committed (or the scrubber's rules grew and the fixture needs a re-scrub).
const recordings = readdirSync(DEFAULT_FIXTURES)
  .filter((d) => statSync(join(DEFAULT_FIXTURES, d)).isDirectory())
  .flatMap((d) =>
    readdirSync(join(DEFAULT_FIXTURES, d))
      .filter((f) => f.endsWith(".json") && f !== "scenario.json")
      .map((f) => join(d, f)),
  )
  .sort();

describe("committed fixtures", () => {
  it("there are recordings to check", () => {
    expect(recordings.length).toBeGreaterThan(0);
  });

  it.each(recordings)("%s needs no scrubbing", (rel) => {
    const raw: unknown = JSON.parse(readFileSync(join(DEFAULT_FIXTURES, rel), "utf8"));
    const { replacements } = scrubRecording(rel, raw);
    expect(replacements).toEqual([]);
  });
});
