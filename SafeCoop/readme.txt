SafeCoop Prototype 0.4.0
========================

This is an open-source development build. It is not playable co-op yet.

What this build does
--------------------
- Loads through Captain of Industry's official Mods loader.
- Can make a direct, encrypted Tailscale host/join connection to one selected peer.
- Checks the Captain of Industry version, SafeCoop version, the SHA-256 fingerprint of the loaded save, and a private session code before accepting that peer.
- Binds only to the configured Tailscale address; it never opens a router port or cloud server.
- Captures save-affecting actions at scheduling time, serializes them with the game's own serializer, and sends a bounded in-memory payload to the verified peer.
- Applies a received action only on the game's input-update thread and never sends it back to its origin.
- Keeps the host waiting after a disconnect and makes a joining player retry while the host is loading.
- Reports local session state to the SafeCoop launcher: waiting, connected, disconnected, or refused.

What this build does not do
---------------------------
- It is experimental software. Use it only with a copied test save.
- It does not use BepInEx, winhttp.dll, native code, process launching, or raw-memory access.
- Its command and input-update observers are explicitly non-saveable and cannot be written into a game save.

Use only a disposable copied save. Keep normal backups until full multi-action testing is complete.
