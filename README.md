# Vs2005DebugMcp

MCP server (C#, stdio, JSON-RPC 2.0) que expõe ao Claude Code controle
programático do debugger do Visual Studio 2005, via automação COM
(`EnvDTE`/`EnvDTE80`) — **não** via reverse-engineering do protocolo do
`msvsmon.exe`. Ver a spec original em [`docs/SPEC.md`](docs/SPEC.md) para o
raciocínio arquitetural (rota COM escolhida sobre SOAP/`vsdebugeng.dll`).

## Estado atual: Fases 1 e 2, não validadas contra VS2005 real

> **Em espera (desde 05/10/2026): validação na VDI adiada pelo autor**,
> sem data. O código das Fases 1 e 2 está pronto e enviado; o caminho de
> instalação do VS2005 na VDI segue sem confirmação. Ao retomar, seguir o
> roteiro do [`HANDOFF.md`](HANDOFF.md). Fases 3 e 4 não começam antes
> dessa validação.

Este projeto foi escrito num ambiente **sem VS2005 instalado** (dev
principal, não a VDI alvo). Tudo que não depende de `EnvDTE` foi
compilado e testado de ponta a ponta aqui:

- Framing JSON-RPC 2.0 sobre stdio no formato do transporte stdio do MCP
  (uma mensagem JSON por linha). Testado em 05/10/2026 com um cliente MCP
  real, o MCP Inspector em modo CLI (`npx @modelcontextprotocol/inspector
  --cli <exe> --method tools/list`): handshake, `tools/list` com os 8
  tools e `tools/call` devolvendo o erro de ROT esperado numa máquina sem
  VS2005. A versão anterior usava `Content-Length` (framing do LSP) e dava
  timeout nesse cliente: no Claude Code ela travaria já no `initialize`.
- `StaDispatcher` (fila single-thread STA) — compila e roda, mas sem
  chamada COM real para validar contra travamento/`RPC_E_WRONG_THREAD`.
- Assinaturas dos membros de `EnvDTE.StackFrame`, `EnvDTE.Expression`,
  `EnvDTE80.Debugger2`, `EnvDTE.OutputWindow`/`OutputWindowPane` foram
  conferidas por reflection contra o pacote NuGet `EnvDTE`/`EnvDTE80`
  17.14.40260 (VS2022) só para garantir que o C# compila contra a forma
  geral da API — **não** é garantia de que a VS2005 real tem exatamente
  essas assinaturas. Ver "Referências COM" abaixo.

**O que NÃO foi validado (só dá pra fazer na VDI com o VS2005 real):**
tudo que envolve `devenv.exe` de verdade — attach via ROT, ler
CurrentMode/CallStack/Locals de uma sessão de debug real, ler a Output
Window de verdade. Ver [`HANDOFF.md`](HANDOFF.md) para o roteiro exato
de finalização.

## Referências COM — por que o build falha aqui de propósito

O `.csproj` referencia `EnvDTE.dll`/`EnvDTE80.dll` por **caminho de
arquivo** (não NuGet), apontando por padrão para
`C:\Program Files\Microsoft Visual Studio 8\Common7\IDE\PublicAssemblies`.
Numa máquina sem VS2005 instalado nesse caminho, `dotnet build` falha
com `CS0246` nesses símbolos — **esperado**, não é bug.

Isso é deliberado: pacotes NuGet oficiais `EnvDTE`/`EnvDTE80` existem,
mas a versão mais recente (17.x, VS2022) referencia as interfaces
`Debugger2`/`Debugger3` como elas existem *hoje*, que podem ter
assinaturas diferentes das do VS2005 (2005 usava `EnvDTE80` recém-criado
para esse VS). Misturar a interop assembly errada corre risco de
`InvalidCastException` em runtime ao fazer o cast COM. Trocar por uma
versão nova "porque compila" mascara esse risco em vez de resolvê-lo.

Na VDI, aponte `Vs2005InstallDir` para a instalação real:

```powershell
dotnet build -p:Vs2005InstallDir="C:\Program Files\Microsoft Visual Studio 8"
```

(ou edite o valor default no `.csproj` depois de confirmar o caminho).

## Uso (depois de buildado na VDI)

```json
{
  "mcpServers": {
    "vs2005-debug": {
      "command": "caminho\\para\\Vs2005DebugMcp.exe",
      "args": []
    }
  }
}
```

`claude mcp add vs2005-debug -- caminho\para\Vs2005DebugMcp.exe`

## Tools expostos

Fase 1, somente leitura:

| Tool | O que faz |
|---|---|
| `vs_get_status` | Modo atual do debugger (design/break/run) |
| `vs_get_call_stack` | Stack frames da thread atual (requer modo break) |
| `vs_get_locals` | Locals de um stack frame (requer modo break; valores grandes sao truncados em 4000 chars) |
| `vs_read_output_window` | Texto de uma pane da Output Window |
| `vs_list_output_panes` | Lista os nomes das panes disponiveis (use antes de `vs_read_output_window` se "Debug" nao for encontrada — instalacao PT-BR usa "Depurar") |

Fase 2, controle de execução (antecipada para ser validada na mesma
rodada da VDI que a Fase 1):

| Tool | O que faz |
|---|---|
| `vs_step` | `mode`: `into`/`over`/`out`; exige modo break; `wait_for_break` default true |
| `vs_go` | Continua até o próximo break ou o fim; em modo design equivale ao F5; `wait_for_break` default false |
| `vs_stop_debugging` | Encerra a sessão de debug e espera o modo design |

Os três devolvem `{ Action, ModeBefore, ModeAfter }`. Com
`wait_for_break=true`, a chamada COM ocupa a thread STA até o debugger
parar, e nenhum outro tool responde nesse intervalo.

Os tools da Fase 1 não modificam o estado do debugger. Os da Fase 2
modificam. Fases 3 (breakpoints/evaluate) e 4 (attach remoto) ficam para
depois. A Fase 4 perdeu prioridade porque o cenário atual de uso é debug
local.

## Threading

`DTE` é apartment-threaded (STA). Toda chamada `dte.*` passa por
`Dte/StaDispatcher.cs` — uma thread dedicada `ApartmentState.STA`
consumindo uma fila. Nunca chame membros de `DTE2`/`Debugger`/etc fora
dela.

## Licença

[MIT](LICENSE).
