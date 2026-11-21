// Run from mac-mcp (which has the dependencies): `npm run test:dev`.
// dev/ has no node_modules of its own, so the MCP SDK is resolved to mac-mcp's copy; the
// mapping is the SDK's own `exports` rule ("./*" -> "./dist/esm/*").
const sdk = new URL("../mac-mcp/node_modules/@modelcontextprotocol/sdk/dist/esm/", import.meta.url).pathname;

export default {
  resolve: {
    alias: [{ find: /^@modelcontextprotocol\/sdk\/(.*)$/, replacement: `${sdk}$1` }],
  },
  test: {
    include: ["dev/test/**/*.test.ts"],
    environment: "node",
  },
};
