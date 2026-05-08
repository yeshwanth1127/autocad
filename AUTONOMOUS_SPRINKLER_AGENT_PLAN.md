# Autonomous-First Sprinkler Agent (Definitive)

Autonomous-by-default in-process AutoCAD agent that reuses existing sprinkler engines, runs safely under strict document-lock and undo semantics, persists cross-session project memory, and orchestrates tools via OpenRouter chat-completions with deterministic safety limits.

## Status

| Phase | Description | Status |
|-------|-------------|--------|
| **1** | Runtime Safety Shell (`AcadLockGuard`, `AgentUndoScope`, palette run-state machine, `IAgentObserver`) | **Done** |
| **2** | Read Tools and Context (`ContextBuilder`, six read tools, token budget, read-only loop validation) | **Done** |
| **3** | Write Tools Incrementally with Guard Rails | Pending |
| **4** | OpenRouter + Autonomous Loop Controls | Pending |
| **5** | Cross-Session Memory + Prompt Conditioning | Pending |
| **6** | Verification Gates and Rollout | Pending |

---

## Steps

### Phase 1: Runtime Safety Shell

1. Implement `AcadLockGuard` used by every write tool: acquire `doc.LockDocument()`, open transaction, run preflight (`SprinklerXData.EnsureRegApp`, ensure sprinkler layers/required blocks), commit/abort deterministically.
2. Implement `AgentUndoScope` for run-level undo grouping so one autonomous run can be reverted by a single undo action.
3. Add palette run-state machine in `SprinklerPaletteControl` with 4 states (`Idle`, `Running`, `Stopping`, `AwaitingUndo`) controlling Start/Stop/Undo and input disablement.
4. Define `IAgentObserver` as the sole runtime-to-UI callback contract (`OnStep`, `OnToolCall`, `OnToolResult`, `OnComplete`, `OnStopped`, `OnError`).

### Phase 2: Read Tools and Context (no writes)

5. Implement `ContextBuilder` that summarizes drawing state (zone status, head count, gap count, nearest shaft, manual edit flag, NFPA profile, last decisions/corrections) and enforces a token budget target under ~1200 tokens.
6. Implement read tools returning compact JSON: `list_zones`, `get_zone_geometry` (summary form), `get_shaft_location`, `validate_coverage`, `get_xdata_tags`, `get_pipe_summary`.
7. Validate read-only agent loop end-to-end before any write tool is enabled.

### Phase 3: Write Tools Incrementally with Guard Rails

8. Add destructive guard policy per zone (`HasManualEdits`, `IsLocked`, `override_manual_edits`) and return blocked tool results with actionable reason text.
9. Implement write tools one-by-one in strict order, each via `AcadLockGuard` and existing internals: `place_sprinklers` → `route_main_pipe` → `attach_branches` → `cleanup_zone` → `add_end_cap` → `generate_area_report`.
10. For each successful write: record decision in project memory and save immediately.
11. Return structured tool results including `status`, primary counts, and `next_step` hint to guide model planning.

### Phase 4: OpenRouter + Autonomous Loop Controls

12. Implement `OpenRouterClient` against `/chat/completions` with model `anthropic/claude-sonnet-4-5`, `tool_choice=auto`, timeout budget, and retry/backoff for 429/5xx.
13. Implement `AgentLoop` with limits: `MaxSteps`, `MaxWrites`, per-step timeout, repeated-failure threshold, and no-progress detection after write attempts.
14. Enforce stop semantics through cancellation token; transition palette to `Stopping` and terminate cleanly without corrupting transaction state.
15. Split prompt architecture into static (cacheable policy/rules/sequence) and dynamic (context snapshot + corrections + recent decisions).

### Phase 5: Cross-Session Memory + Prompt Conditioning

16. Implement `ProjectMemory` file beside DWG (`.sprk-memory.json`) with drawing identity, NFPA profile, decisions, corrections, and zone state (`HasManualEdits`, `IsLocked`, notes, timestamps).
17. Load memory at run start, update `LastOpened`, and persist after each successful write tool and explicit engineer correction actions.
18. Inject active zone corrections and recent decisions into each dynamic prompt context.

### Phase 6: Verification Gates and Rollout

19. Gate promotion phase-by-phase: no move to write tools until read-only loop is proven on real drawings.
20. Validate redesign continuity via boundary-handle xdata linkage and cleanup correctness (no blind deletes).
21. Validate cancellation + undo behavior under mid-run interruptions and write bursts.
22. Validate token budget, cache behavior, and API reliability under throttling and transient failures.
23. Release behind autonomy mode config with autonomous default for internal use and assisted fallback mode during stabilization.

---

## Relevant files

- `autocad-final/UI/SprinklerPaletteControl.cs` — run state machine and observer integration.
- `autocad-final/Geometry/SprinklerXData.cs` — xdata contract and boundary-handle continuity.
- `autocad-final/AreaWorkflow/SprinklerLayers.cs` — layer contract and preflight dependency.
- `autocad-final/AreaWorkflow/SprinklerZoneAutomationCleanup.cs` — safe cleanup keyed by boundary handle.
- `autocad-final/AreaWorkflow/SprinklerGridPlacement2d.cs` — placement/coverage engine for read and write tools.
- `autocad-final/AreaWorkflow/MainPipeRouting2d.cs` — routing core for trunk/connector path decisions.
- `autocad-final/Commands/ApplySprinklersCommand.cs` — reusable placement path for `place_sprinklers`.
- `autocad-final/Commands/RouteMainPipeCommand.cs` — reusable route path for `route_main_pipe`.
- `autocad-final/Commands/AttachBranchesCommand.cs` — reusable branch attach path for `attach_branches`.
- `autocad-final/Commands/SprinklerDesignCommand.cs` — redesign orchestration reference.
- `autocad-final/Reporting/AreaTableService.cs` — report generation integration.
- `autocad-final/Properties.config` — runtime settings (model, limits, spacing defaults, mode).

---

## Verification

1. **Phase 1 gate:** validate lock guard from palette-triggered execution, verify no lock/contention errors, and verify one-run undo semantics with multiple writes.
2. **Phase 2 gate:** validate all 6 read tools on representative drawings and confirm context serialization remains under token budget.
3. **Phase 3 gate:** validate each write tool independently for correctness, memory logging, and undo reversibility before enabling next write tool.
4. **Phase 3 redesign gate:** validate cleanup uses boundary-handle/xdata continuity and preserves non-target manual content.
5. **Phase 4 gate:** validate OpenRouter retries/backoff under simulated 429 and confirm deterministic stop behavior.
6. **Phase 4 safety gate:** validate no-progress detector halts after repeated ineffective writes.
7. **Phase 5 gate:** validate `.sprk-memory.json` round-trip across drawing reopen and correction-driven behavior adjustments in subsequent runs.
8. **End-to-end gate:** run full autonomous single-zone then multi-zone scenarios without deadlocks, runaway writes, or UI desync.

---

## Decisions

- **Included:** autonomous-first (interrupt/undo safety model) and in-process orchestration in existing plugin.
- **Included:** OpenRouter integration using OpenAI-style tool-calls response handling.
- **Included:** cross-session memory file adjacent to DWG with correction injection.
- **Excluded in v1:** external HTTP orchestration service, RAG/vector DB, model fine-tuning, multi-document coordination.
- **Excluded in v1:** rewriting proven geometry/routing engines (`SprinklerGridPlacement2d`, `MainPipeRouting2d`).

---

## Further considerations

1. **Undo granularity:** keep full-run undo for trust/simplicity first; add optional per-zone undo mode later if needed.
2. **Memory growth control:** cap persisted decisions/corrections and keep only high-signal items to protect prompt/token budgets.
3. **Fallback model policy:** defer multi-model fallback until primary model behavior is stable and deterministic.
