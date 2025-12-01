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

- O `BlobStore` atual é um armazenamento local por conteúdo (content-addressed) ideal para ficheiros de qualquer tipo, mas este MVP não inclui replicação, deduplicação em rede ou políticas de quota.
- Limites do MVP:
  - O listener simulado atualmente impõe um limite de payload de 5MB no `QuicListenerWorker` (veja `QuicListenerWorker.cs`). Para suportar vídeos e grandes imagens, será necessário um plano de chunking/streaming ou remoção desse limite.
  - O armazenamento atual grava blobs inteiros em disco; ficheiros muito grandes sugerem adotar chunking + manifestos (ex: dividir em blocos SHA-256 e armazenar um índice/manifest que referencia os blocos).

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
