# Contribuindo para o FreedomNode

Obrigado por contribuir! Este guia rápido descreve o fluxo de trabalho, requisitos mínimos e práticas específicas do projeto para que as PRs sejam rápidas de revisar e fáceis de integrar.

## Começando (ambiente de desenvolvimento)

- Requisitos: .NET 10 SDK instalado (localmente).

```powershell
# Build the solution
dotnet build FalconNode.sln

# Run tests
dotnet test tests\FreedomNode.Tests
```

- Debug: abrir a solução no Visual Studio (ou usar VS Code com a extensão C#). `Properties/launchSettings.json` possui perfis de execução.

## Branches e Pull Requests

- Branches: siga o padrão `feature/<resumo-curto>` ou `fix/<resumo-curto>`.
- Abra PRs contra `main` (ou a branch de integração se houver). Inclua descrição clara, motivação e quaisquer artefatos relevantes (capturas, logs, etc.).
- Mensagens de commit: mantenha concisas; considere usar Conventional Commits: `feat: ...`, `fix: ...`, `chore: ...`.

## Testes (obrigatório)

- Sempre adicione testes cobrindo o comportamento alterado/novo. Os testes estão em `tests/FreedomNode.Tests`.

Padrões de teste usados no repositório:

- Use `Channel.CreateBounded<T>` para simular canais de entrada/saída nos workers.
- Aloque buffers com `ArrayPool<byte>.Shared.Rent(...)` e certifique-se de devolver com `ArrayPool<byte>.Shared.Return(...)` no bloco finally.
- Observação: `BlobStore.StoreAsync` agora aceita `ReadOnlyMemory<byte>` — prefira passar `ReadOnlyMemory` quando possível para evitar cópias intermédias.
- Métodos de recuperação: `RetrieveBytesAsync` para blobs pequenos; `RetrieveToStreamAsync` ou `RetrieveToBufferAsync` para payloads maiores.
-- Testes de ingestão de arquivos: verifique que `FileIngestor.IngestAsync` produz um manifesto (hex) e que `FileRetriever.ReassembleFileAsync(manifest, stream)` reconstitui o conteúdo por streaming dos blocos. Use diretórios temporários e limpe arquivos no fim do teste.
- Para mensagens binárias, componha o buffer escrevendo `FixedHeader` seguido do payload; use helpers `ReadFromSpan`/`WriteToSpan` nos testes.

## Revisão de PR — checklist curto

- [ ] Novos comportamentos estão cobertos por testes automatizados (unit/integration).
- [ ] Não há fugas de buffer (ArrayPool alugado/devolvido corretamente).
- [ ] Mensagens binárias respeitam o `FixedHeader` (tamanho, tipos, ordem de bytes — endianness).
- [ ] Alterações de crypto/gestão de chaves foram revistas (padrões NSec: ed25519/x25519).
- [ ] Atualize `README.md` e `.github/copilot-instructions.md` se a mudança afetar arquitetura ou guidelines.

## Padrões técnicos importantes

### Channels + BackgroundService

- Os workers derivam de `BackgroundService`; registre-os em `Program.cs` com `builder.Services.AddHostedService<YourWorker>()`.
- A comunicação entre workers usa `Channel<NetworkPacket>` e `Channel<OutgoingMessage>`.

### Gerenciamento de memória

- Arrays são reutilizados via `ArrayPool<byte>`. Sempre devolva buffers; prefira try/finally para garantir devolução mesmo em caso de erro.

### Protocolo binário

- As mensagens usam `FixedHeader` (`FixedHeader.Size`) seguido de payloads tipados. Mantenha os helpers de escrita/leitura simétricos.
- Tipos de mensagem usados por `NodeLogicWorker`: 0x01=Handshake, 0x02=Onion, 0x03=FindNode(req), 0x04=FindNode(res), 0x05=STORE(req), 0x06=STORE_RES(res), 0x07=FETCH(req), 0x08=FETCH_RES(res).

### Ingestão e recuperação de arquivos (importante)

- `FileIngestor` divide arquivos em blocos de 256 KiB, armazena cada bloco via `BlobStore.StoreAsync(ReadOnlyMemory<byte>)` e grava um pequeno manifesto JSON (também no `BlobStore`) com a lista de hashes.
- `FileRetriever` reconstrói conteúdo recuperando o manifesto (`RetrieveBytesAsync`) e escrevendo cada bloco no stream alvo com `RetrieveToStreamAsync`.

### Crypto

- NSec: ed25519 para assinatura e X25519 para acordo de chaves. Mantenha chaves de identidade e chaves de onion separadas.

## Como adicionar um novo worker

1. Crie `src/Workers/YourWorker.cs` implementando `BackgroundService`.
2. Receba dependências via DI (canais, PeerTable, RoutingTable, BlobStore, etc.).
3. Registre o worker em `Program.cs` com `AddHostedService<YourWorker>()`.
4. Escreva testes usando canais limitados (bounded) e `NullLogger` para exercitar o loop.

> Observação: o construtor de `NodeLogicWorker` agora requer `BlobStore` e `FileIngestor` além dos canais e tabelas. Nos testes crie/injete `BlobStore` e `FileIngestor` (use variantes `NullLogger`) e construa `NodeSettings` com um parâmetro de porta quando relevante.

## Nota sobre UI / modo de depuração

-- `TerminalUi` é registrado quando a aplicação é executada com `--debug`. Depende de `NodeLogicWorker` como singleton e também requer `FileIngestor` e `FileRetriever`.
- Testes que exercem a lógica da UI podem instanciar estas dependências e invocar handlers diretamente (por exemplo `HandleUpload`, `HandleFetch`) ou executar a UI em background para simular fluxos interativos.

## Como adicionar um novo tipo de mensagem

1. Defina a estrutura/tamanho e os helpers `ReadFromSpan`/`WriteToSpan` em `src/Core/Messages`.
2. Atualize o switch/handler em `NodeLogicWorker` para o novo tipo de mensagem.
3. Adicione testes que compõem buffers [FixedHeader | payload] e verifiquem o comportamento.

Exemplo (Ping/Pong — apenas documentação):

```csharp
// src/Core/Messages/PingPayload.cs (exemplo)
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

// ... (restante do exemplo preservado)
```

## Problemas conhecidos / dicas práticas

- QUIC: o runtime nativo pode ser necessário para testes e integração com QUIC. Em dev, `ConnectionManagerWorker` aceita certificados sem validação (dev-only) via `RemoteCertificateValidationCallback`.
- Para blobs grandes: este MVP grava blobs inteiros; ao mudar para chunking atualize `BlobStore` e acrescente testes de compatibilidade.

## Contato e suporte

- Para dúvidas técnicas sobre arquitetura (DHT / onion / crypto) abra uma issue com contexto e os nós envolvidos.
- Para alterações grandes (formatos de mensagens, primitives crypto ou design de armazenamento), abra um RFC (issue) antes de implementar.

Agradecemos a sua contribuição — mantenha PRs pequenos e testáveis para acelerar a revisão.
