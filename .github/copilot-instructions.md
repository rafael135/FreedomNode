<!-- concise Copilot instructions for contributors and AI agents -->
# Copilot / AI Agent Instructions — FreedomNode (short)

Purpose: help AI contributors be immediately productive — focus on where code lives, how components talk, and common dev flows.

Key areas
- Core protocol & crypto: src/Core/ (Crypto, Dht, Messages, Network, State). Examples: `HandshakePayload`, `FixedHeader`, `OnionBuilder`.
- Storage: src/Storage/ (BlobStore) — used by NodeLogicWorker for store/fetch.
- Workers: src/Workers/ (NodeLogicWorker, ConnectionManagerWorker, QuicListenerWorker). Workers are BackgroundService and registered in `Program.cs` via AddHostedService.

Developer workflows (quick)
- Build solution: dotnet build FalconNode.sln
- Run locally: dotnet run --project FalconNode.csproj
- Tests: dotnet test tests/FreedomNode.Tests
- Debug: IDEs use `Properties/launchSettings.json` and appsettings*.json in the project root / bin/Debug/net10.0.

Important code patterns to preserve
- Channels + BackgroundService: inter-worker comms use Channel<T> in `Program.cs` (inChannel/outChannel). Tests create bounded channels to simulate worker I/O.
- Memory management: code uses ArrayPool<byte>.Shared.Rent/Return a lot — always return rented buffers (both in production code and tests) to avoid memory leaks.
- Binary protocol: messages use `FixedHeader` (FixedHeader.Size) followed by typed payloads (Handshake = 0x01, Onion = 0x02, DHT FindNode req = 0x03, FindNode res = 0x04, Store/Fetch flows use 0x06/0x08 examples). Use helper ReadFromSpan/WriteToSpan in tests.
- Crypto primitives: NSec is used for Ed25519/X25519 (SignatureAlgorithm.Ed25519, KeyAgreementAlgorithm.X25519). Look at NodeLogicWorker and CryptoHelper for pattern usage (session keys, Verify/Sign).

How to add features
- New worker: add class under src/Workers, implement BackgroundService or IHostedService, and register in Program.cs with builder.Services.AddHostedService<YourWorker>(). Use channels & DI for shared state.
- New message type: add to src/Core/Messages, define payload Read/Write helpers, update NodeLogicWorker switch on MessageType and tests under tests/ to exercise the path.
- Storage change: use BlobStore for persistence and update BlobStoreTests.cs for behaviour expectations.

Tests & examples
- Tests demonstrate calling StartAsync/StopAsync on a worker and writing NetworkPacket entries into channels. Use NullLogger for logging in tests.
- Example packets in tests use: allocate buffer (ArrayPool), write FixedHeader then payload, wrap into NetworkPacket with original buffer reference, write to channel and assert outgoing channel messages.

NOTES / gotchas
- Target framework & runtime: project builds for net10.0 and ships native runtimes under bin/Debug/net10.0/runtimes — be mindful when testing QUIC native bindings.
- QUIC connections in ConnectionManagerWorker use SslClientAuthenticationOptions with ApplicationProtocols = "freedom-v1" and RemoteCertificateValidationCallback = true for dev flows.

If anything is ambiguous here, point to the specific file/area and I’ll update the instructions with examples or missing details.
