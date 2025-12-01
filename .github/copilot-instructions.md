<!-- concise Copilot instructions for contributors and AI agents -->
# Copilot / AI Agent Instructions — FreedomNode (short)

Purpose: help AI contributors be immediately productive — show where the code lives, how the pieces communicate, and the precise patterns to follow.

Key areas (what you'll want to read first)
- Core protocol & crypto: src/Core/ — look at `Crypto/CryptoHelper.cs` and `Crypto/OnionBuilder.cs` for crypto primitives and onion construction.
- Network shapes: `src/Core/Network/FixedHeader.cs` and `src/Core/Messages/*` define the wire format used across the app.
- Storage & FS: `src/Core/Storage/BlobStore.cs`, `src/Core/FS/FileIngestor.cs` and `src/Core/FS/FileRetriever.cs` show how files are chunked and persisted.
- Workers & communication: `src/Workers/` contains the core services (NodeLogicWorker, QuicListenerWorker, ConnectionManagerWorker). Inter-worker messaging uses Channels (see `Program.cs`).

Developer workflows (quick, exact commands)
- Build: dotnet build FalconNode.sln
- Run app locally (development): dotnet run --project FalconNode.csproj
- Run unit tests: dotnet test tests/FreedomNode.Tests --no-build
- Run a single test (example): dotnet test tests/FreedomNode.Tests -t "FixedHeaderTests"
- Debug: open project in an IDE (Visual Studio / VS Code) and use `Properties/launchSettings.json` or run from `bin/Debug/net10.0`. For QUIC/native bindings be mindful of platform runtime assets under `bin/Debug/net10.0/runtimes`.

Important code patterns to preserve
- Channels & worker design: the app wires two bounded channels in `Program.cs` — `Channel<NetworkPacket>` (incoming) and `Channel<OutgoingMessage>` (outgoing). Workers act as BackgroundService readers/writers; tests instantiate bounded channels to exercise worker loops (see tests/NodeLogicWorkerTests.cs).
- Buffer ownership: the code consistently rents buffers from `ArrayPool<byte>.Shared` and stores original buffer references inside `NetworkPacket`/OutgoingMessage records. Always return rented buffers in finally blocks. Tests show correct patterns (FixedHeaderTests.cs, NodeLogicWorkerTests.cs).
- Wire format: payloads are built as [FixedHeader | payload]. FixedHeader is 16 bytes. Message type mapping is used throughout (examples in `NodeLogicWorker`):
	- 0x01 — Handshake (HandshakePayload.Size)
	- 0x02 — Onion route / relay
	- 0x03 — DHT FindNode request
	- 0x04 — DHT FindNode response
	- 0x06 — Store response (hash returned)
	- 0x08 — Fetch response (payload chunks)
	- 0x05 — control/debug messages used by TerminalUI for ad-hoc testing

- Crypto primitives: the repo uses NSec for Ed25519 / X25519 — inspect `src/Core/Crypto/CryptoHelper.cs` and tests in `CryptoHelperTests.cs` for usage patterns (sign/verify, ephemeral key usage in onions).

How to add features (concrete checklist)
1) New worker:
	- Add under `src/Workers`, implement `BackgroundService`.
	- Accept DI for `Channel<NetworkPacket>` / `Channel<OutgoingMessage>` and singletons (PeerTable, RoutingTable, BlobStore) as needed.
	- Register it in `Program.cs`. Mirror patterns used by `QuicListenerWorker` and `ConnectionManagerWorker`.
2) New message type:
	- Declare payload helpers in `src/Core/Messages` (ReadFromSpan/WriteToSpan).
	- Update `NodeLogicWorker.ProcessPacket` handlers and add tests in `tests/FreedomNode.Tests` that build buffers using `FixedHeader` + payload.
	- Use ArrayPool in tests consistent with existing patterns and ensure buffers are returned.
3) Storage/FS updates:
	- `FileIngestor` splits files into 256 KiB chunks. Update BlobStore & tests when changing this behavior.

Tests & examples (where to find code you can mimic)
- Tests are in `tests/FreedomNode.Tests` — pay attention to: `FixedHeaderTests.cs`, `HandshakePayloadTests.cs`, `BlobStoreTests.cs`, `NodeLogicWorkerTests.cs`, `OnionBuilderTests.cs`, and `OnionMultiHopTests.cs`.
- Tests show best-practices: rent buffers, write `FixedHeader` then payload, pass Memory/ReadOnlyMemory to APIs, inject `NullLogger<T>` to reduce noise, and use bounded channels to simulate network I/O.

End-to-end example (new message type)
- Example added: simple Ping/Pong message to illustrate adding a new message type end-to-end:
	- Request: `src/Core/Messages/PingPayload.cs` (message type 0x09, 4 byte uint payload)
	- Response: `src/Core/Messages/PongPayload.cs` (message type 0x0A, echo payload)
	- Handler: `src/Workers/NodeLogicWorker.cs` (added `HandlePing` which reads Ping and writes Pong using ArrayPool)
	- Test: `tests/FreedomNode.Tests/PingPongTests.cs` demonstrates the pattern: rent buffer, write header + payload, inject into incoming channel, assert outgoing response header & payload.

Checklist to add a new message type (quick):
1. Add payload helpers to `src/Core/Messages` with ReadFromSpan/WriteToSpan + Size constant.
2. Add handler in `NodeLogicWorker.ProcessPacket` (follow buffer rent/return patterns).
3. Write a test under `tests/FreedomNode.Tests` that constructs [FixedHeader | payload] via ArrayPool, writes a `NetworkPacket` into the incoming channel and asserts an `OutgoingMessage` on outgoing channel.

NOTES / gotchas
- Target runtime: the project targets net10.0 and includes platform-specific native runtime assets under `bin/Debug/net10.0/runtimes`. QUIC native dependencies may fail on unsupported platforms — tests & CI use the managed code paths unless configured.
- QUIC specifics: `ConnectionManagerWorker` and `QuicListenerWorker` use built-in QUIC APIs and set `ApplicationProtocols` to "freedom-v1". Local dev uses `RemoteCertificateValidationCallback = true` for convenience — production code should validate certs accordingly.

If anything is ambiguous or you want an example of a specific pattern (buffer lifecycle, new message addition, or worker template), point to the file/area and I’ll add a short, concrete snippet or test scaffold.

If anything is ambiguous here, point to the specific file/area and I’ll update the instructions with examples or missing details.
