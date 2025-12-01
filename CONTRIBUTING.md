# Contributing to FreedomNode

Obrigado por contribuir! Este guia rápido descreve o fluxo de trabalho, requisitos mínimos e práticas específicas do projeto para que as PRs sejam rápidas de revisar e fáceis de integrar.

## Começando (ambiente de desenvolvimento)

- Requisitos: .NET 10 SDK instalado (localmente). Em Windows / PowerShell:

```powershell
# Build the solution
dotnet build FalconNode.sln

# Run tests
dotnet test tests\FreedomNode.Tests
```

- Debug: abrir a solução no Visual Studio (ou usar VS Code com C# extension). `Properties/launchSettings.json` possui perfis de execução.

## Branches & Pull Requests

- Branches: siga o padrão `feature/<resumo-curto>` ou `fix/<resumo-curto>`.
- Abra PRs contra `main` (ou a branch de integração se for definida). Inclua descrição clara, motivos e captura de telas/artefatos se aplicável.
- Use mensagens de commit concisas e convencionais (opcionalmente: Conventional Commits): `feat: adiciona X`, `fix: corrige Y`, `chore: ...`.

## Testes (obrigatório)

- Sempre inclua testes cobrindo o comportamento alterado/novo. O projeto contém testes em `tests/FreedomNode.Tests`.
- Test patterns used in the repo:
  - Use `Channel.CreateBounded<T>` to simulate in/out channels for workers.
  - Allocate buffers with `ArrayPool<byte>.Shared.Rent(...)` and ensure `Return(...)` in finally blocks.
  - Note: `BlobStore.StoreAsync` now accepts `ReadOnlyMemory<byte>`; tests should pass ReadOnlyMemory where possible instead of creating intermediate arrays.
  - Retrieve methods: use `RetrieveBytesAsync` for small blobs, `RetrieveToStreamAsync` or `RetrieveToBufferAsync` for larger blobs.
  - File ingestion tests: validate `FileIngestor.IngestAsync` produces a manifest (hex string), and that `FileRetriever.ReassembleFileAsync(manifest, stream)` reassembles the original content by streaming chunks. Use temporary test directories and clean up blob files after tests.
  - Compose binary messages by writing `FixedHeader` then payload; tests use ReadFromSpan/WriteToSpan helpers.

## Revisão de PR — checklist curto

- [ ] Os novos casos são cobertos por testes automatizados (unit/integration).
- [ ] Não há vazamentos de buffer (ArrayPool rented/returned).
- [ ] Mensagens binárias seguem `FixedHeader` contract (size, types, endianness).
- [ ] Alterações de crypto ou key handling foram revisadas (NSec patterns: ed25519/x25519).
- [ ] Atualize `README.md` e `.github/copilot-instructions.md` quando grandes recursos ou abordagens mudarem.

## Padrões técnicos importantes

- Channels + BackgroundService
  - Workers are BackgroundService derived; register them in `Program.cs` using `builder.Services.AddHostedService<YourWorker>()`.
  - Inter-worker comms use `Channel<NetworkPacket>` and `Channel<OutgoingMessage>`.

- Memory management
  - Arrays are reused via `ArrayPool<byte>`. Always return buffers, prefer try/finally to ensure return even on error.

- Binary protocol
  - Messages use `FixedHeader` (FixedHeader.Size) and typed payloads. Keep write/read helpers symmetrical.
  - Message types: 0x01=Handshake, 0x02=Onion, 0x03=FindNode(req), 0x04=FindNode(res), 0x05=STORE(req), 0x06=STORE_RES(res), 0x07=FETCH(req), 0x08=FETCH_RES(res).

- File ingestion & retrieval (important):
  - `FileIngestor` splits files into 256 KiB chunks, stores each chunk using `BlobStore.StoreAsync(ReadOnlyMemory<byte>)`, then writes a small manifest JSON (also stored in BlobStore) which lists chunk hashes.
  - `FileRetriever` reassembles content by retrieving the manifest (via `RetrieveBytesAsync`) and piping each chunk into the provided stream using `RetrieveToStreamAsync`.

- Crypto
  - NSec keys are used for ed25519 (sign/verify) and X25519 (session keys). Keep identity keys and onion keys separate.

## Como adicionar um novo worker

1. Crie um novo arquivo `src/Workers/SeuWorker.cs` implementando `BackgroundService`.
2. Use DI to accept channels or singletons (PeerTable, RoutingTable, BlobStore) that your worker needs.
3. Registre o worker no `Program.cs` via `AddHostedService<SeuWorker>()`.
4. Escreva testes que usem bounded channels and NullLogger to exercise the loop.

Note: `NodeLogicWorker` constructor now requires `BlobStore` and `FileIngestor` in addition to the channels and tables. Tests should create and inject a `BlobStore` and `FileIngestor` (use `NullLogger` variants) and construct NodeSettings with a port parameter where necessary.

UI / Debug mode note:

- The `TerminalUi` is registered when the application runs with `--debug`. It relies on `NodeLogicWorker` as a singleton and also requires `FileIngestor` and `FileRetriever`. Tests that exercise the UI logic can instantiate these dependencies and call the UI handlers directly (e.g., `HandleUpload`, `HandleFetch`) or run the UI in a background thread to simulate interactive flows.

## Como adicionar uma nova mensagem tipo

1. Defina estrutura/size e helpers `ReadFromSpan/WriteToSpan` em `src/Core/Messages`.
2. Atualize `NodeLogicWorker` switch/handler para a nova message type.
3. Adicione testes que construam buffers com `FixedHeader` + payload e assertem o comportamento.

Exemplo (documentado) — Ping/Pong (exemplo mínimo, *docs-only*):

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
