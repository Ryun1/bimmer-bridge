# Agent HTTP API — contract

The contract between `vm-agent` (server) and `mac-mcp` (client). Both sides are built against
this file. Change it here first.

Covers what exists after Phases 1 and 3. Later phases add endpoints.

## Transport
- Plain HTTP, port **8765**, on the VM's shared-network IPv4 address only.
- Every request, including `/health`, needs `Authorization: Bearer <token>`.
- The token is 32 random bytes, base64url without padding (43 characters).
- Responses are `application/json; charset=utf-8`. Property names are camelCase.
  Absent values are `null`, never omitted.

## Errors
Any non-2xx response has this body:

```json
{ "error": { "code": "unauthorized", "message": "Missing or invalid bearer token." } }
```

| Status | `code` | When |
|---|---|---|
| 401 | `unauthorized` | header missing, malformed, or token wrong. Sent before any routing, so an unauthenticated caller cannot tell which paths exist |
| 404 | `not_found` | unknown path (authenticated callers only) |
| 405 | `method_not_allowed` | known path, wrong method |
| 500 | `internal_error` | unhandled failure; the message is generic, details go to the agent log |

A 401 also carries `WWW-Authenticate: Bearer`.

## `GET /health`
Never touches the cable and never loads the EDIABAS DLL.

```json
{
  "agent": "ediabas-bridge-agent",
  "version": "0.1.0",
  "startedAt": "2026-11-21T15:04:05Z",
  "uptimeSeconds": 512,
  "os": {
    "description": "Microsoft Windows 10.0.26100",
    "osArchitecture": "Arm64",
    "processArchitecture": "X86"
  },
  "user": "ryan",
  "elevated": false,
  "sessionId": 1,
  "bindAddress": "192.168.64.3",
  "port": 8765,
  "gateway": "192.168.64.1",
  "conflictingProcesses": [ { "name": "INPALOAD", "pid": 4312 } ],
  "ediabas": { "dllPath": "C:\\EDIABAS\\BIN\\api32.dll", "dllFound": true },
  "lastCableUseAt": null
}
```

- `agent` is always the literal `ediabas-bridge-agent`. Clients use it to confirm they
  reached the right service.
- `conflictingProcesses` lists running processes whose name matches the configured conflict
  list (case-insensitive, without `.exe`). Empty array when none.
- `sessionId` is the Windows session the agent runs in. `0` means it is not in an
  interactive session and screenshots will not work.
- `ediabas.dllFound` is a file-existence check only.
- `lastCableUseAt` is `null` until Phase 2.

## `GET /windows`
Visible top-level windows with a non-empty title, in z-order (front first).

```json
{
  "windows": [
    {
      "handle": "0x000A0B2C",
      "title": "INPA - BMW Group",
      "processName": "INPALOAD",
      "pid": 4312,
      "bounds": { "x": 0, "y": 0, "width": 1280, "height": 800 },
      "minimized": false
    }
  ]
}
```

- `handle` is the HWND as `0x` plus 8 upper-case hex digits.
- `processName` has no `.exe`; `null` if the process cannot be queried.
- `bounds` are screen pixels. For a minimised window they are whatever Windows reports.
- Cloaked windows (hidden UWP shells) are left out.
