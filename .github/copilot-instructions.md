# FreedomNode — AI Agent Instructions

C#/.NET decentralized network node (MVP/proof-of-concept). Targets **net10.0**.

## Architecture (quick orientation)

```
Program.cs → wires DI, creates bounded Channel<NetworkPacket> (in) + Channel<OutgoingMessage> (out)
src/Workers/   → BackgroundService consumers: QuicListenerWorker, ConnectionManagerWorker, NodeLogicWorker
src/Core/      → protocol primitives (Crypto, DHT, FS, Messages, Network, State, Storage)
```

**Data flow**: QUIC listener → incoming channel → `NodeLogicWorker.ProcessPacket` → outgoing channel → connection manager → peer.

## Developer commands

| Task | Command |
|------|---------|
| Build | `dotnet build FalconNode.sln` |
| Run | `dotnet run --project FalconNode.csproj` |
| Run with debug UI | `dotnet run -- --debug --port 5001 --seed 5000` |
| All tests | `dotnet test tests/FreedomNode.Tests` |
| Single test | `dotnet test tests/FreedomNode.Tests --filter FixedHeaderTests` |

## Wire format (critical)

All messages: `[FixedHeader 16 bytes | payload]`. See `src/Core/Network/FixedHeader.cs`.

| Type | Hex | Purpose |
|------|-----|---------|
| Handshake | 0x01 | Auth exchange (136-byte `HandshakePayload`) |
| Onion | 0x02 | Encrypted relay |
| DHT FindNode | 0x03 | Request |
| DHT FindNode Response | 0x04 | Response |
| Store | 0x05 | Store blob request |
| Store Response | 0x06 | Returns 32-byte hash |
| Fetch | 0x07 | Request blob by hash |
| Fetch Response | 0x08 | Blob chunks |

## Patterns to follow

1. **Buffer ownership** — Rent from `ArrayPool<byte>.Shared`, store original ref in `NetworkPacket.OriginalBufferReference` / `OutgoingMessage`, return in `finally`. See `NodeLogicWorker.ExecuteAsync` and `FixedHeaderTests.cs`.

2. **Payload structs** — Use `ReadFromSpan` / `WriteToSpan` + `const int Size`. Example: `HandshakePayload.cs`.

3. **New message type checklist**:
   - Add payload struct in `src/Core/Messages/` with Span methods
   - Add case in `NodeLogicWorker.ProcessPacket` switch
   - Add test in `tests/FreedomNode.Tests/` using bounded channels + ArrayPool

4. **New worker** — Implement `BackgroundService`, inject channels + singletons via DI, register in `Program.cs`.

5. **Crypto** — NSec library: Ed25519 (signatures), X25519 (key agreement), ChaCha20-Poly1305 (AEAD). See `CryptoHelper.cs`, `OnionBuilder.cs`.

6. **Storage** — `BlobStore` uses SHA-256 content addressing; `FileIngestor` chunks at 256 KiB.

## Key files to study

- `Program.cs` — DI wiring, channel setup, worker registration
- `src/Workers/NodeLogicWorker.cs` — main packet switch, request/response handling
- `src/Core/Network/FixedHeader.cs` — wire format struct
- `tests/FreedomNode.Tests/NodeLogicWorkerTests.cs` — full worker test with channels
- `tests/FreedomNode.Tests/FixedHeaderTests.cs` — buffer rent/return pattern

## Gotchas

- **QUIC natives**: Platform-specific runtimes under `bin/Debug/net10.0/runtimes/`. Tests use managed paths.
- **ApplicationProtocol**: QUIC uses `"freedom-v1"`.
- **Debug cert validation**: Dev mode skips cert validation — don't copy to production.
