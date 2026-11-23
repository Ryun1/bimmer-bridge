// Every relative link in the repo's markdown resolves: the file (or folder) exists and, for a
// `#anchor`, the target file has a heading with that GitHub-style slug (or an explicit
// `<a id="...">`). Only inline `](target)` links are checked (not reference-style links or
// HTML `href`s). Links to the web are not checked. docs/superpowers/ holds point-in-time
// plans and is left as written.
import { execFileSync } from "node:child_process";
import { existsSync, readFileSync, statSync } from "node:fs";
import { dirname, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
const SKIP = [/^docs\/superpowers\//, /^research_notes\//, /^\.superpowers\//];

function markdownFiles(): string[] {
  const out = execFileSync("git", ["ls-files", "--cached", "--others", "--exclude-standard", "--", "*.md"], {
    cwd: root,
    encoding: "utf8",
  });
  return [...new Set(out.split("\n").filter(Boolean))]
    .filter((f) => !SKIP.some((r) => r.test(f)))
    .filter((f) => existsSync(join(root, f)));
}

/** The text with fenced code blocks and inline code spans blanked out. */
function prose(text: string): string {
  return text
    .replace(/^(\s*)(```|~~~)[^\n]*\n[\s\S]*?^\s*\2[^\n]*$/gm, "")
    .replace(/`[^`\n]*`/g, "``");
}

/** GitHub's heading slug: lower case, punctuation dropped (but `-` and `_`), spaces to `-`. */
export function slug(heading: string): string {
  return heading
    .replace(/\[([^\]]*)\]\([^)]*\)/g, "$1")
    .trim()
    .toLowerCase()
    .replace(/[^\p{L}\p{N}\s_-]/gu, "")
    .replace(/\s/g, "-");
}

const anchorCache = new Map<string, Set<string>>();

function anchors(file: string): Set<string> {
  const cached = anchorCache.get(file);
  if (cached) return cached;
  const text = readFileSync(file, "utf8").replace(/^(\s*)(```|~~~)[^\n]*\n[\s\S]*?^\s*\2[^\n]*$/gm, "");
  const seen = new Map<string, number>();
  const set = new Set<string>();
  for (const m of text.matchAll(/^#{1,6}\s+(.+?)\s*#*\s*$/gm)) {
    const base = slug(m[1]!);
    const n = seen.get(base) ?? 0;
    seen.set(base, n + 1);
    set.add(n === 0 ? base : `${base}-${n}`);
  }
  for (const m of text.matchAll(/<a\s+(?:id|name)="([^"]+)"/g)) set.add(m[1]!);
  anchorCache.set(file, set);
  return set;
}

function brokenLinks(file: string): string[] {
  const abs = join(root, file);
  const problems: string[] = [];
  for (const m of prose(readFileSync(abs, "utf8")).matchAll(/\]\(\s*<?([^)\s>]+)>?(?:\s+"[^"]*")?\s*\)/g)) {
    const target = m[1]!;
    if (/^[a-z][a-z0-9+.-]*:/i.test(target) || target.startsWith("//")) continue;
    const [rawPath = "", anchor] = target.split("#", 2);
    const path = rawPath === "" ? abs : resolve(dirname(abs), decodeURIComponent(rawPath));
    if (!existsSync(path)) {
      problems.push(`${file}: ${target} (no such file)`);
      continue;
    }
    if (anchor === undefined || anchor === "") continue;
    if (statSync(path).isDirectory() || !path.endsWith(".md")) {
      problems.push(`${file}: ${target} (anchor on a non-markdown target)`);
      continue;
    }
    if (!anchors(path).has(decodeURIComponent(anchor).toLowerCase())) {
      problems.push(`${file}: ${target} (no heading #${anchor} in ${relative(root, path)})`);
    }
  }
  return problems;
}

describe("markdown links", () => {
  it("slugs headings as GitHub does", () => {
    expect(slug("`GET /hello`")).toBe("get-hello");
    expect(slug("3. Install the agent in the VM")).toBe("3-install-the-agent-in-the-vm");
    expect(slug("Scenarios the evals use (`skills/evals/`)")).toBe("scenarios-the-evals-use-skillsevals");
    expect(slug("2. Hard safety rules (non-negotiable)")).toBe("2-hard-safety-rules-non-negotiable");
  });

  it("finds the markdown files", () => {
    expect(markdownFiles()).toContain("README.md");
  });

  it("every relative link resolves", () => {
    const problems = markdownFiles().flatMap(brokenLinks);
    expect(problems).toEqual([]);
  });
});
