import { describe, expect, it } from "vitest";
import { TEXT_BUDGET, fitText } from "../src/textBudget.js";

describe("fitText", () => {
  it("leaves text within the budget alone", () => {
    expect(fitText("a\nb", "Narrow it.", 10)).toBe("a\nb");
    expect(TEXT_BUDGET).toBe(60_000);
  });

  it("cuts at a line end and ends with the hint", () => {
    const text = Array.from({ length: 100 }, (_, i) => `line ${i}`).join("\n");
    const t = fitText(text, "Use filter.", 200);
    expect(t.length).toBeLessThanOrEqual(200);
    const lines = t.split("\n");
    expect(lines.at(-1)).toMatch(/^… cut at \d+ of \d+ characters\. Use filter\.$/);
    // every kept line is whole
    for (const l of lines.slice(0, -1)) expect(l).toMatch(/^line \d+$/);
  });

  it("cuts a single long line hard when it has no line end", () => {
    const t = fitText("x".repeat(1000), "Read less.", 200);
    expect(t.length).toBeLessThanOrEqual(200);
    expect(t).toMatch(/^x+\n… cut at \d+ of 1000 characters\. Read less\.$/);
  });
});
