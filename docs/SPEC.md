> Spec original deste projeto (14/09/2026), guardada sem alterações para que o README e o HANDOFF possam citá-la.

# Spec: MCP Server para Debug Remoto do VS2005 (vs2005-debug-mcp)

## Objetivo

Implementar um MCP server standalone em C# que exponha ao Claude Code (rodando dentro do VSCodium) controle programático do debugger do Visual Studio 2005, incluindo attach remoto via `msvsmon.exe` a um processo .NET 2.0 (VB8) rodando em outra máquina.

## Contexto / decisão arquitetural

**Não implementar** reverse-engineering do protocolo SOAP do `msvsmon.exe` nem P/Invoke contra `vsdebugeng.dll`. Essas rotas foram avaliadas e descartadas: o protocolo é obfuscado, não documentado publicamente, e o ICorDebug é sempre local — não existe "API remota" nesse nível.

**Rota escolhida**: automação COM via `EnvDTE`/`EnvDTE80` (modelo `DTE`), que já fala com `msvsmon` internamente através do mecanismo padrão de "Attach to Process > Transport: Default". O servidor MCP automatiza o IDE do VS2005 (`devenv.exe`) via COM, e deixa o próprio VS lidar com a comunicação remota como ele já faz manualmente.

## Pré-requisitos a validar ANTES de codificar

- [ ] Abrir o Object Browser (ou a Macros IDE, se disponível) do VS2005 instalado na máquina alvo e confirmar a assinatura exata de `Debugger2.GetProcesses(Transport, TransportQualifier)` (ou `Debugger3`, dependendo da versão do `EnvDTE90a.dll` presente). A assinatura documentada publicamente é de versões posteriores do EnvDTE e pode divergir.
- [ ] Confirmar o ProgID exato disponível: `VisualStudio.DTE.8.0` é o esperado para VS2005.
- [ ] Confirmar que o `msvsmon.exe` já está rodando na máquina alvo com o transporte "Default" habilitado e a porta configurada (TCP:4020 para VS2005).
- [ ] Referenciar `EnvDTE.dll` e `EnvDTE80.dll` a partir da própria instalação do VS2005 (não via NuGet de versão mais nova) para evitar mismatch de interface COM.

## Arquitetura de arquivos

```
vs2005-debug-mcp/
├── vs2005-debug-mcp.csproj
├── Program.cs              # loop MCP (JSON-RPC 2.0 sobre stdio)
├── McpProtocol/
│   ├── JsonRpcServer.cs    # framing e dispatch de mensagens MCP
│   └── ToolSchema.cs       # definições de schema JSON dos tools
├── Dte/
│   ├── DteSession.cs       # attach/create do DTE, mantém referência viva, roda em thread STA
│   └── StaDispatcher.cs    # fila de execução single-thread para chamadas COM
└── Tools/
    ├── AttachTool.cs       # GetProcesses(transport, qualifier) → Attach()
    ├── BreakpointTool.cs   # Add/Remove breakpoints
    ├── StepTool.cs         # StepInto / StepOver / StepOut / Go / Stop
    ├── InspectTool.cs      # CurrentStackFrame, Locals, GetExpression(expr)
    └── OutputTool.cs       # leitura da Output Window (janela de debug)
```

## Requisito crítico: threading STA

O objeto `DTE` é apartment-threaded (STA). Todas as chamadas COM devem ser executadas na mesma thread STA que criou/anexou a sessão — nunca a partir do thread pool do servidor JSON-RPC.

Implementar `StaDispatcher`: uma thread dedicada com `ApartmentState.STA`, rodando uma fila (`BlockingCollection<Action>`), onde cada tool enfileira sua chamada COM e aguarda o resultado via `TaskCompletionSource`. Nenhuma chamada a `dte.*` deve acontecer fora dessa thread.

## Tools MCP a expor

Cada tool é uma função MCP com schema JSON de entrada/saída. Retorno como content block de texto (JSON serializado).

### `vs_attach`
Anexa ao processo remoto via transporte do msvsmon.
- Input: `{ "transport": string (default "Default"), "qualifier": string, "process_name": string }`
- Ação: `Debugger2.GetProcesses(transport, qualifier)` → localizar processo por nome → `.Attach()`
- Output: `{ "attached": bool, "process_id": int, "error": string? }`

### `vs_detach`
Desanexa do processo sem terminar a execução remota.
- Input: `{}`
- Ação: `Debugger.DetachAll()`

### `vs_set_breakpoint`
- Input: `{ "file": string, "line": int, "condition": string? }`
- Ação: `Debugger.Breakpoints.Add("", file, line, 1, "", condition ?? "")`
- Output: `{ "breakpoint_id": int }`

### `vs_remove_breakpoint`
- Input: `{ "breakpoint_id": int }`
- Ação: localizar em `Breakpoints` por índice/arquivo+linha e `.Delete()`

### `vs_list_breakpoints`
- Input: `{}`
- Output: lista de `{ file, line, enabled, condition }`

### `vs_step`
- Input: `{ "mode": "into" | "over" | "out", "wait_for_break": bool (default true) }`
- Ação: `StepInto` / `StepOver` / `StepOut` conforme `mode`

### `vs_go`
Continua execução até o próximo breakpoint ou fim.
- Input: `{ "wait_for_break": bool (default false) }`
- Ação: `Debugger.Go(waitForBreakOrEnd)`

### `vs_stop_debugging`
- Input: `{}`
- Ação: `Debugger.Stop(waitForDesignMode: true)`

### `vs_get_call_stack`
- Input: `{}`
- Ação: percorrer `Debugger.CurrentProgram.StackFrames` (ou `CurrentThread.StackFrames`)
- Output: lista de `{ function_name, file, line }`

### `vs_get_locals`
- Input: `{ "stack_frame_index": int (default 0, frame atual) }`
- Ação: `CurrentStackFrame.Locals` — iterar `EnvDTE.Expression`
- Output: lista de `{ name, type, value }`

### `vs_evaluate`
- Input: `{ "expression": string }`
- Ação: `Debugger.GetExpression(expression)`
- Output: `{ "value": string, "type": string, "is_valid": bool }`

### `vs_read_output_window`
- Input: `{ "pane": string (default "Debug") }`
- Ação: localizar `OutputWindowPane` pelo nome, ler `TextDocument`
- Output: `{ "text": string }`

### `vs_get_status`
- Input: `{}`
- Ação: `dte.Debugger.CurrentMode` (design/break/run)
- Output: `{ "mode": "design" | "break" | "run" }`

## Sessão DTE — lógica de conexão

Ordem de tentativa em `DteSession.Connect()`:

1. `Marshal.GetActiveObject("VisualStudio.DTE.8.0")` — tenta anexar a uma instância já aberta (via ROT).
2. Se falhar: log claro do motivo mais provável — **elevação assimétrica** (VS2005 e o processo do MCP server rodando em níveis de UAC diferentes não se enxergam no ROT). Não tentar `CreateObject` automaticamente nesse caso; retornar erro explícito pedindo para o usuário verificar se ambos os processos rodam no mesmo nível de elevação.
3. Só usar `CreateObject("VisualStudio.DTE.8.0")` (nova instância) se explicitamente solicitado via flag, já que abrir uma nova instância do VS2005 não serve para anexar a uma sessão de debug já em andamento.

## Registro no Claude Code

Adicionar ao `.mcp.json` do projeto:

```json
{
  "mcpServers": {
    "vs2005-debug": {
      "command": "path/para/vs2005-debug-mcp.exe",
      "args": []
    }
  }
}
```

Ou via CLI: `claude mcp add vs2005-debug -- path\para\vs2005-debug-mcp.exe`

A extensão Claude Code do VSCodium herda essa configuração por invocar o mesmo CLI internamente.

## Fases de implementação (MVP → completo)

**Fase 1 — conexão e leitura**
`DteSession` com STA dispatcher, `vs_get_status`, `vs_get_call_stack`, `vs_get_locals`, `vs_read_output_window`. Sem nenhuma ação que modifique estado — só validar que a automação COM funciona e que dá para ler o estado de uma sessão de debug já iniciada manualmente.

**Fase 2 — controle de execução**
`vs_step`, `vs_go`, `vs_stop_debugging`.

**Fase 3 — breakpoints e avaliação**
`vs_set_breakpoint`, `vs_remove_breakpoint`, `vs_list_breakpoints`, `vs_evaluate`.

**Fase 4 — attach remoto**
`vs_attach`, `vs_detach`. Deixado por último de propósito: depende do pré-requisito não validado (assinatura de `GetProcesses`) e é a parte com maior risco de retrabalho.

## Riscos conhecidos (não tratar como surpresa se aparecerem)

| Risco | Sintoma | Mitigação |
|---|---|---|
| Thread errada chamando COM | `RPC_E_WRONG_THREAD` ou travamento | Toda chamada `dte.*` passa pelo `StaDispatcher` |
| Elevação assimétrica | `GetActiveObject` lança `MK_E_UNAVAILABLE` mesmo com VS aberto | Rodar MCP server e VS2005 no mesmo nível de UAC |
| Assembly EnvDTE errado | `InvalidCastException` ao converter para `Debugger2`/`Debugger3` | Referenciar EnvDTE.dll da instalação local do VS2005 |
| Assinatura de `GetProcesses` divergente | Erro de compilação ou `MissingMethodException` em runtime | Validar contra Object Browser do VS2005 antes da Fase 4 |
| Debugger fora de modo break ao chamar Locals/Evaluate | Exceção COM ou coleção vazia | `vs_get_status` antes de qualquer chamada de inspeção; retornar erro claro se `mode != break` |

## Fora de escopo

- Qualquer automação de UI/tela (cliques, screenshots) — esta spec é 100% COM automation, não computer-use.
- Suporte a outras versões do Visual Studio além de 2005 (embora a mesma arquitetura deva portar com troca de ProgID).
- Debug de código VB6/COM legado — o alvo é .NET 2.0 (VB8/VB.NET), que usa o pipeline ICorDebug-backed do DTE normalmente.