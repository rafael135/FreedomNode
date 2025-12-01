# FreedomNode

FreedomNode é um MVP (prova de conceito) em C#/.NET para redes descentralizadas. O objetivo primário é prototipar modelos de protocolo (DHT, onion-routing, handshakes) e validar formatos binários e fluxos antes de implementar uma versão de produção em Rust.

### Objetivo do projeto

Este nó é pensado para ser a base de infraestrutura de uma rede social descentralizada: cada instância (nó) atua como host/armazenamento para conteúdos do tipo mensagens, imagens, vídeos e outros ficheiros enviados pelos usuários. O MVP concentra-se em provar os blocos técnicos essenciais — identidade, handshakes autenticados, roteamento DHT e armazenamento content-addressed via SHA-256 — antes de migrar para uma implementação de alta performance em Rust.

## Visão Geral

- **Core:** Lógica principal do protocolo, incluindo criptografia, DHT, mensagens, rede e gerenciamento de estado.
- **Storage:** Persistência de dados e manipulação de mensagens de armazenamento.
- **Workers:** Workers para tarefas de longa duração, como gerenciamento de conexões, lógica de nó e escuta QUIC.
- **Program.cs:** Ponto de entrada da aplicação, responsável por inicializar serviços e workers.

## Arquitetura & fluxo de dados

1. Startup inicializa `NodeSettings` (NodeId aleatório + configured ports) e registra singletons: `PeerTable`, `RoutingTable`, `BlobStore`, `FileIngestor`.
2. Dois canais bounded (entrada e saída) atravessam os workers: `Channel<NetworkPacket>` (entrada) e `Channel<OutgoingMessage>` (saída).
3. `QuicListenerWorker` aceita conexões/streams QUIC, lê um `FixedHeader` + payload, e publica `NetworkPacket` no canal de entrada.
4. `NodeLogicWorker` consome `NetworkPacket`s, aplica processamento (handshake, onion peeling, DHT, store/fetch) e escreve respostas/encaminhamentos no canal de saída.
5. `ConnectionManagerWorker` mantém conexões QUIC ativas e envia `OutgoingMessage`s para peers.

## Formato da mensagem (importante)

- FixedHeader — 16 bytes (big-endian onde aplicável):
  - Version (1 byte)
  - Flags (1 byte)
  - MessageType (1 byte) — examples used by NodeLogicWorker:
    - 0x01 = Handshake
    - 0x02 = Onion
    - 0x03 = DHT find node (request)
    - 0x04 = FindNode response
    - 0x05 = STORE (store request)
    - 0x06 = STORE_RES (store result / response)
    - 0x07 = FETCH (fetch request)
    - 0x08 = FETCH_RES (fetch response)
  - Reserved (1 byte)
  - RequestId (4 bytes)
  - PayloadLength (4 bytes)
  - Checksum (CRC32, 4 bytes)

- HandshakePayload — 136 bytes: 32 (identity key) + 32 (onion key) + 8 (timestamp) + 64 (ed25519 signature). Use `HandshakePayload.WriteSignableBytes` ao construir os bytes a serem assinados.

## Criptografia & formatos

- Assinaturas: ed25519 (NSec)
- Key agreement: x25519 (ECDH)
- KDF: HKDF-SHA256
- AEAD: ChaCha20-Poly1305 (layers de onion)

## Como executar (dev)

- Build (requer .NET 10 SDK):

```powershell
dotnet build FalconNode.sln
```

- Run (a aplicação inicializa os workers):

```powershell
dotnet run --project FalconNode.csproj
```

- Debug: abrir a solução no Visual Studio / VS Code. `Properties/launchSettings.json` contém configurações de depuração e perfis.

## Debug mode & Terminal UI (interactive)

- Program supports runtime flags: `--port <port>`, `--seed <seedPort>` and `--debug`.
- In debug mode the application registers a small interactive `TerminalUi` (see `src/UI/TerminalUI.cs`). The UI uses the same `NodeLogicWorker` singleton to send handshakes, publish files and craft ad-hoc packets for testing.
- Example (PowerShell):

```powershell
dotnet run -- --debug --port 5001 --seed 5000
```

- Interactive `TerminalUi` commands:
  - connect <port> — send a handshake to loopback:<port>
  - upload <text> — create a small file, ingest + upload via `FileIngestor`, returns manifest hash
  - fetch <manifestHash> — reassemble manifest via `FileRetriever` and display content
  - send-store <port> <text> — craft a STORE request (0x05) and send to a remote port (useful for manual protocol experiments)

## Observabilidade & padrões de performance

- Uso intensivo de `ArrayPool<byte>` para reduzir pressão do GC — ao modificar código, preserve o contrato de ownership e retorno dos buffers.
- Logging via `ILogger<T>`; NodeLogicWorker registra eventos-chave (handshake, DHT ops, onion processing).
- Canais são bounded (2000) e configurados com comportamento `Wait` no modo cheio.

## Storage

- `BlobStore` persiste blobs em `AppContext.BaseDirectory/data/blobs/` usando o hex SHA-256 como filename. Gravações usam arquivo temporário + rename para atomicidade.

API notes (current implementation):

- StoreAsync(ReadOnlyMemory<byte>): optimized to accept ReadOnlyMemory and avoid extra allocations (used widely by workers and FileIngestor).
- RetrieveBytesAsync(byte[] hash): returns a byte[] (preferred for small manifests / metadata).
- RetrieveToStreamAsync(byte[] hash, Stream target): stream blobs directly into a Stream for large payloads.
- RetrieveToBufferAsync(byte[] hash, Memory<byte> destination): read directly into an existing buffer.
- HasBlob / GetBlobSize for quick existence/size checks.

File ingestion and publishing

- `FileIngestor` implements chunking + manifest creation (src/Core/FS/FileIngestor.cs). `NodeLogicWorker` uses `FileIngestor.IngestAsync` for `PublishMessageAsync`, returning a manifest/message id that can be propagated in the DHT.

### Considerações sobre armazenamento de mídia (mensagens, imagens, vídeo)

- O projeto agora suporta ingestão por chunks via `FileIngestor` (src/Core/FS/FileIngestor.cs): arquivos são lidos em pedaços fixos (256 KiB), cada pedaço é armazenado como um blob independente e um manifesto JSON é escrito contendo a lista ordenada de hashes dos blocos.
- `FileRetriever` (src/Core/FS/FileRetriever.cs) reconstitui arquivos a partir do manifesto, usando `BlobStore.RetrieveToStreamAsync` para escrever cada chunk diretamente em um Stream (sem alocar buffers desnecessários).

Limites & comportamento atual:

- O `QuicListenerWorker` ainda tem um limite de payload por pacote (ver `QuicListenerWorker.cs`); para arquivos grandes o fluxo de FileIngestor + manifests/Fetch é a abordagem recomendada.
- O `BlobStore` não precisa mais receber arquivos inteiros quando trabalhar com arquivos grandes — o padrão atual é armazenar chunks + um pequeno manifesto. Isso reduz uso de memória e permite streaming de volta ao cliente.

Práticas recomendadas para produção / port para Rust (continua relevante):

1. Manter chunking + streaming para arquivos grandes e validar cross-language compatibilidade com manifests.
2. Implementar descoberta/recuperação de chunks ausentes via DHT/network (o reassembler atual lê apenas do armazenamento local).
3. Adicionar retenção, quotas, e políticas de replicação/peering para disponibilidade.

- Recomendações práticas para produção / port para Rust:
  1. Introduzir chunking transparente para blobs grandes: divida arquivos > N MB em blocos de tamanho fixo (ex. 1–4 MB) e nomeie cada bloco por SHA-256.
  2. Manifests: armazene um manifesto JSON/binary com a lista ordenada de blocos, tamanhos, e checksums para facilitar streaming e fetch parcial.
  3. Replicação/P2P: adicione sincronização entre peers (background replication) para alta disponibilidade e resilência.
  4. Quotas & GC: implemente políticas de retenção/limpeza e quotas por usuário/nó para evitar crescimento ilimitado.
  5. Range / streaming fetch: oferecer endpoints/streams que sirvam ranges (subconjuntos) do blob sem manter tudo na memória.
  6. Integração com CDN/Edge: para conteúdo muito quente, considerar caches de borda.

Essas melhorias são particularmente importantes porque o objetivo final é ter um nó que possa hospedar e disponibilizar mídia para uma rede social descentralizada sem depender de um servidor central.

## Migrando para Rust — plano prático (MVP → produção)

Mapeamentos recomendados:

- Async runtime: tokio
- QUIC: quinn (compatível com QUIC; usar exemplos do quinn para listeners/clients)
- Crypto: ed25519-dalek, x25519-dalek, chacha20poly1305, hkdf
- Channels: tokio::sync::mpsc::channel (bounded)
- Buffer reuse: bytes::BytesMut / pools

Incremental port checklist:

1. Reimplemente FixedHeader & HandshakePayload em Rust com testes binários de compatibilidade.
2. Implementar BlobStore no Rust e validar com blobs criados pelo C# (teste cross-language).
3. Portar crypto helpers e validar com vetores/assinaturas do C#.
4. Portar NodeLogic flow + canais e teste de integração com QUIC (quinn).

## Tests & recomendações

- Automated unit tests exist under `tests/FreedomNode.Tests` and cover core behaviors (FixedHeader, HandshakePayload, NodeLogicWorker flows, BlobStore helpers). Run them locally with:

```powershell
dotnet test tests\FreedomNode.Tests
```

- Recommended additional tests: onion-layer round-trip, large blob chunking, and integration tests that exercise QUIC paths when native runtimes are present.

---

Contribuições:

- Quer contribuir? Veja `CONTRIBUTING.md` (criado no repo) para orientações de PR, testes e checklist de revisão.

Referências úteis

- AI agent guidance: `.github/copilot-instructions.md` — curto e direto para contribuir com segurança e velocidade.

---
Se quiser, posso abrir um PR com um conjunto de templates (ISSUE_TEMPLATE / PULL_REQUEST_TEMPLATE) e adicionar instruções de CI para executar os testes em cada PR.

- Adicionar um novo worker: criar `BackgroundService` sob `src/Workers/` e registrá-lo em `Program.cs`.
- Nova mensagem: implementar payload em `src/Core/Messages`, adicionar parsing/handler no `NodeLogicWorker` e escrever testes.

---
