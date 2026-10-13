# NapCat local API integration

The module implements an independent HTTP/SSE client and a private Node host adapter. It does not copy NapCat implementation source, edit its code or binaries, embed its QQ libraries into the game, or redistribute its runtime package. The runtime downloads directly from the official upstream release after an explicit Prepare QQ Service or Robot QQ QR Login click. Automatic restore never downloads or replaces executable code.

- Source repository: https://github.com/NapNeko/NapCatQQ
- Version: v4.18.38
- Upstream tag source commit: bbd0d2aca3d470c20b33b1d4f95123335633bf26
- Official package: https://github.com/NapNeko/NapCatQQ/releases/download/v4.18.38/NapCat.Shell.Windows.Node.zip
- Size: 124,079,600 bytes
- SHA-256: b88007a373a30deefb387e7cf17911752ff900bce22c534b3da6b644f5c6d0ae
- License and usage terms: https://github.com/NapNeko/NapCatQQ/blob/v4.18.38/LICENSE
- Official Windows deployment guidance: https://napneko.github.io/guide/boot/Shell

NapCat uses a limited redistribution license and additional component-specific licenses. The repository license must not be treated as a standard unrestricted open-source license. This project distributes only its independent adapter and links users to the upstream runtime; do not rehost or publicly modify the runtime without checking all applicable permissions. QQ components remain subject to Tencent's terms. No LLBot token or third-party sign-service approval is required to start this pinned runtime and obtain a QQ QR code. QQ's own account checks still apply.

The adapter uses standard documented local APIs: /api/auth/login (SHA-256(password + .napcat)), local credential renewal, /api/QQLogin/CheckLoginStatus, /api/QQLogin/RefreshQRcode, and OneBot send_group_msg, send_private_msg, get_status, get_login_info, /_events. Only loopback addresses are used, with random independent web/API ports and credentials. HTTP redirects and proxies are disabled for local calls. Protocol stdout/stderr is not forwarded to game logs.

The independent host preloader intercepts loading of the pinned wrapper.node only inside the owned Node processes and exposes a private data-directory getter through a separate exports object. Native functions and original binaries remain unchanged; no global QQ data-directory configuration or registry is modified. NODE_OPTIONS loads the same adapter in worker processes. Account data stays in qq-data outside the versioned runtime so subsequent module updates can retain it. Dispose terminates only the process tree the module created and clears temporary local API configurations before releasing the exclusive lease. Game chat history, phone authorization and cloud push configuration remain separate.

Tests cover compilation, input feedback, no personal authorization-token requirement, archive traversal, runtime hashes, QR source host validation, QR PNG content round-trip, event filtering, HTTP credentials, mention/text segments, business failure handling, real pinned runtime launch and QQ QR generation without logging in, private data directory, Unicode/space paths, and ownership cleanup. No real account was scanned or used to send messages during automated tests.