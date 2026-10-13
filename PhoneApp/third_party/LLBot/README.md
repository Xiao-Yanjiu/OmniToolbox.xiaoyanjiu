Historical backend for module 0.3.37. Current modules use NapCat and do not load LLBot.

# LLBot local integration

The module implements its own HTTP/SSE client and manages an independent, hidden local process. It does not copy LLBot source code, link LLBot into the game, inject QQ, or redistribute the runtime package. The runtime downloads directly from the official release only after the user explicitly clicks Prepare LLBot or Robot QQ QR Login. Automatic restore does not download or replace executable code.

- Official repository: https://github.com/LLOneBot/LuckyLilliaBot
- Official version: v8.3.2
- Official Windows package: https://github.com/LLOneBot/LuckyLilliaBot/releases/download/v8.3.2/LuckyLillia-CLI-win-x64.zip
- Package SHA-256: 69cf61a40b406009314dfd1eb1d4da4662c6327204fe13d193d9b6889f4ecfe2
- Package size: 95,714,293 bytes
- Official authorization: https://auth.luckylillia.com
- Source LICENSE: https://github.com/LLOneBot/LuckyLilliaBot/blob/v8.3.2/LICENSE (GPL-2.0)
- Other components in the upstream runtime, including its native sign-proxy and Node.js, retain their own licenses. The source repository license must not be assumed to authorize redistribution of every bundled component.

The module checks the official archive SHA-256 and validates extraction paths. It records runtime file hashes and verifies them before each start; only its generated default configuration is excluded. The module owns and stops only the process it created. Both local APIs bind to 127.0.0.1 with random per-start credentials and unused local ports; redirects and proxies are disabled for local calls. Account quick-login files and databases remain in the private runtime data directory; the upstream implementation encrypts QQ session fields. The module's personal authorization token is DPAPI protected and passed via child process environment, never via command arguments or public source. The child stdout/stderr is not forwarded to game logs.

Current module remains a source module for the existing online manifest. LLBot's Node.js and native .node dependencies run in its own process. Accepting DLL modules does not make these components in-process C# libraries.

Tests cover module compilation, archive traversal protection, input validation, QR PNG checks, reply event selection, local API authentication, literal text/mention segments, API business failures, and real official CLI startup without any account or authorization. Real QQ QR login, delivery, reconnection with valid credentials and in-game replies still require user-side verification.