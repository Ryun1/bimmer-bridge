# EDIABAS internals for a reimplementation: SGBD format, BEST/2 bytecode, API/config, testing, legal

Citation conventions:
- `UP:` is the local read-only EdiabasLib checkout at `/Users/ryan/projects/ediabaslib-spike/upstream` (shallow clone of github.com/uholeschak/ediabaslib master, HEAD 5e9efc5). Line numbers refer to that checkout. The same paths are on GitHub at https://github.com/uholeschak/ediabaslib/blob/master/<path>.
- `EN` is `UP:EdiabasLib/EdiabasLib/EdiabasNet.cs`, which has 7617 lines.
- `OPS` is `UP:EdiabasLib/EdiabasLib/EdOperations.cs`.

## 1. SGBD file layout (.prg / .grp)

### Takeaway
The .prg and .grp files share one container: the ASCII magic `@EDIABAS OBJECT\0`, then a little-endian header of absolute section offsets at 0x7C–0x98. One header field says whether the file is a group file (0) or a variant file (1). Every byte after the plain header is XOR-ed with 0xF7, with the exception of a few count fields. The only public descriptions of the layout are reverse-engineered code: EdiabasLib, the MIT-licensed BESTDIS disassembler bundled in it, and a TypeScript port of EdiabasLib (EdiabasX). BMW publishes no format specification.

### Cited Findings
- **Magic.** All three test binaries begin with the 16 bytes `@EDIABAS OBJECT\0`. A hexdump of `UP:EdiabasLib/Test/Ecu/cmd_test1.prg`, `cmd_test2.prg` and `cmd_ident.grp` at offset 0 confirms this.
- **Header map, as written in the BESTDIS comments** (`UP:Tools/BESTDIS/Program.cs:394-407`):
  - 0x00: magic
  - 0x10: 0 = group file, 1 = prg file
  - 0x14: -1
  - 0x18: ssize
  - 0x78: unknown pointer
  - 0x7C: uses pointer
  - 0x80: job code pointer
  - 0x84: table pointer
  - 0x88: job list pointer
  - 0x8C: unknown pointer
  - 0x90: description pointer
  - 0x94: info pointer
  - 0x98: unknown pointer (missing or reserved)

  BESTDIS reads 0x9C header bytes.
- **What the hexdumps show.** At 0x10, cmd_test1.prg and cmd_test2.prg hold `01 00 00 00` and cmd_ident.grp holds `00 00 00 00`. Unused pointers are `ffffffff`. 0x9C holds `aaaa aaaa` in every file. 0x80 (job code) is always `0xA0`, so code starts right after the header. cmd_test1.prg has 0x90 = -1, meaning it has no description block. cmd_test2.prg has a non-(-1) value at 0x8C, a section no tool parses.
- **File type.** `GetFileType` reads the uint32 at 0x10, and the value 0 is treated as a group file (`EN:5408-5450`, `EN:5338`). When asked for a name, EdiabasLib looks for `<name>.prg` first and falls back to `<name>.grp` (`EN:5416-5426`).
- **XOR obfuscation.** `ReadAndDecryptBytes` XORs every byte with 0xF7 (`EN:7158-7168`); BESTDIS does the same (`UP:Tools/BESTDIS/Program.cs:834`). Header pointers and these counts are read raw with `ReadInt32`, which does no XOR (`EN:7171-7183`):
  - job count (`EN:4923`)
  - uses count (`EN:4709`)
  - description byte count (`EN:4776`)

  The table count, unlike the others, is read through the XOR decoder (`EN:5018-5024`).
- **EdiabasX on the XOR scope.** It says data from 0xA0 onward is XOR 0xF7 and that the table count's encoding is "inconsistent", so its parser tries the raw value first and then the decoded one — [EdiabasX interpreter reference](https://emdzej.github.io/ediabasx-docs/reference/interpreter.html).
- **Job table** (`EN:4898-4990`). At the 0x88 offset sits an int32 count. Each 0x44-byte XOR-ed record holds a 0x40-byte NUL-padded cp1252 name and a uint32 absolute code address at 0x40. The array/string buffer size ("ssize", `#pragma arraysize`) comes from header 0x18; 0 means the default of 1024 (`EN:4900-4913`, `UP:Tools/BESTDIS/Program.cs:513-521`).
- **Version/info block** (`EN:4724-4756`). It is 0x6C bytes at the 0x94 offset, XOR-ed:
  - bytes 0–2: BIP version
  - 0x04: revision minor (int16)
  - 0x06: revision major (int16)
  - 0x08: author, 0x40 bytes
  - 0x48: "from"/last-changed date string
  - 0x68: package version (int32)

  The two tools disagree on the date field length: EdiabasLib reads 0x20 bytes (`EN:4752`) and BESTDIS reads 0x24 (`UP:Tools/BESTDIS/Program.cs:529`).
- **BIP version check.** If the file's BIP version is greater than the emulated EDIABAS version, EdiabasLib raises BIP-0009 "BEST VERSION ERROR" (`EN:5649-5658`). The emulated version is `EdiabasVersion = 0x773`, i.e. 7.7.3 (`EN:595`). The `EdiabasLib.config` doc still says "Current EDIABAS emulation version is 7.6.0" (`UP:docs/EdiabasLib.config_file.md:2`), so the doc is stale.
- **Description block** (`EN:4758-4836`). An int32 byte count is followed by XOR-ed, newline-separated text lines. A line starting `JOBNAME:` begins a new job's comment list; lines before the first `JOBNAME:` are global (ECU) comments. Lines are capped at about 1098 bytes. This block is also where `_ARGUMENTS`/`_RESULTS` metadata comes from: argument and result names, types and comments are encoded as comment lines such as `RESULT:`, `RESULTTYPE:` and `RESULTCOMMENT:`, as the BEST/1 group source shows (`UP:EdiabasLib/Test/Ecu/cmd_ident.b1g:1-20`).
- **Tables** (`EN:4992-5105`).
  - At the 0x84 offset: an XOR-ed int32 table count, then 0x50-byte records holding a 0x40-byte name, a column-data offset at 0x40, a column count at 0x48 and a row count at 0x4C.
  - Cells are XOR-ed, NUL-terminated cp1252 strings stored row-major. Row 0 is the header row of column names, upper-cased for lookup.
  - Every cell is a string: numeric cells such as `"0x14"` are text, as in the `table` declarations of `UP:EdiabasLib/Test/Ecu/cmd_test2.b2v:20-50`.
- **USES inheritance** (`EN:4687-4720`, `EN:4838-4896`).
  - At the 0x7C offset: a raw int32 count, then XOR-ed 0x100-byte base-file names.
  - Jobs of each base file (`<name>.prg` in EcuPath) are appended after the file's own jobs. The first name wins in the lookup dictionary, so local jobs override base jobs. INITIALISIERUNG and ENDE in base files are not registered.
  - The job list is resolved one level deep: there is no recursive USES of USES.
  - A base-file job runs against the base file's stream (`EN:5677-5700`). Base-file tables can be opened via `_sgbdBaseFs` and `_tableInfosExt` (`EN:4015`).
- **Encoding.** EdiabasNet uses `Encoding.GetEncoding(1252)` for all strings (`EN:2433`); BESTDIS does the same (`UP:Tools/BESTDIS/Program.cs:361`).
- **Operand encoding** (`EN:5800-5822`, `EN:1497-1515`, `EN:6800-6960`). Each instruction is 2 XOR-ed bytes: an opcode, then an address-mode byte whose high nibble is arg0's mode and low nibble is arg1's. There are 16 modes:

  | Mode | Meaning |
  |---|---|
  | 0 | None |
  | 1 | RegS |
  | 2 | RegAb |
  | 3 | RegI |
  | 4 | RegL |
  | 5 | Imm8 |
  | 6 | Imm16 |
  | 7 | Imm32 |
  | 8 | ImmStr (length-prefixed) |
  | 9 | IdxImm |
  | 10 | IdxReg |
  | 11 | IdxRegImm |
  | 12 | IdxImmLenImm |
  | 13 | IdxImmLenReg |
  | 14 | IdxRegLenImm |
  | 15 | IdxRegLenReg |

  Jump-class opcodes with an Imm32 arg0 are PC-relative to the next instruction (`EN:5818-5822`).
- **EdiabasX on a second container format.** EdiabasX claims a "legacy binary PRG/GRP" format: a 32-byte header with magic `0x00475250` ("PRG\0") and a 12-byte job entry. No such format appears in EdiabasLib, which only handles `@EDIABAS OBJECT` — [EdiabasX interpreter reference](https://emdzej.github.io/ediabasx-docs/reference/interpreter.html). The claim is unverified.
- **No official specification.** When asked how EdiabasLib decodes SGBD/GRP files, the EdiabasLib author answered that SGBDs "are assembly code and decompilation is basically not really possible. You could only execute the files" — [issue #223](https://github.com/uholeschak/ediabaslib/issues/223).

### Inferences
- A Zig parser needs about seven small routines: header, job list, uses, info, description, tables, and an operand decoder. The BESTDIS source is about 840 lines and MIT-licensed, so it can serve as a non-GPL reference for the container layout. Section 7 covers what that does and does not settle.
- Treat any field that is "read raw" versus "XOR-decoded" as fixed by EdiabasLib behaviour, and write tests for the table-count quirk.
- Mirror EdiabasLib's single-level USES resolution unless a real SGBD shows nested USES.

### Gaps
- No public document explains header 0x78, 0x8C, 0x98 or the `aaaa aaaa` at 0x9C.
- The legacy "PRG\0" format claimed by EdiabasX is unconfirmed.
- No public BMW specification of the object format was found.

## 2. BEST/1, BEST/2 language and the BIP interpreter

### Takeaway
BEST/2 is BMW's C-like SGBD source language. The best2 compiler lowers it to BEST/1 assembly, which is then assembled to the .prg bytecode that the BIP (BEST interpreter) runs. The VM has 32 byte registers (B0–BF, A0–AF), 16 I registers, 8 L registers, 16 S (string/byte-array) registers and 8 F registers. It has carry, zero, sign and overflow flags, an error-trap mask, and result sets built by `erg*` opcodes. BMW's language and function references ship with EDIABAS and are not legitimately published online.

### Cited Findings
- **Compilation pipeline.** "Best/2 is always converted to Best/1 and the compiled to the .prg" — EdiabasLib author in [issue #223](https://github.com/uholeschak/ediabaslib/issues/223).
  - The test sources show both kinds of input. The `.b2v` files contain C-like `job(...) { ... }` bodies with inline `#asm ... #endasm` BEST/1 assembly (`UP:EdiabasLib/Test/Ecu/cmd_test2.b2v:52-110`). The `.b1v`/`.b1g` files are plain BEST/1 assembly with `;JOBNAME:` comment headers (`UP:EdiabasLib/Test/Ecu/cmd_ident.b1g`).
  - The BMW compilers are `Best164.exe`/`Best264.exe` in `%EDIABAS_PATH%\BIN`, invoked as `best2 -Q -L test.lib x.b2v` (`UP:EdiabasLib/Test/Ecu/CompileStd.bat:7-41`).
- **Third-party description.** A VDE-Verlag book excerpt describes BEST/2 as the description language for SGBD contents, with syntax similar to C, placed alongside the EDIABAS runtime and the EDIC interface — [VDE excerpt PDF](https://www.vde-verlag.de/buecher/leseprobe/9783800732753_PROBE_01.pdf).
- **Registers** (`EN:1865-1945`).

  | Register class | Encodings | Notes |
  |---|---|---|
  | RegAb (byte) | 0x00–0x0F (B0–BF), 0x80–0x8F (A0–AF) | 32 total |
  | RegI (16-bit) | 0x10–0x17, 0x90–0x97 | 16 total |
  | RegL (32-bit) | 0x18–0x1B, 0x98–0x9B | 8 total |
  | RegS (string) | 0x1C–0x23, 0x2C–0x33 | 16 total |
  | RegF (float) | 0x24–0x2B | 8 total |

  Register opcodes ≥0x80 are remapped by `index = opcode - 0x80 + 0x34` (`EN:6970-6990`). Backing storage is `_byteRegisters[32]`, `_floatRegisters[16]` and `_stringRegisters[16]`, each string sized `ArrayMaxBufSize` (`EN:3215-3221`).
- **Flags** (`EN:2161-2240`). The flags are Carry, Zero, Sign and Overflow. Zero and Sign are computed with masks for operand lengths 1, 2 and 4. Overflow uses two's-complement sign rules.
  - EdiabasX documents the `pushf` bit layout as C=1, Z=2, S=4, V=8 — [EdiabasX](https://emdzej.github.io/ediabasx-docs/reference/interpreter.html).
- **Error trap** (`EN:4140-4186`, `EN:3178-3212`).
  - `SetError` maps an error code to a trap bit number through `_trapBitDict`:

    | Error code | Trap bit |
    |---|---|
    | BIP-0002 | 2 |
    | BIP-0006 | 6 |
    | BIP-0009 | 9 |
    | BIP-0010 | 10 |
    | IFH-0001 … IFH-0016 | 11 … 26 |
    | BIP-0011 (only when version ≥7.6.0) | 8 |
    | IFH-0069 (only when version ≥7.6.0) | 28 |
    | IFH-0074 (only when version ≥7.7.0) | 29 |
    | anything else | 0 |

  - The error is raised as an exception only if `(1<<bit) & ~_errorTrapMask` is non-zero. Otherwise the SGBD can test it via trap opcodes; per EdiabasX these are `settmr/gettmr/sett/clrt/jt/jnt` ([EdiabasX](https://emdzej.github.io/ediabasx-docs/reference/interpreter.html)).
  - An error raised during the ENDE job is rewritten to SYS-0018 (`EN:4167-4172`).
  - The test SGBD shows the idiom for throwing a custom trap: `clrt; settmr #$FFFFFFFF.L; sett #2; eerr` (`UP:EdiabasLib/Test/Ecu/cmd_test2.b2v:86-92`).
- **Per-job reset** (`EN:5762-5790`). At the start of each job EdiabasLib clears results, the stack, flags, string registers, trap bit and mask, progress info and the job status. It sets the `BipEcuFile` config property and sets the PC to the job's offset.
- **Result sets** (`EN:5852-5880`, `EN:4616-4668`).
  - Results accumulate in a dictionary; the `enewset` opcode pushes a set.
  - At job end, set 0 is the synthetic "system" set with these results: `VARIANTE`, `OBJECT`, `JOBNAME`, `SAETZE` (the number of data sets, type W). `GRUPPE` and `FAMILIE` are added when version ≥7.6.0.
  - If `SystemResults` ≠ 0 it also adds `JOBSTATUS`, `UBATTCURRENT`, `UBATTHISTORY`, `IGNITIONCURRENT` and `IGNITIONHISTORY` (each -1 unless simulating).
  - Real EDIABAS output carries the same system-set fields, shown in a user's log in [issue #40](https://github.com/uholeschak/ediabaslib/issues/40).
- **Opcode table.** `OcList` maps 0x00–0xB7 to mnemonic and handler. A `null` handler throws "Function not implemented" (`EN:1948-2136`, `EN:5839-5851`).
  - Examples: 0x00 move, 0x0B jump, 0x0C jtsr = null, 0x0D ret = null, 0x7C tabseek, 0x84 xsendr = null, 0xA0 pcall = null, 0xB5 ssize, 0xB6 tabcols, 0xB7 tabrows.
  - EdiabasX states it "implements 184 opcodes" and lists about 15 as stubs "for backward compatibility" — [EdiabasX](https://emdzej.github.io/ediabasx-docs/reference/interpreter.html).
- **Version-dependent semantics.** EDIABAS 7.6.0 changed math behaviour. For example, `a2flt` (BEST/2 `ator`) raises BIP-0011 on an invalid string unless the `CompatMode` config property is set (`OPS:44-70`). The `EdiabasLib.config` doc says: "EDIABAS 7.6.0 has incompatible changes in mathematical function. This prevents using old ECU files. Setting this value to 1 (what is the default), keeps the behaviour of EDIABAS 7.3.0" (`UP:docs/EdiabasLib.config_file.md:44`).
- **Which opcodes real SGBDs use.**
  - The README says: "At the moment most BEST/1 commands are implemented, but a few are still missing, which seem to be not required in the current ECU files. The implemented commands are automatically tested and compared with EDIABAS output" (`UP:README.md:58-59`).
  - Sample frequency counts from `UP:EdiabasLib/Test/Ecu/dm338ds1_interbyte.b1v`, a BESTDIS disassembly of a 1997 BMW DME 3.3 SGBD (BIP 05.05.00, author "BMW TP-421") counted with grep/uniq:

    | Mnemonic | Count |
    |---|---|
    | push | 2984 |
    | move | 2913 |
    | pop | 2900 |
    | clear | 1286 |
    | jump | 656 |
    | jz | 416 |
    | atsp | 329 |
    | jnz | 295 |
    | tabget | 247 |
    | ergs | 142 |
    | tabseek | 129 |
    | tabset | 118 |
    | comp | 93 |
    | a2fix | 91 |
    | fix2hex | 89 |
    | scmp | 80 |
    | jpl | 78 |
    | a2flt | 73 |
    | adds | 60 |
    | xsend | 53 |

    The long tail includes xawlen, etag, ergi/ergr/ergl/ergy, parw/parl, xprog, xsetpar, xreps and scat. None of the 16 unimplemented opcodes appear in it.
- **Official documents.** No legitimate public copy of the official BEST/2 language or function reference was found. Searches for the titles returned only third-party projects and unofficial uploads, which are not linked here. The EDIABAS API developer's guide exists, but copies online are unofficial uploads (Scribd).

### Inferences
- In practice the BIP is a register VM with an operand-addressing matrix. Porting `GetOpArg`, the `Operand` class and the flag helpers exactly matters more than the opcode count.
- Since 16 opcodes are unimplemented upstream and absent from a sampled real SGBD, a port can match EdiabasLib by raising a clear "not implemented" error. Opcode usage should still be measured across the user's own ECU directory (for example with a Zig disassembler) before deciding.
- Pin an emulated EDIABAS version, as EdiabasLib does (7.7.3 plus the CompatMode default). Several semantics branch on it: trap bits, system results, a2flt errors.

### Gaps
- No public opcode-semantics document exists beyond EdiabasLib source and EdiabasX docs, which are derived from it. The exact stack depth limit (BIP-0005) and float formatting edge cases are not documented publicly.
- No corpus-wide opcode statistics were found. Only one BMW-derived disassembly is present locally.

## 3. BestNet and BESTDIS

### Takeaway
BestNet is not a compiler. It is a thin GPL wrapper that P/Invokes BMW's proprietary `Best32.dll`/`Best64.dll` from EDIABAS 7.6+, so it needs a licensed EDIABAS install. BESTDIS is an MIT-licensed (tacotruck, 2014) disassembler from .prg/.grp to BEST/1-style text. Neither runs without Windows and .NET. Both are useful offline: the committed test .prg files are the fixtures, and BESTDIS-style disassembly is the oracle for operand decoding.

### Cited Findings
- **BestNet.** It imports `__best32Startup`, `__best1Init`, `__best1Asm`, `__best2Init`, `__best2Cc` and related functions from `Best32.dll`/`Best64.dll`. It requires `MinBestVersion = 0x00070600` and links `B2Runtim.lib` (`UP:Tools/BestNet/Program.cs:17-120`). The author: "Basically here is a wrapper for the BEST Compiler, but it will only work with EDIABAS 7.6.0 … Otherwise use Best1.exe or Best2.exe that comes with EDIABAS" — [issue #175](https://github.com/uholeschak/ediabaslib/issues/175).
- **BESTDIS.**
  - License: MIT, "Copyright (c) 2014 tacotruck" (`UP:Tools/BESTDIS/Program.cs:1-22`).
  - Output: `.b1v`/`.b1g` text with the info header, description comments, `;Uses:`, `#SSIZE`, `TBEG/HEAD/LINE/TEND` tables, and `JOBNAME#` disassembly (`UP:Tools/BESTDIS/Program.cs:364-560`).
  - Input: it takes a directory/glob ([issue #175](https://github.com/uholeschak/ediabaslib/issues/175)).
  - Author's caveat: "There is a decompiler, but it will not help you" ([issue #223](https://github.com/uholeschak/ediabaslib/issues/223)).
- **Committed test fixtures.**
  - Upstream commits GPL test sources and their compiled .prg/.grp: `base1`, `base2`, `cmd_test1` (BEST/1, 1287 lines), `cmd_test2` (BEST/2, 5804 lines, `uses: base1,base2`, plus `test.lib`), `cmd_ident.grp` (group with IDENTIFIKATION), `adapter_prg`, and two BMW-derived files, `d60m47a0_dcan` and `dm338ds1_interbyte` (`UP:EdiabasLib/Test/Ecu/`).
  - They are compiled either with BMW's best1/best2 (`CompileStd.bat`) or with BestNet (`CompileNet.bat`).
  - `cmd_test2.b2v` covers USES inheritance and table lookups across base files.

### Inferences
- New test SGBDs can only be produced on a machine with licensed EDIABAS (best1/best2 or Best32.dll). The user's Windows VM appears to have EDIABAS installed (the bridge talks to EDIABAS there), so best2 may be on hand to generate new fixtures. The committed .prg files need no compiler at all.
- A Zig disassembler whose output matches BESTDIS byte-for-byte on the same .prg would make a strong decoder test. Run BESTDIS under .NET on another machine, not via upstream scripts here.

### Gaps
- Whether the BMW-derived `dm338ds1_interbyte` and `d60m47a0_dcan` files can be redistributed is unclear; see section 8.

## 4. EDIABAS API, error codes, INI keys and trace formats

### Takeaway
The API is the C `api.h` (v8.0): `apiInit/apiInitExt`, `apiJob/apiJobData/apiJobExt`, `apiState` (BUSY=0, READY=1, BREAK=2, ERROR=3), and typed `apiResult*` getters over result sets indexed from 0, where set 0 holds the system results. Error codes are numeric bands with fixed texts:

| Band | Numeric range |
|---|---|
| IFH | 10–59, extended later |
| BIP | 60–89 |
| SYS | 90–119 |
| API | 120–149 |
| NET | 150+ |

EDIABAS.INI keys include Interface, EcuPath, TracePath, TraceSize, ApiTrace and IfhTrace. EdiabasLib's own trace files are not byte-compatible with BMW's.

### Cited Findings
- **Header contents.** `UP:EdiabasLib/Api32/Api.h` is a reproduction of `api.h - Header file of API/EDIABAS, Version 8.0` (lines 1-3).
  - `APICOMPATIBILITYVERSION 0x0800`; API states BUSY=0, READY=1, BREAK=2, ERROR=3.
  - Limits: APIMAXNAME 64, APIMAXPARA 1024, APIMAXPARAEXT 65536, APIMAXSTDPARA 256, APIMAXRESULT 256, APIMAXTEXT 1024, APIMAXBINARY 1024 / EXT 65536 (`Api.h:17-40`).
  - Function prototypes at `Api.h:437-482`: apiBreak, apiEnd, apiErrorCode, apiErrorText, apiGetConfig, apiSetConfig, apiInit, apiInitExt, apiJob, apiJobData, apiJobExt, apiJobInfo, apiResultBinary, apiResultBinaryExt, apiResultByte, apiResultChar, apiResultDWord, apiResultFormat, apiResultInt, apiResultLong, apiResultLongLong, apiResultQWord, apiResultName, apiResultNumber, apiResultReal, apiResultSets, apiResultText, apiResultVar, apiResultWord, apiResultsNew/Delete/Scope, apiState, apiStateExt, apiSwitchDevice, apiCheckVersion, apiTrace.
  - Result formats: APIFORMAT_CHAR 0 … APIFORMAT_QWORD 10 (`UP:EdiabasLib/apiNET/API.cs:45-70`).
- **Error codes.**
  - Numeric enum (`EN:609-760`): `EDIABAS_ERR_NONE=0`, IFH-0000=10, BIP-0000=60, SYS-0000=90, API-0000=120, NET-0000=150. `Api.h:43-45` adds `EDIABAS_RESERVED=1` and `EDIABAS_ERROR_CODE_OUT_OF_RANGE=2`.
  - The full text table is at `EN:1015-1200+`. Key entries:

    | Code | Text |
    |---|---|
    | IFH-0009 | NO RESPONSE FROM CONTROLUNIT |
    | IFH-0018 | INITIALIZATION ERROR |
    | BIP-0004 | ILLEGAL OPCODE |
    | BIP-0005 | STACK OVERFLOW |
    | BIP-0006 | BEST FILE ERROR |
    | BIP-0007 | DIVISION BY ZERO |
    | BIP-0008 | BEST BREAK |
    | BIP-0009 | BEST VERSION ERROR |
    | BIP-0010 | CONSTANT DATA ACCESS ERROR |
    | BIP-0011 | REAL ERROR |
    | SYS-0002 | ECU OBJECT FILE NOT FOUND |
    | SYS-0005 | OBJECT FILE NOT FOUND |
    | SYS-0008 | JOB NOT FOUND |
    | SYS-0009 | NO INITIALIZATION JOB |
    | SYS-0010 | INITIALIZATION ERROR |
    | SYS-0011 | NO IDENTIFICATIONJOB |
    | SYS-0012 | IDENTIFICATION ERROR |
    | SYS-0018 | END JOB ERROR |
    | SYS-0020 | BASE OBJECT FILE NOT FOUND |
    | API-0005 | ILLEGAL RESULT FORMAT |
    | API-0014 | RESULT NOT FOUND |
    | NET-0009 | TIMEOUT |

- **Result text formatting.** `apiResultText` format specifiers are `[-][digit1][.digit2][e|E]` followed by a conversion character C/B/I/W/L/D/R/T, "extracted from the Ediabas developer guide" (`UP:docs/EDIABAS_result_types_and_formats.md`). The edge cases are exercised in `UP:EdiabasLib/Test/Scripts/coverage.bat` (results20–23, e.g. `ERGR=-10.5eR`, `ERGS= 0 . 8 T`).
- **INI keys.** EdiabasLib reads `EdiabasLib.config` in place of EDIABAS.INI. Standard keys: Interface (STD:OBD, ADS, ENET, RPLUS), ApiTrace, IfhTrace, TraceBuffering, TracePath, EcuPath, RetryComm, the ENET keys, and the HSFZ/DoIP ports 6801/6811/13400 (`UP:docs/EdiabasLib.config_file.md:5-30`).
  - Classic EDIABAS.INI semantics, from EDIABAS's own INI documentation as summarised by search results: ApiTrace 0 = off, 1 = user trace, 2 = plus timestamp; IfhTrace 0–3; TraceSize is the max trace file size in KB (0–32767, default 1024); EcuPath defaults to the current directory. EdiabasLib documents ApiTrace only as 0–1 ([EdiabasLib.config docs](https://uholeschak.github.io/ediabaslib/docs/EdiabasLib.config_file.html)). The BMW INI.pdf exists in EDIABAS installs, but the online copies found are on unofficial mirrors and are not linked here.
- **Trace logging.** EdiabasLib's log levels are Off=0, Ifh=1, Error=2, Info=3 (`EN:2450-2456`). At Info level it logs every executed instruction as `>`/`<` disassembly (`EN:5839-5846`), which is a ready-made instruction trace for differential debugging. It writes its own `ifh.trc`, and `LogfileConverter` turns "EdiabasLib input trace file (`.trc`)" into CarSimulator or `.sim` files (`UP:docs/LogfileConverter_parameters.md`).

### Inferences
- Byte-for-byte comparison against BMW API traces is not realistic. Compare structured outputs instead: result sets and values, with the same `FormatResult` conversions, plus error codes. Optionally compare EdiabasLib's Info-level instruction trace with a Zig equivalent, step by step.

### Gaps
- No public specification of the BMW `api.trc`/`ifh.trc` line format was found.
- No authoritative list was found of IFH codes beyond 0049, such as IFH-0069 and IFH-0074 used in the trap table.

## 5. Does INITIALISIERUNG run before virtual jobs like _JOBS?

### Takeaway
In EdiabasLib it does by default: `ExecuteVJob` runs the init job if `_requestInit && !NoInitForVJobs`, and `NoInitForVJobs` defaults to false. EdiabasLib's own Deep OBD code turns it on (true) for metadata-only reads, so the switch exists to skip init where it is unwanted. No public BMW document found states real EDIABAS's behaviour for virtual jobs specifically.

### Cited Findings
- `ExecuteVJob` starts with `if (_requestInit && !NoInitForVJobs) ExecuteInitJob();` (`EN:5887-5892`). `NoInitForVJobs` is a public auto-property that defaults to false (`EN:2716`).
- Deep OBD sets `NoInitForVJobs = true` before metadata calls in `UP:EdiabasLib/BmwFileReader/SgFunctions.cs:265,396,605,857,946` and in `UP:BmwDeepObd/EdiabasToolActivity.cs:2419`. It sets it to false in `UP:BmwDeepObd/EdiabasThread.cs:3495` and `UP:Tools/Psdz/PsdzClientLibrary/PsdzClient/DetectVehicle.cs:1261`.
- **Init semantics** (`EN:5463-5505`).
  - `ExecuteInitJob` runs INITIALISIERUNG and requires result `DONE == 1` in result set 1. Otherwise it closes the SGBD and sets SYS-0010.
  - `_requestInit` starts true (`EN:2530`) and is reset to true when the SGBD is reopened (`EN:3947`).
  - An SGBD can request re-init with `ergsysi "!INITIALISIERUNG", nonzero` (`OPS:599-607`).
  - Normal jobs run init unless the call is recursive (`EN:5762-5765`).
- **BMW's standard comment.** The INITIALISIERUNG comment inside SGBD sources reads: "Dieser Job wird vom EDIABAS automatisch beim erstem Zugriff auf eine SGBD aufgerufen. Bei weiteren Zugriffen auf die selbe SGBD wird dieser Job nicht mehr aufgerufen." ("EDIABAS calls this job automatically on the first access to an SGBD and not again on later accesses to the same SGBD.") This is in `UP:EdiabasLib/Test/Ecu/cmd_test2.b2v:57-62`.
- **Upstream test coverage.** The coverage script runs `_JOBS`, `_VERSIONINFO`, `_JOBCOMMENTS#INITIALISIERUNG`, and INIT_EXCEPTION/INIT_ERROR cases. The same script runs against real EDIABAS (`coverage ediabas`) and against EdiabasLib, so init-before-vjob behaviour is part of what upstream compares (`UP:EdiabasLib/Test/Scripts/coverage.bat`, results13/14/17_x).

### Inferences
- The default plus the "first access" comment suggest real EDIABAS runs INITIALISIERUNG on first access to an SGBD whatever the job, virtual jobs included. EdiabasLib's default (init before vjobs) likely mirrors observed EDIABAS behaviour, since upstream diff-tests against it.
- For a read-only bridge, a `NoInitForVJobs`-style option is valuable. It lets `_JOBS`/`_TABLE` run without touching the car, and Deep OBD uses it exactly that way.

### Gaps
- No upstream issue or BMW document explicitly confirms real EDIABAS behaviour for vjobs. Confirm it empirically in the VM with the API trace on, running `_JOBS` with the cable unplugged.

## 6. Upstream testing and simulation files as replay fixtures

### Takeaway
Upstream testing is differential and script-driven, with no unit-test framework. The same job list in `coverage.bat` runs through three drivers: EdiabasTest (EdiabasLib), EdiabasLibCall (EdiabasLib via the Api32 shim), and EdiabasCall (real BMW api32.dll). The output logs are compared. EdiabasLib `.sim` files are INI request/response maps at the telegram level. They can serve as transport-level replay fixtures for the port, but only for the EdiabasLib-extended syntax.

### Cited Findings
- **`coverage.bat` drivers.** It takes `lib|apilib|ediabas` as the first argument and runs `EdiabasTest.exe`, `EdiabasLibCall.exe` or `EdiabasCall.exe` respectively, writing `output_lib.log`, `output_apilib.log` or `output_ediabas.log`. It uses OpenCover for coverage. It needs CarSimulator (`e61.txt`) or a real interface (`UP:EdiabasLib/Test/Scripts/coverage.bat:1-70`).
- **Jobs covered.** BMW SGBDs (`d_motor.grp`, `d_ccc.grp`, `d_ehc.grp`, `d_klima.grp`, `e60.prg`, `tmode.prg`) from a sibling `Ecu` directory that is not in the repo. The committed test SGBDs exercise:
  - flag semantics for each arithmetic op: TEST_SUBB_FLAGS … TEST_FCOMP_FLAGS
  - parameter parsing: TEST_PARL `0y011001`, `0x10`
  - a2fix edge cases
  - trap and exception paths: TEST_RAISE_ERROR 0–32, TEST_RAISE_RUNTIMEERR 249–410, BIP1/BIP10, BREAK
  - result formatting
  - init/exit exceptions

  The README says implemented commands "are automatically tested and compared with EDIABAS output" (`UP:README.md:59`).
- **EdiabasTest CLI grammar.** `-j "<job>#<args;>#<results;>#<stdargs;>"`, with `|hex` for binary arguments (`UP:docs/EdiabasTest_parameters.md`).
- **Simulation file format** (`UP:docs/EdiabasLib.simulation_file.md`). Simulation files are INI files named `<interface>.sim` or `<sgbd>.sim`.
  - Sections `[REQUEST]`, `[RESPONSE]` and `[KEYBYTES]`, with keys pairing requests to responses as comma-separated hex. `XX` is a wildcard.
  - Extended syntax adds `[<addr>.REQUEST]`, mask operators (`80&C0`), variable length (`..`), computed responses (`01+[02]`, `#00`, `01^$00` checksums) and multi-frame functional responses.
  - Config keys: `Simulation`, `SimulationPath`, `SimulationCompat`, `SimulationInterfaces`.
  - The parser is `UP:EdiabasLib/EdiabasLib/EdSimFile.cs` (815 lines).
- **When simulation arrived.** In May 2024 the author wrote "Simulation is not supported in EdiabsLib … simulate the OBD interface of the vehicle" ([issue #176](https://github.com/uholeschak/ediabaslib/issues/176)). EdSimFile.cs was first committed on 2024-09-02 ("Parse sim file", GitHub commits API), so simulation is a late-2024 addition.
- **CarSimulator** (`e61.txt` response files) is the older replay path. LogfileConverter converts traces to CarSimulator responses and then to `.sim` (`UP:docs/LogfileConverter_parameters.md`).

### Inferences
- Recommended test strategy for the Zig port:
  - **(a)** Decoder golden tests against the committed `UP:EdiabasLib/Test/Ecu/*.prg/.grp`.
  - **(b)** Interpreter golden tests: run each `cmd_test1`/`cmd_test2` job via the coverage.bat job list. Capture expected result sets once from EdiabasLib, run in a .NET environment, and from real EDIABAS in the user's VM via the bridge or Tool32. Store them as JSON fixtures.
  - **(c)** Transport replay using `.sim` files (or bridge-captured telegrams) for the user's own SGBDs.
  - **(d)** An optional step-trace diff against EdiabasLib's Info-level instruction log.
- The `.sim` format is simple and documented, and implementing a compatible reader gives free interoperability with existing captures. EdiabasLib's extended syntax is non-standard, so fixtures meant for real EDIABAS should use standard syntax.

### Gaps
- Upstream publishes no golden output logs: they are generated locally and deleted.
- No real EDIABAS `.sim` specification was found apart from EdiabasLib's statement that it supports "simulation files like the standard EDIABAS".

## 7. Known correctness gaps between EdiabasLib and real EDIABAS

### Takeaway
Known gaps:
- 16 opcodes are unimplemented, said to be unused in current ECU files.
- Interface-specific concepts are unsupported: EDIC parameters and `set_parameter`.
- Some behaviour depends on the emulated version or CompatMode: 7.3.0 versus 7.6.0 math.
- USES resolution is single-level.
- The info date field length is ambiguous.

The issue tracker shows few interpreter-level bugs; most issues are about adapters and the app.

### Cited Findings
- **Missing opcodes.** README: "a few are still missing, which seem to be not required in the current ECU files" (`UP:README.md:58`). The unimplemented set is all entries with `null` handlers in `EN:1948-2136`.
- **Empty arguments.** Issue #6: jobs with empty arguments mis-parsed (fixed: "Fixed pars handling for empty arguments"). `set_parameter` for concept 0 (EDIC) was changed to return "concept not implemented" because EdiabasLib cannot set EDIC parameters — [issue #6](https://github.com/uholeschak/ediabaslib/issues/6).
- **CompatMode.** The 7.6.0 math changes break old ECU files; CompatMode=1 keeps the 7.3.0 behaviour (`UP:docs/EdiabasLib.config_file.md:44`, `OPS:44-70`).
- **Extensions.** EdiabasLib adds behaviour real EDIABAS lacks: `_JOBS` with argument `ALL`, which lists inherited jobs (`EN:5925-5927`, commented "EdiabasLib extensions"), and `NoInitForVJobs`.
- **Messwertblock jobs.** Issue #40 reports `STATUS_MESSWERTBLOCK_0` behaving differently until INPA had run `MESSWERTBLOCK_X_SCHREIBEN`. This is ECU state, not an interpreter bug (`UP:docs/Page_specification.md:776`; [issue #40](https://github.com/uholeschak/ediabaslib/issues/40)).
- **Field-length disagreement.** EdiabasLib reads the info block's date as 0x20 bytes and BESTDIS as 0x24 (`EN:4752` versus `UP:Tools/BESTDIS/Program.cs:529`).

### Inferences
- The port should copy EdiabasLib behaviour, including its extensions, but keep the extensions behind flags so EDIABAS-pure behaviour can be compared in the VM.

### Gaps
- The tracker was searched for "not implemented", "SGBD", "compatibility", "simulation", "BIP-" and opcode names; there is no systematic list of interpreter divergences. The detailed fidelity knowledge lives in the coverage scripts, not in issues.

## 8. Licensing and legal context

### Takeaway
A Zig port that translates EdiabasNet is a derivative work of GPL-3 code and must itself be GPL-3 (or GPL-3-compatible) if distributed. BMW SGBDs and EDIABAS are BMW copyright and must not be bundled; users supply their own, for example from their own EDIABAS/ISTA/Standard Tools install. In the EU, the ideas, the programming language and the data file formats are not protected by copyright (SAS v WPL), and Article 5(3) allows lawful users to observe, study and test a program. Reimplementing a BEST/2 interpreter clean-room is therefore on solid ground, while copying BMW code or files is not.

### Cited Findings
- **EdiabasLib licence.** EdiabasLib is GPLv3 (`UP:license.md:1`). GPLv3 §0 defines "modify" as "to copy from or adapt all or part of the work in a fashion requiring copyright permission", and a modified version must be conveyed under the GPL (§5) — [GPLv3 text](https://www.gnu.org/licenses/gpl-3.0.html). BESTDIS, inside the same repo, is MIT (`UP:Tools/BESTDIS/Program.cs:1-22`).
- **BMW copyright.** The EdiabasLib author on SGBDs: "You could use the .prg and .grp files from INPA or ISTA. The files are copyright by BMW and could not be added" — [issue #175](https://github.com/uholeschak/ediabaslib/issues/175).
  - Despite this, the upstream repo contains a BESTDIS disassembly of a BMW SGBD authored by "BMW TP-421 Weber" (`UP:EdiabasLib/Test/Ecu/dm338ds1_interbyte.b1v:1-10`).
  - The Deep OBD app downloads and extracts about 2.5 GB of "ECU files" on first start (`UP:docs/Deep_OBD_for_BMW_and_VAG.md:35`).
- **EdiabasX.** It describes itself as a "TypeScript port of EdiabasLib" while being licensed PolyForm Noncommercial 1.0.0 — [EdiabasX repo](https://github.com/emdzej/ediabasx).
- **EU Directive 2009/24/EC** — [EUR-Lex](https://eur-lex.europa.eu/legal-content/EN/TXT/?uri=CELEX:32009L0024).
  - Art. 1(2): "Ideas and principles which underlie any element of a computer program, including those which underlie its interfaces, are not protected by copyright."
  - Art. 5(3): a lawful user may "observe, study or test the functioning of the program in order to determine the ideas and principles … while performing any of the acts of loading, displaying, running … which he is entitled to do."
  - Art. 6(1): decompilation is allowed without authorisation where "indispensable to obtain the information necessary to achieve the interoperability of an independently created computer program", if (a) done by a licensee or lawful user, (b) the information was not "readily available", and (c) it is confined to the necessary parts.
  - Art. 6(2): the information may not be used for other goals, may not be given to others except when necessary for interoperability, and may not be used for "a computer program substantially similar in its expression".
  - Art. 8: contract terms contrary to Art. 6 or 5(3) are "null and void".
- **SAS Institute v World Programming (CJEU C-406/10, 2 May 2012).** The functionality of a program, its programming language and the format of its data files are not forms of expression protected by copyright. Reproducing protected parts, however, can still infringe — [Wikipedia summary](https://en.wikipedia.org/wiki/SAS_Institute_Inc_v_World_Programming_Ltd); [SCL report](https://www.scl.org/2451-sas-institute-inc-v-world-programming-ltd-report-and-analysis/).
- **US position.** *Sega v. Accolade* (9th Cir. 1992) held that disassembling object code to reach unprotected functional elements for interoperability is fair use, including intermediate copies; *Sony v. Connectix* (2000) extended this. 17 U.S.C. §1201(f) is the DMCA's interoperability exemption — [Copyright Office summary](https://www.copyright.gov/fair-use/summaries/segaenters-accolade-9thcir1992.pdf); [Wikipedia](https://en.wikipedia.org/wiki/Sega_v._Accolade).
- **How EdiabasLib tells users to get SGBDs.** It points users to files from their own INPA/ISTA installs ([issue #175](https://github.com/uholeschak/ediabaslib/issues/175)). The Configuration Generator docs tell users to identify the `.GRP`/`.PRG` names "on the info page of each ECU in INPA" (`UP:docs/Configuration_Generator.md:14`).

### Inferences
- There are two clean licensing routes:
  - **(a)** License the Zig port GPL-3 and port EdiabasNet directly. This is simplest and legally clear, because GPL obligations apply to the port.
  - **(b)** For a non-GPL licence, do a documented clean-room. One team writes a specification from EdiabasLib, BESTDIS (MIT), black-box observation of real EDIABAS in the user's VM (Art. 5(3)) and GPL test outputs. Another team implements from that specification without reading the GPL code. Under SAS v WPL the BEST/2 bytecode format and the .prg container are a language and a data format, not protected expression. Art. 6 decompilation of BMW binaries is likely unnecessary, since the information is already "readily available" through EdiabasLib, which narrows Art. 6's applicability.
- EdiabasX's noncommercial licence on a self-described EdiabasLib port looks inconsistent with GPL-3 if code was translated. Treat that project as a reference only, not as a source to copy from.
- The 0xF7 XOR is trivial obfuscation. Whether it counts as a "technological measure" under DMCA §1201 or EU InfoSoc art. 6 is untested. Decoding it is an interoperability need, and EdiabasLib has published it openly for years. This is low risk but worth a sentence in the project's legal notes.
- Do not ship any BMW .prg/.grp, including the BMW-derived test files in upstream. Use only `cmd_test1`, `cmd_test2`, `base1`, `base2`, `cmd_ident` and `adapter_prg`, which are authored by Holeschak and covered by the GPL. Read SGBDs from the user's own EcuPath.

### Gaps
- No court decision addresses EDIABAS or SGBDs specifically.
- BMW's EDIABAS/ISTA licence terms were not found publicly, so their redistribution clauses could not be quoted.
- Whether compiled `.prg` outputs of GPL sources produced with BMW's proprietary compiler carry any BMW runtime code, such as `B2Runtim.lib` linked into BEST/2 output (`UP:Tools/BestNet/Program.cs:20`), is unknown. If they do, even the GPL test `cmd_test2.prg` may contain BMW library code; this needs checking before redistributing that fixture. `cmd_test2.prg` is linked with `test.lib`, and BEST/2 compilation references `B2Runtim.lib`.
