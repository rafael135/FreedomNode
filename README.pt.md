# FreedomNode

FreedomNode é um MVP (prova de conceito) em C#/.NET para redes descentralizadas. O objetivo primário é prototipar modelos de protocolo (DHT, onion routing, handshakes) e validar formatos binários e fluxos antes de implementar uma versão de produção em Rust.

### Objetivo do projeto

Este nó é pensado para ser a base de infraestrutura de uma rede social descentralizada: cada instância (nó) atua como host/armazenamento para conteúdos do tipo mensagens, imagens, vídeos e outros arquivos enviados pelos usuários. O MVP concentra-se em provar os blocos técnicos essenciais — identidade, handshakes autenticados, roteamento DHT e armazenamento content-addressed via SHA-256 — antes de migrar para uma implementação de alta performance em Rust.

## Visão Geral

- **Core:** Lógica principal do protocolo, incluindo primitivas criptográficas, DHT, mensagens, rede e gestão de estado.
- **Storage:** Persistência de dados e tratamento de mensagens de armazenamento.
- **Workers:** Serviços de background para tarefas de longa duração, como gestão de conexões, lógica do nó e escuta QUIC.
- **Program.cs:** Ponto de entrada da aplicação — registra serviços e workers e registra os canais de entrada/saída usados pelos workers.

## Arquitetura & fluxo de dados

1. Startup inicializa `NodeSettings` (NodeId aleatório + portas configuradas) e registra singletons: `PeerTable`, `RoutingTable`, `BlobStore`, `FileIngestor`.
2. Dois canais com limite (entrada e saída) atravessam os workers: `Channel<NetworkPacket>` (entrada) e `Channel<OutgoingMessage>` (saída).
3. `QuicListenerWorker` aceita conexões/fluxos QUIC, lê um `FixedHeader` + payload, e publica um `NetworkPacket` no canal de entrada.
4. `NodeLogicWorker` consome `NetworkPacket`s, aplica processamento (handshake, desembrulhamento de camadas onion, operações DHT, STORE/FETCH — armazenar/buscar) e escreve respostas/encaminhamentos no canal de saída.
5. `ConnectionManagerWorker` mantém conexões QUIC ativas e envia `OutgoingMessage`s para peers.

## Formato da mensagem (importante)

- FixedHeader — 16 bytes (big-endian onde aplicável):
  - Version (1 byte)
  - Flags (1 byte)
  - MessageType (1 byte) — exemplos usados por `NodeLogicWorker`:
    - 0x01 = Handshake
    - 0x02 = Onion
    - 0x03 = DHT FindNode (requisição)
    - 0x04 = DHT FindNode (resposta)
    - 0x05 = STORE (pedido de armazenamento)
    - 0x06 = STORE_RES (resposta ao STORE)
    - 0x07 = FETCH (pedido de fetch)
    - 0x08 = FETCH_RES (resposta ao FETCH)
  - Reserved (1 byte)
  - RequestId (4 bytes)
  - PayloadLength (4 bytes)
  - Checksum (CRC32, 4 bytes)

- HandshakePayload — 136 bytes: 32 (identity key) + 32 (onion key) + 8 (timestamp) + 64 (assinatura ed25519). Use `HandshakePayload.WriteSignableBytes` ao construir os bytes a serem assinados.

## Criptografia & formatos

- Assinaturas: ed25519 (NSec)
- Key agreement: X25519 (ECDH)
- KDF: HKDF-SHA256
- AEAD: ChaCha20-Poly1305 (camadas de onion)

## Requisitos no Linux (importante)

**Dependência de biblioteca nativa QUIC**: FreedomNode usa QUIC para transporte de rede, o que requer a biblioteca nativa `msquic`. **Esta biblioteca NÃO está incluída por padrão no .NET SDK no Linux** e deve ser instalada separadamente.

### Sintomas de msquic ausente

Se você tentar executar a aplicação sem o msquic instalado, verá um erro durante a inicialização do listener QUIC, tipicamente:

```
Error in QUIC Listener Worker.

System.PlatformNotSupportedException: System.Net.Quic is not supported on this platform: 
Unable to load MsQuic library version '2'. For more information see: 
https://learn.microsoft.com/dotnet/fundamentals/networking/quic/quic-overview#platform-dependencies

at System.Net.Quic.QuicListener.ListenAsync(QuicListenerOptions options, CancellationToken cancellationToken)
```

### Instalação por distribuição

**Arch Linux / Manjaro:**
```bash
yay -S msquic
# ou usando paru
paru -S msquic
```

**Ubuntu / Debian:**

A biblioteca msquic está disponível através do repositório de pacotes da Microsoft:

```bash
# Adicionar repositório de pacotes da Microsoft
wget https://packages.microsoft.com/config/ubuntu/$(lsb_release -rs)/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb
rm packages-microsoft-prod.deb

# Atualizar e instalar
sudo apt update
sudo apt install libmsquic
```

Para Debian, substitua `ubuntu/$(lsb_release -rs)` pela versão apropriada do Debian (ex: `debian/11` para Debian 11).

**Fedora / RHEL / CentOS:**
```bash
# Adicionar repositório da Microsoft
sudo rpm -Uvh https://packages.microsoft.com/config/fedora/$(rpm -E %fedora)/packages-microsoft-prod.rpm

# Instalar msquic
sudo dnf install libmsquic
```

**Compilando do código-fonte (qualquer distribuição):**

Se sua distribuição não possui msquic nos repositórios:

```bash
# Clonar o repositório
git clone --recursive https://github.com/microsoft/msquic.git
cd msquic

# Compilar (requer cmake, build-essential/base-devel)
mkdir build && cd build
cmake -G 'Unix Makefiles' ..
cmake --build .

# Instalar no sistema
sudo cmake --install .
```

### Verificando a instalação

Após instalar o msquic, você pode verificar a instalação checando se a biblioteca está disponível:

```bash
# Verificar se o arquivo da biblioteca existe (localização pode variar por distribuição)
ldconfig -p | grep msquic

# Deve mostrar algo como:
# libmsquic.so.2 (libc6,x86-64) => /usr/lib/x86_64-linux-gnu/libmsquic.so.2
```

**Importante**: FreedomNode requer a **biblioteca MsQuic versão 2**. Se você tiver uma versão mais antiga instalada, pode ser necessário atualizá-la.

**Nota para desenvolvedores**: No Windows e macOS, as bibliotecas nativas QUIC são incluídas com o .NET SDK e não requerem instalação separada.

## Como executar (dev)

- Compilar (requer .NET 10 SDK):

```bash
dotnet build FalconNode.sln
```

- Executar (a aplicação inicia os workers):

```bash
dotnet run --project FalconNode.csproj
```

- Debug: abrir a solução no Visual Studio / VS Code. `Properties/launchSettings.json` contém perfis de execução.

## Modo de depuração e Terminal UI (interativo)

- O programa aceita flags de execução: `--port <port>`, `--seed <seedPort>` e `--debug`.
-- Em modo debug a aplicação registra uma pequena interface interativa `TerminalUi` (veja `src/UI/TerminalUI.cs`). A interface usa o mesmo singleton `NodeLogicWorker` para enviar handshakes, publicar arquivos e criar pacotes ad-hoc para testes.

Exemplo:

```bash
dotnet run -- --debug --port 5001 --seed 5000
```

Comandos interativos do `TerminalUi`:
- `connect <port>` — enviar um handshake para loopback:<port>
- `upload <text>` — criar um pequeno arquivo, ingerir e enviar via `FileIngestor`; retorna o hash do manifesto
- `fetch <manifestHash>` — reconstituir o manifesto via `FileRetriever` e mostrar o conteúdo
- `send-store <port> <text>` — criar um pedido STORE (0x05) e enviar para uma porta remota (útil para testes manuais de protocolo)

## Observabilidade & padrões de performance

- Uso intensivo de `ArrayPool<byte>` para reduzir a pressão do GC — ao modificar código, preserve o contrato de propriedade e devolução dos buffers.
- Registro via `ILogger<T>`; `NodeLogicWorker` registra eventos-chave (handshake, operações DHT, processamento de camadas onion).
- Canais têm limite (2000) e usam comportamento `Wait` quando estão cheios.

## Storage

- `BlobStore` persiste blobs em `AppContext.BaseDirectory/data/blobs/` usando o hexadecimal SHA-256 como nome de arquivo. Todos os blobs são criptografados com ChaCha20-Poly1305 usando a chave de armazenamento do nó antes de serem gravados no disco. As gravações usam um arquivo temporário seguido de renomeação para garantir atomicidade.

Notas da API (implementação atual):

- `StoreAsync(ReadOnlyMemory<byte>)`: otimizado para aceitar `ReadOnlyMemory` e evitar alocações desnecessárias (usado por workers e FileIngestor).
- `RetrieveBytesAsync(byte[] hash)`: retorna um `byte[]` (preferível para manifests/metadata pequenos).
- `RetrieveToStreamAsync(byte[] hash, Stream target)`: escreve um blob diretamente num `Stream` para payloads grandes.
- `RetrieveToBufferAsync(byte[] hash, Memory<byte> destination)`: lê diretamente para um buffer existente.
- `HasBlob` / `GetBlobSize` para verificações rápidas de existência/tamanho.

Ingestão e publicação de arquivos

- `FileIngestor` implementa divisão em blocos (chunking) e criação de manifestos (src/Core/FS/FileIngestor.cs). `NodeLogicWorker` usa `FileIngestor.IngestAsync` para `PublishMessageAsync`, retornando um identificador (manifest) que pode ser propagado na DHT.

### Considerações sobre armazenamento de mídia (mensagens, imagens, vídeo)

- O projeto suporta ingestão por blocos via `FileIngestor` (src/Core/FS/FileIngestor.cs): arquivos são lidos em blocos fixos (256 KiB), cada bloco é armazenado como um blob independente e um manifesto JSON é criado contendo a lista ordenada de hashes dos blocos.
- `FileRetriever` (src/Core/FS/FileRetriever.cs) reconstitui arquivos a partir do manifesto, usando `BlobStore.RetrieveToStreamAsync` para escrever cada bloco diretamente num `Stream` (sem alocar buffers desnecessários).

Limites e comportamento atual:

- O `QuicListenerWorker` ainda tem um limite de payload por pacote (veja `QuicListenerWorker.cs`); para arquivos grandes use o fluxo de `FileIngestor` + manifest/`FETCH`.
- `BlobStore` já não precisa de receber arquivos inteiros para mídia grande — usar chunking + um pequeno manifesto reduz uso de memória e permite streaming eficiente para o cliente.

Práticas recomendadas para produção / port para Rust (continua relevante):

1. Manter chunking + streaming para arquivos grandes e validar compatibilidade cross-language dos manifestos.
2. Implementar descoberta/recuperação de blocos/chunks ausentes via DHT/rede (o reassembler atual apenas lê do armazenamento local).
3. Adicionar políticas de retenção, quotas e replicação/peering para disponibilidade.

- Recomendações práticas para port para Rust:
  1. Async runtime: tokio
  2. QUIC: quinn
  3. Crypto: ed25519-dalek, x25519-dalek, chacha20poly1305, hkdf
  4. Channels: tokio::sync::mpsc (bounded)
  5. Buffer reuse: bytes::BytesMut / pools

Incremental port checklist:

1. Implementar FixedHeader & HandshakePayload com testes que verifiquem compatibilidade binária.
2. Implementar BlobStore em Rust e validar com blobs criados pela implementação C#.
3. Portar helpers de crypto e validar com vetores do C#.
4. Portar NodeLogic flow + canais e adicionar testes de integração com QUIC.

## Testes & recomendações

- Testes unitários automatizados existem em `tests/FreedomNode.Tests` e cobrem comportamentos principais (FixedHeader, HandshakePayload, fluxos do NodeLogicWorker, helpers do BlobStore). Execute-os localmente com:

```bash
dotnet test tests\FreedomNode.Tests
```

- Testes recomendados adicionais: onion-layer round-trip, chunking de arquivos grandes, e testes de integração que exerçam caminhos QUIC quando runtimes nativos estão presentes.

## Padrões de documentação

- Todas as classes, métodos e propriedades públicas possuem comentários de documentação XML (`///`) para IntelliSense e tooltips hover.
- Summaries de classe são de linha única para compatibilidade; comportamento detalhado é documentado em seções `<remarks>`.
- Veja `CONTRIBUTING.pt.md` para padrões de documentação XML e exemplos.

---

Contribuições:

- Quer contribuir? Veja `CONTRIBUTING.md` para orientações de PR, testes e checklist de revisão.

Referências úteis:

- `.github/copilot-instructions.md` — orientações para contribuir rápido e em segurança.

---
