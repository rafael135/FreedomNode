# Contributing to FreedomNode

[English](./CONTRIBUTING.md) • [Português (pt-BR)](./CONTRIBUTING.pt.md)

Thanks for contributing! This quick guide explains the workflow, minimum requirements and repository conventions to keep PRs easy to review and merge.

## Getting started (development)

- Requirements: .NET 10 SDK installed.

```bash
# build the solution
dotnet build FalconNode.sln

# run unit tests
dotnet test tests/FreedomNode.Tests
```

- Debug: open the solution in Visual Studio or use VS Code with the C# extension. `Properties/launchSettings.json` contains run/debug profiles.

## Branches & pull requests

- Branch naming: prefer `feature/<short-summary>` or `fix/<short-summary>`.
- Open PRs against `main` (or the integration branch if defined). Include a clear description, motivation and any relevant screenshots or artifacts.
- Commit messages: keep them concise and consider using Conventional Commits: `feat: ...`, `fix: ...`, `chore: ...`.

## Tests (required)

- Always add tests for changed/new behaviour. Tests live under `tests/FreedomNode.Tests`.

Test patterns used in the repo:

- Use `Channel.CreateBounded<T>` to simulate in/out channels for workers.
- Allocate buffers with `ArrayPool<byte>.Shared.Rent(...)` and ensure `ArrayPool<byte>.Shared.Return(...)` in finally blocks.
- `BlobStore.StoreAsync` accepts `ReadOnlyMemory<byte>` — prefer passing ReadOnlyMemory where possible to avoid temporary allocations.
- For retrieval: prefer `RetrieveBytesAsync` for small blobs, `RetrieveToStreamAsync` and `RetrieveToBufferAsync` for larger payloads.
- File ingestion tests: verify that `FileIngestor.IngestAsync` produces a manifest (hex string) and that `FileRetriever.ReassembleFileAsync(manifest, stream)` reassembles the original content by streaming chunks. Use temporary test directories and clean up created blob files after tests.
- For message tests: compose binary packets by writing `FixedHeader` followed by payload and use ReadFromSpan/WriteToSpan helpers.

## Pull request checklist

- [ ] New behaviour is covered by automated tests (unit/integration).
- [ ] No buffer leaks (ArrayPool rented/returned).
- [ ] Binary messages comply with the `FixedHeader` contract (size, types, endianness).
- [ ] Crypto or key handling changes were reviewed (NSec patterns: ed25519/x25519).
- [ ] Update `README.md` and `.github/copilot-instructions.md` if the change impacts architecture or contributor guidelines.

## Key technical patterns

### Channels + BackgroundService

- Workers are implemented as BackgroundService derived types; register them in `Program.cs` with `builder.Services.AddHostedService<YourWorker>()`.
- Inter-worker communication uses `Channel<NetworkPacket>` and `Channel<OutgoingMessage>`.

### Memory management

- Arrays are reused via `ArrayPool<byte>`. Always return rented buffers; prefer try/finally to ensure buffers are returned even on error.

### Binary protocol

- Messages are built using `FixedHeader` (FixedHeader.Size) followed by typed payloads. Keep Read/Write helpers symmetrical.
- Message types used by NodeLogicWorker are: 0x01=Handshake, 0x02=Onion, 0x03=FindNode(req), 0x04=FindNode(res), 0x05=STORE(req), 0x06=STORE_RES(res), 0x07=FETCH(req), 0x08=FETCH_RES(res).

### File ingestion & retrieval

- `FileIngestor` splits files into 256 KiB chunks, stores each chunk via `BlobStore.StoreAsync(ReadOnlyMemory<byte>)`, and writes a small manifest JSON (also stored in BlobStore) that lists chunk hashes.
- `FileRetriever` reassembles content by retrieving the manifest (`RetrieveBytesAsync`) and piping each retrieved chunk into the provided stream (`RetrieveToStreamAsync`).

### Crypto

- NSec is used for ed25519 (sign/verify) and X25519 (session keys). Keep identity keys and onion keys separate.

### XML Documentation

- All public classes, methods, properties, and constructors must have XML documentation comments (triple-slash `///`).
- Class summaries: use single-line `<summary>` for hover tooltip compatibility.
- Complex behavior: add `<remarks>` sections with implementation details, threading notes, and usage guidance.
- Parameters: document all parameters with `<param>` tags, including validation rules and null handling.
- Return values: use `<returns>` to describe what the method returns, including null/empty cases.
- Examples: prefer concise, actionable descriptions over verbose explanations.

Example pattern:

```csharp
/// <summary>
/// Stores a blob using content-addressed storage with ChaCha20-Poly1305 encryption.
/// </summary>
/// <remarks>
/// Writes are atomic: a temporary file is created and renamed on success.
/// Concurrent writes for the same content are tolerated (idempotent).
/// </remarks>
/// <param name="data">The plaintext data to encrypt and store.</param>
/// <returns>The SHA-256 content hash (32 bytes) used as the blob identifier.</returns>
public async Task<byte[]> StoreAsync(ReadOnlyMemory<byte> data) { ... }
```

## Adding a new worker

1. Create a new file under `src/Workers/YourWorker.cs` implementing `BackgroundService`.
2. Accept dependencies (channels, singletons like PeerTable, RoutingTable, BlobStore) via DI.
3. Register the worker in `Program.cs` with `AddHostedService<YourWorker>()`.
4. Provide tests using bounded channels and `NullLogger` to verify the background loop.

Note: NodeLogicWorker now requires `BlobStore` and `FileIngestor` in addition to channels and tables. Tests should create and inject `BlobStore` and `FileIngestor` (use `NullLogger` variants) and construct `NodeSettings` with a port parameter where required.

## UI / Debug mode note

- The interactive `TerminalUi` is registered when the application runs with `--debug`. It relies on `NodeLogicWorker` as a singleton and also uses `FileIngestor` and `FileRetriever`. Tests that exercise the UI can instantiate these dependencies and invoke UI handlers directly (e.g. `HandleUpload`, `HandleFetch`) or run the UI in a background thread to simulate interactive flows.

## Adding a new message type

1. Define the payload helpers with `ReadFromSpan/WriteToSpan` in `src/Core/Messages` and a `Size` constant.
2. Update `NodeLogicWorker` message switch/handlers for the new message type.
3. Add tests that construct [FixedHeader | payload] buffers and assert the expected behavior.

Example (Ping/Pong docs-only example preserved here):

```csharp
// src/Core/Messages/PingPayload.cs (example)
public readonly struct PingPayload
{
  public const int Size = 4; // 4 bytes (uint)
  public readonly uint Value;

  public PingPayload(uint value) => Value = value;

  public static PingPayload ReadFromSpan(ReadOnlySpan<byte> src) =>
    new PingPayload(BinaryPrimitives.ReadUInt32BigEndian(src));

  public void WriteToSpan(Span<byte> dst) =>
    BinaryPrimitives.WriteUInt32BigEndian(dst, Value);
}

// src/Core/Messages/PongPayload.cs (example)
public readonly struct PongPayload
{
  public const int Size = 4; // 4 bytes (uint)
  public readonly uint Value;

  public PongPayload(uint value) => Value = value;

  public static PongPayload ReadFromSpan(ReadOnlySpan<byte> src) =>
    new PongPayload(BinaryPrimitives.ReadUInt32BigEndian(src));

  public void WriteToSpan(Span<byte> dst) =>
    BinaryPrimitives.WriteUInt32BigEndian(dst, Value);
}

// NodeLogicWorker.cs (example handler code — simplified)
// Add this to your message switch: case 0x09: await HandlePing(packet); break;
private async Task HandlePing(NetworkPacket packet)
{
  if (packet.Payload.Length < PingPayload.Size)
    return; // invalid payload

  var ping = PingPayload.ReadFromSpan(packet.Payload.Span);

  // build pong response
  var pong = new PongPayload(ping.Value);
  int responseSize = FixedHeader.Size + PongPayload.Size;
  byte[] buffer = ArrayPool<byte>.Shared.Rent(responseSize);

  try
  {
    // header: 0x0A = PONG
    new FixedHeader(0x0A, packet.RequestId, (uint)PongPayload.Size)
      .WriteToSpan(buffer.AsSpan(0, FixedHeader.Size));

    // payload
    pong.WriteToSpan(buffer.AsSpan(FixedHeader.Size));

    _outgoingWriter.TryWrite(new OutgoingMessage(packet.Origin, buffer.AsMemory(0, responseSize), buffer));
  }
  catch
  {
    // On error, return buffer immediately
    ArrayPool<byte>.Shared.Return(buffer);
    throw;
  }
}

// Test (example)
// - Rent buffer with FixedHeader.Size + PingPayload.Size
// - Write FixedHeader (0x09, request id) + Ping payload (4 bytes)
// - Create NetworkPacket and write to inChannel
// - Assert outgoing message has header 0x0A and payload matching the original ping value

// Note: this example is intentionally documented here — keep it docs-only unless you want the change to be live.
```

## Problemas conhecidos / dicas práticas

- QUIC: runtime nativo pode ser necessário para testes e integração com QUIC. Em dev, `ConnectionManagerWorker` aceita certificados sem validação (dev-only) via `RemoteCertificateValidationCallback`.
- Para blobs grandes: este MVP grava blobs inteiros; ao alterar para chunking, atualize `BlobStore` e adicione testes de compatibilidade.

## Contato e suporte

- Se tiver dúvidas ou dúvidas técnicas sobre a arquitetura (DHT / onion / crypto), abra uma issue descrevendo o contexto e os nós envolvidos.
- Para alterações grandes (alterar formatos de mensagem, crypto primitives, ou design de storage) antes de implementar, abra um RFC (issue) para discutir.

Agradecemos a sua contribuição — busque manter PRs pequenos e testáveis para acelerar a revisão.
