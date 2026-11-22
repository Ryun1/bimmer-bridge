// Lets Node (type stripping, no build) run dev entry points that import mac-mcp/src:
//  - an import written "./x.js" for the compiler is retried as "./x.ts" when no .js exists;
//  - "@modelcontextprotocol/sdk/*" (dev/ has no node_modules) resolves to mac-mcp's copy, the
//    same mapping dev/vitest.config.ts uses.
// Used by `node --experimental-transform-types --import ./dev/ts-resolve.mjs dev/record-server.ts`.
import { register } from "node:module";

const sdk = new URL("../mac-mcp/node_modules/@modelcontextprotocol/sdk/dist/esm/", import.meta.url).href;

register(
  "data:text/javascript," +
    encodeURIComponent(`
const sdk = ${JSON.stringify(sdk)};
export async function resolve(specifier, context, next) {
  const m = /^@modelcontextprotocol\\/sdk\\/(.*)$/.exec(specifier);
  if (m) return next(sdk + m[1], context);
  try {
    return await next(specifier, context);
  } catch (e) {
    if (e?.code === "ERR_MODULE_NOT_FOUND" && /^\\.\\.?\\/.*\\.js$/.test(specifier)) {
      return next(specifier.slice(0, -3) + ".ts", context);
    }
    throw e;
  }
}
`),
);
