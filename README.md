# FreedomNode

[English](./README.md) • [Português (pt-BR)](./README.pt.md)

FreedomNode is a C#/.NET MVP (proof of concept) for decentralized networks. The primary goal is to prototype protocol building blocks (DHT, onion routing, authenticated handshakes) and validate binary message formats and flows before implementing a production-grade version in Rust.

## Project goal

This node is intended to serve as the infrastructure foundation for a decentralized social network. Each instance (node) can host and store user content such as messages, images, videos and other files. The MVP focuses on proving the essential technical building blocks—identity, authenticated handshakes, DHT routing, and content-addressed storage using SHA-256—before a high-performance Rust implementation.

## Overview

- Core: protocol logic including crypto primitives, DHT, messages, networking and state management.
- Storage: persistence helpers and storage-related message handling.
- Workers: background services for long-running tasks like connection management, node logic and QUIC listening.
- Program.cs: application entrypoint — registers services and workers and wires the in/out channels used by the workers.

## Architecture & data flow

1. Startup initializes `NodeSettings` (random NodeId + configured ports) and registers singletons: `PeerTable`, `RoutingTable`, `BlobStore`, `FileIngestor`.
2. Two bounded channels connect workers: `Channel<NetworkPacket>` (incoming) and `Channel<OutgoingMessage>` (outgoing).
3. `QuicListenerWorker` accepts QUIC connections/streams, reads `FixedHeader` + payload, and publishes a `NetworkPacket` to the incoming channel.
4. `NodeLogicWorker` consumes `NetworkPacket`s and performs packet handling (handshake, onion peeling, DHT operations, STORE/FETCH), writing responses and forwarded messages to the outgoing channel.
5. `ConnectionManagerWorker` maintains active QUIC connections and sends `OutgoingMessage`s to peers.

## Message format (important)

- FixedHeader — 16 bytes (big-endian where applicable):
  - Version (1 byte)
  - Flags (1 byte)
  - MessageType (1 byte) — examples used by NodeLogicWorker:
    - 0x01 = Handshake
    - 0x02 = Onion
    - 0x03 = DHT FindNode (request)
    - 0x04 = DHT FindNode (response)
    - 0x05 = STORE (store request)
    - 0x06 = STORE_RES (store result / response)
    - 0x07 = FETCH (fetch request)
    - 0x08 = FETCH_RES (fetch response)
  - Reserved (1 byte)
  - RequestId (4 bytes)
  - PayloadLength (4 bytes)
  - Checksum (CRC32, 4 bytes)

- HandshakePayload — 136 bytes: 32 (identity key) + 32 (onion key) + 8 (timestamp) + 64 (ed25519 signature). Use `HandshakePayload.WriteSignableBytes` to build the bytes that are signed.

## Cryptography & primitives

- Signatures: ed25519 (NSec)
- Key agreement: X25519 (ECDH)
- KDF: HKDF-SHA256
- AEAD: ChaCha20-Poly1305 (onion layers)

## How to run (development)

- Build (requires .NET 10 SDK):

```bash
dotnet build FalconNode.sln
```

- Run (the app will start the workers):

```bash
dotnet run --project FalconNode.csproj
```

- Debug: open the solution in Visual Studio or VS Code. `Properties/launchSettings.json` contains run/debug profiles.

## Debug mode & Terminal UI (interactive)

- Program supports runtime flags: `--port <port>`, `--seed <seedPort>` and `--debug`.
- In debug mode the application registers a small interactive `TerminalUi` (see `src/UI/TerminalUI.cs`) that uses the same `NodeLogicWorker` singleton to craft handshakes, publish files and create ad-hoc packets for manual testing.

Example:

```bash
dotnet run -- --debug --port 5001 --seed 5000
```

Interactive `TerminalUi` commands:
- connect <port> — send a handshake to loopback:<port>
- upload <text> — create a small file, ingest + upload via `FileIngestor`, returns manifest hash
- fetch <manifestHash> — reassemble manifest via `FileRetriever` and display content
- send-store <port> <text> — craft a STORE request (0x05) and send to a remote port (useful for manual protocol experiments)

## Observability & performance patterns

- Heavy use of `ArrayPool<byte>` reduces GC pressure — preserve buffer ownership and ensure rented buffers are returned in finally blocks.
- Logging is performed via `ILogger<T>`; `NodeLogicWorker` logs important events (handshake activity, DHT operations, onion processing).
- Channels are bounded (size 2000) and use wait behaviour when full.

## Storage

- `BlobStore` persists blobs under `AppContext.BaseDirectory/data/blobs/` using the hex SHA-256 fingerprint as filename. All blobs are encrypted with ChaCha20-Poly1305 using the node's storage key before being written to disk. Writes use a temporary file + atomic rename.

API notes (current implementation):

- `StoreAsync(ReadOnlyMemory<byte>)`: accepts ReadOnlyMemory to avoid allocations.
- `RetrieveBytesAsync(byte[] hash)`: returns a byte[] (good for small manifests/metadata).
- `RetrieveToStreamAsync(byte[] hash, Stream target)`: writes a blob directly into a Stream for large payloads.
- `RetrieveToBufferAsync(byte[] hash, Memory<byte> destination)`: read directly into an existing buffer.
- `HasBlob` / `GetBlobSize` for quick existence checks.

File ingestion and publishing

- `FileIngestor` splits files into 256 KiB chunks and writes each chunk as a blob, then stores a small JSON manifest with the list of chunk hashes (see `src/Core/FS/FileIngestor.cs`). `NodeLogicWorker` calls `FileIngestor.IngestAsync` for `PublishMessageAsync`, returning a manifest ID that can be propagated in the DHT.

### Media storage considerations (messages, images, video)

- The project supports chunked ingestion via `FileIngestor` — files are processed in fixed-size blocks (256 KiB), each chunk becomes an independent blob and a manifest JSON lists the ordered chunk hashes.
- `FileRetriever` reconstructs files by retrieving the manifest and streaming each chunk into the output `Stream` (see `src/Core/FS/FileRetriever.cs`).

Current limits & behaviour

- `QuicListenerWorker` still imposes a limit on per-packet payload size (see `QuicListenerWorker.cs`); for large files use the FileIngestor + manifest / FETCH flows.
- `BlobStore` no longer must accept full files for large media — chunking plus a small manifest reduces memory usage and enables efficient streaming back to clients.

Production guidelines / porting to Rust

1. Keep chunking + streaming for large files and validate cross-language manifest compatibility.
2. Implement discovery and retrieval of missing chunks via DHT/network (the current reassembler only reads local storage).
3. Add retention policies, quotas and replication/peering policies for availability.

Practical Rust mapping suggestions:

- Async runtime: tokio
- QUIC: quinn
- Crypto: ed25519-dalek, x25519-dalek, chacha20poly1305, hkdf
- Channels: tokio::sync::mpsc (bounded)
- Buffer reuse: bytes::BytesMut or a pool

Incremental port checklist:

1. Implement FixedHeader & HandshakePayload with tests that validate binary compatibility.
2. Implement BlobStore in Rust and validate with blobs created by the C# implementation.
3. Port crypto helper primitives and validate with the C# vectors.
4. Port NodeLogic flow + channels and add integration tests with QUIC.

## Tests & recommendations

- Automated unit tests live under `tests/FreedomNode.Tests` and cover core behaviors (FixedHeader, HandshakePayload, NodeLogicWorker flows, BlobStore helpers). Run them locally with:

```bash
dotnet test tests/FreedomNode.Tests
```

- Recommended extra tests: onion-layer round-trip, large-blob chunking, and integration tests that exercise QUIC when native runtimes are available.

## Documentation standards

- All public classes, methods, and properties have XML documentation comments (`///`) for IntelliSense and hover tooltips.
- Class summaries are single-line for compatibility; detailed behavior is documented in `<remarks>` sections.
- See `CONTRIBUTING.md` for XML documentation patterns and examples.

---

Contributions

- Want to contribute? See `CONTRIBUTING.md` for contribution guidelines, tests and a review checklist.

Helpful references

- Developer guidelines and suggested patterns are documented in `.github/copilot-instructions.md`.

---

Quick notes on adding new features here:

- To add a new worker: create a `BackgroundService` under `src/Workers/` and register it in `Program.cs`.
- To add a new message type: create the payload helpers in `src/Core/Messages` (ReadFromSpan/WriteToSpan), update `NodeLogicWorker` handlers and add tests.

---
