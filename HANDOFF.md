# Handoff para a VDI (onde o VS2005 realmente vive)

Este projeto foi escrito e testado até onde dá **sem** um VS2005 de
verdade por perto. O que segue é o roteiro para o Claude Code rodando
*na VDI* (Windows 10, mesma máquina que hospeda o VS2005 e o processo
alvo VB8/.NET 2.0) terminar a Fase 1 e validar tudo isso contra a IDE
real.

> **Em espera desde 05/10/2026.** O autor adiou a rodada na VDI para
> outro momento, sem data. Nada abaixo foi executado ainda.

## Contexto confirmado pelo autor (05/10/2026)

- VDI tem .NET 10 SDK e Claude Code.
- VS2005 em **en-US**: a pane de debug se chama "Debug" (default do tool).
- Cenário de debug **local** (F5 ou attach local), não remoto via
  `msvsmon.exe`. A Fase 4 (attach remoto) perde prioridade.
- **Caminho de instalação do VS2005: ainda não confirmado.** O default do
  `.csproj` é `C:\Program Files\Microsoft Visual Studio 8`. Numa VDI 64 bits
  o mais provável é `C:\Program Files (x86)\Microsoft Visual Studio 8`.
- Decisão: validar Fase 1 e Fase 2 **na mesma rodada**.

## Passo a passo (uma rodada só: build + Fase 1 + Fase 2)

1. **Achar a instalação do VS2005** e buildar apontando para ela:

   ```powershell
   Get-ChildItem 'C:\Program Files*\Microsoft Visual Studio 8\Common7\IDE\PublicAssemblies\EnvDTE*.dll'
   dotnet build -p:Vs2005InstallDir="C:\Program Files (x86)\Microsoft Visual Studio 8"
   ```

   Se der `CS0246`, o caminho está errado. Se der
   `InvalidCastException`/`MissingMethodException` em runtime, é mismatch
   de assinatura: não trocar para NuGet mais novo como atalho, conferir a
   assinatura real no Object Browser do próprio VS2005. Depois de
   confirmado, gravar o caminho como default no `.csproj`.

2. **Registrar no Claude Code da VDI:**

   ```powershell
   claude mcp add vs2005-debug -- "<caminho>\bin\Debug\net10.0-windows\Vs2005DebugMcp.exe"
   ```

   Antes, se quiser isolar problema de protocolo de problema de COM:
   `npx @modelcontextprotocol/inspector --cli <exe> --method tools/list`
   deve listar 8 tools.

3. **Abrir a solution no VS2005, pôr um breakpoint e dar F5**, deixando o
   debugger parado no breakpoint (modo break). VS2005 e Claude Code no
   mesmo nível de UAC (os dois sem "Executar como administrador", ou os
   dois com).

4. **Fase 1, leitura**, nesta ordem:
   - `vs_get_status` → deve dar `"break"`. Se der erro de ROT
     (`MK_E_UNAVAILABLE`), é UAC assimétrico; não adianta seguir.
   - `vs_get_call_stack` → `EnvDTE.StackFrame` base **não expõe
     arquivo/linha**, só `FunctionName`/`Module`/`Language`. Se linha-fonte
     fizer falta, anotar; investigar `StackFrame2` fica para depois.
   - `vs_get_locals` (e `stack_frame_index: 1`) → valores longos são
     truncados em 4000 chars. Anotar o que acontece num frame sem código
     gerenciado.
   - `vs_read_output_window` → pane "Debug".

5. **Fase 2, controle**, com o debugger ainda parado:
   - `vs_step` com `mode: "over"`, depois `"into"` numa linha com chamada
     de método, depois `"out"`. Conferir no próprio VS2005 que a seta
     amarela andou, e que `ModeAfter` volta `"break"`.
   - `vs_go` com `wait_for_break: false` → `ModeAfter` deve ser `"run"`.
     Disparar o breakpoint de novo na aplicação e conferir
     `vs_get_status` → `"break"`.
   - `vs_go` com `wait_for_break: true` → a resposta só chega quando o
     breakpoint for atingido. Nesse intervalo, outros tools ficam presos
     (limitação conhecida, a thread STA está ocupada); anotar se isso
     incomoda na prática.
   - `vs_stop_debugging` → `ModeAfter: "design"`.
   - Bônus: `vs_go` em modo design deve equivaler ao F5.

6. **Reconexão**: fechar e reabrir o VS2005 com o servidor MCP vivo e
   chamar `vs_get_status`. `Program.cs` tenta reconectar uma vez; não foi
   testado.

7. Registrar os resultados num commit (README "Estado atual" e este
   arquivo), e só então partir para a Fase 3 (breakpoints/evaluate).

## O que já está pronto e não deveria precisar de retrabalho

- `McpProtocol/JsonRpcServer.cs` — framing do transporte stdio do MCP
  (um JSON por linha), testado com o MCP Inspector em 05/10/2026. A versão
  anterior usava `Content-Length` (LSP) e não conversava com cliente MCP
  nenhum.
- `Dte/StaDispatcher.cs` — fila STA genérica, sem dependência de EnvDTE,
  reutilizável tal como está.
- `Program.cs` — dispatch de `tools/call` pros 5 tools de leitura e os 3 de controle (Fase 2), com
  uma tentativa de reconexão automática via ROT se uma chamada falhar
  com `DteConnectionException` depois de já ter conectado (cobre o caso
  de o VS2005 ser fechado/reaberto no meio de uma sessão de testes
  manual na VDI — ainda não testado contra isso de verdade).
- `Tools/InspectTool.cs::GetLocalsAsync` — já trunca `Expression.Value`
  em 4000 chars e usa `CurrentStackFrame` direto quando
  `stack_frame_index == 0` (evita enumerar `StackFrames` à toa); índice
  fora do alcance agora lança erro explícito em vez de cair
  silenciosamente no frame atual.
- `Tools/OutputTool.cs::ListPanesAsync` (`vs_list_output_panes`) —
  resolve o risco de nome de pane localizado (PT-BR "Depurar").

## O que é só esqueleto e precisa de julgamento na hora

- `Tools/InspectTool.cs` — `GetCallStackAsync`/`GetLocalsAsync` foram
  escritos a partir da forma da interface (`EnvDTE.Thread.StackFrames`,
  `StackFrame.Locals`), não testados contra um debugger real parado.
  Índice de stack frame, ordem, e comportamento quando `Locals` está
  vazio (thread sem frames gerenciados, ex. call nativa) não foram
  cobertos.
- `Dte/DteSession.cs` — `ConnectAsync` guarda a sessão numa única
  instância de processo; se o MCP server precisar sobreviver a um
  restart do VS2005 (fechar/abrir de novo), não há retry/reconexão
  automática implementada.
