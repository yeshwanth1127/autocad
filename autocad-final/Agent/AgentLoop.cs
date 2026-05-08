using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.ApplicationServices;
using autocad_final.AreaWorkflow;

namespace autocad_final.Agent
{
    public sealed class AgentLoop : IDisposable
    {
        private const string ZoningOnlyConstraintText =
            "INTENT CONSTRAINT:\n" +
            "- The user is asking for ZONING ONLY (create zones / zone boundary).\n" +
            "- You may ONLY call: create_sprinkler_zones (preferred) or create_zones_by_cuts (if you need clean straight partitions), plus read tools (list_zones, get_all_closed_polylines, get_drawing_census).\n" +
            "- Do NOT call: route_main_pipe, place_sprinklers, attach_branches, design_zone, cleanup_zone unless the user explicitly asks for piping/sprinklers/branches.\n" +
            "- After zoning succeeds, stop and wait for the user to request design steps.";

        private static bool IsZoningOnlyIntent(string userText)
        {
            if (string.IsNullOrWhiteSpace(userText)) return false;
            var s = userText.ToLowerInvariant();

            bool wantsZones =
                s.Contains("create zone") || s.Contains("create zones") ||
                s.Contains("zone boundary") || s.Contains("make zones") ||
                (s.Contains("zone") && s.Contains("create")) ||
                (s.Contains("zones") && (s.Contains("make") || s.Contains("create")));

            if (!wantsZones) return false;

            // If they mention downstream design steps, do not classify as zoning-only.
            bool mentionsDesign =
                s.Contains("main pipe") || s.Contains("trunk") || s.Contains("route") || s.Contains("routing") ||
                s.Contains("sprinkler") || s.Contains("sprinklers") ||
                s.Contains("branch") || s.Contains("branches") ||
                s.Contains("design_zone") || s.Contains("design zone") || s.Contains("attach");

            return !mentionsDesign;
        }

        private readonly OpenRouterClient _client;
        private readonly ToolDispatcher _dispatcher;
        private readonly int _maxSteps;
        private readonly string _systemPromptBase;
        private readonly string _model;
        private readonly bool _visionFeedback;
        private readonly Document _verificationDoc;
        private readonly ProjectMemory _verificationMemory;
        private bool _disposed;

        /// <param name="systemPromptBase">
        /// The instructions/persona block for the system message.
        /// Pass <c>null</c> to use the built-in default.
        /// The live drawing snapshot is always appended after this text at runtime.
        /// </param>
        /// <param name="visionFeedback">
        /// When true, a screenshot is captured and injected as a vision message after each
        /// successful write-tool call. Requires a vision-capable model. Default: true.
        /// </param>
        /// <param name="verificationDoc">When set with <paramref name="verificationMemory"/>, after a successful run with writes the loop performs one extra model turn (text + screenshot, no tools) so the model can verify results.</param>
        public AgentLoop(
            OpenRouterClient client,
            ToolDispatcher dispatcher,
            string systemPromptBase = null,
            int maxSteps = 12,
            string model = null,
            bool visionFeedback = true,
            Document verificationDoc = null,
            ProjectMemory verificationMemory = null)
        {
            _client           = client     ?? throw new ArgumentNullException(nameof(client));
            _dispatcher       = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _systemPromptBase = string.IsNullOrWhiteSpace(systemPromptBase)
                                    ? RuntimeSettings.DefaultSystemPrompt
                                    : systemPromptBase;
            _maxSteps         = maxSteps <= 0 ? 12 : maxSteps;
            _model            = string.IsNullOrWhiteSpace(model) ? "gpt-4o-mini" : model;
            _visionFeedback   = visionFeedback;
            _verificationDoc  = verificationDoc;
            _verificationMemory = verificationMemory;
        }

        /// <param name="conversationHistory">
        /// Persistent conversation list maintained by the caller.
        /// Pass the same list across multiple calls to enable multi-turn dialogue.
        /// The new user message and all AI / tool messages are appended to this list
        /// during the run, so the caller always holds the up-to-date history.
        /// Pass <c>null</c> to start a fresh single-turn conversation.
        /// </param>
        /// <param name="doc">When set, the drawing snapshot and system prompt are refreshed from the live document before each model step.</param>
        /// <summary>
        /// Overload for <see cref="PlanningLoop"/>: accepts a pre-built system prompt so the
        /// planning loop can inject a fresh per-iteration snapshot without rebuilding it here.
        /// </summary>
        /// <param name="screenshotBase64Png">When set, the new user turn is sent as vision (text + image). Requires a vision-capable model.</param>
        public Task RunAsync(
            string userMessage,
            string prebuiltSystemPrompt,
            IAgentObserver observer,
            CancellationToken cancellationToken,
            List<OpenRouterMessage> conversationHistory = null,
            string screenshotBase64Png = null)
            => RunCoreAsync(userMessage, prebuiltSystemPrompt, observer, cancellationToken, conversationHistory, screenshotBase64Png);

        public async Task RunAsync(
            string userMessage,
            DrawingSnapshot snapshot,
            ProjectMemory memory,
            IAgentObserver observer,
            CancellationToken cancellationToken,
            List<OpenRouterMessage> conversationHistory = null,
            Document doc = null,
            string screenshotBase64Png = null)
        {
            var systemPrompt = BuildSystemPrompt(snapshot ?? new DrawingSnapshot(), memory ?? new ProjectMemory());
            await RunCoreAsync(userMessage, systemPrompt, observer, cancellationToken, conversationHistory, screenshotBase64Png);
        }

        private async Task RunCoreAsync(
            string userMessage,
            string systemPrompt,
            IAgentObserver observer,
            CancellationToken cancellationToken,
            List<OpenRouterMessage> conversationHistory,
            string screenshotBase64Png = null)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(AgentLoop));

            if (observer == null)
                throw new ArgumentNullException(nameof(observer));

            AgentLog.Write("AgentLoop", "RunAsync entered — model=" + _model);

            _dispatcher.SetCancellationToken(cancellationToken);

            var messages = conversationHistory ?? new List<OpenRouterMessage>();
            if (!string.IsNullOrWhiteSpace(screenshotBase64Png))
            {
                var txt = string.IsNullOrWhiteSpace(userMessage)
                    ? "Analyze this screen capture in the context of my AutoCAD sprinkler design. The drawing or palette may be visible."
                    : userMessage;
                messages.Add(OpenRouterMessage.VisionUser(txt, screenshotBase64Png));

                // Intent guard: if the user only asked to create zones, do not drift into routing/sprinklers/branches.
                if (IsZoningOnlyIntent(txt))
                    messages.Add(new OpenRouterMessage { Role = "user", Content = ZoningOnlyConstraintText });
            }
            else
            {
                messages.Add(new OpenRouterMessage
                {
                    Role    = "user",
                    Content = string.IsNullOrWhiteSpace(userMessage) ? "Analyze the current drawing." : userMessage
                });

                // Intent guard: if the user only asked to create zones, do not drift into routing/sprinklers/branches.
                if (IsZoningOnlyIntent(userMessage))
                    messages.Add(new OpenRouterMessage { Role = "user", Content = ZoningOnlyConstraintText });
            }

            bool hadSuccessfulWrite = false;
            int zoningAutoRetryCount = 0;
            const int MaxZoningAutoRetries = 3;

            for (int step = 1; step <= _maxSteps; step++)
            {
                AgentLog.Write("AgentLoop", "step=" + step + " — checking cancellation");
                cancellationToken.ThrowIfCancellationRequested();

                AgentLog.Write("AgentLoop", "step=" + step + " — BuildSystemPrompt");
                // systemPrompt is already built by the caller (either BuildSystemPrompt or PlanningLoop).

                AgentLog.Write("AgentLoop", "step=" + step + " — OnStep (observer)");
                observer.OnStep(step, "Requesting model guidance.");

                var request = new OpenRouterRequest
                {
                    Model = _model,
                    Messages = PrependSystem(systemPrompt, messages),
                    Tools = _dispatcher.GetToolDefinitions().ToList(),
                    ToolChoice = "auto",
                    MaxTokens = 4096
                };

                AgentLog.Write("AgentLoop", "step=" + step + " — calling CompleteAsync (HTTP to OpenRouter)");
                // No ConfigureAwait(false) — continuation must return to the caller's
                // SynchronizationContext (UI thread) so tool dispatch runs on T001.
                var response = await _client.CompleteAsync(request, cancellationToken);
                AgentLog.Write("AgentLoop", "step=" + step + " — CompleteAsync returned");

                var choice = response.Choices != null && response.Choices.Count > 0 ? response.Choices[0] : null;
                if (choice == null || choice.Message == null)
                {
                    AgentLog.Write("AgentLoop", "step=" + step + " — empty response from OpenRouter");
                    observer.OnError("OpenRouter returned an empty response.");
                    return;
                }

                AgentLog.Write("AgentLoop", "step=" + step + " — response role=" +
                    (choice.Message.Role ?? "null") +
                    " toolCalls=" + (choice.Message.ToolCalls?.Count.ToString() ?? "null") +
                    " contentLen=" + (choice.Message.Content?.Length.ToString() ?? "null"));

                messages.Add(new OpenRouterMessage
                {
                    Role = choice.Message.Role ?? "assistant",
                    Content = choice.Message.Content,
                    ToolCalls = choice.Message.ToolCalls
                });

                if (choice.Message.ToolCalls != null && choice.Message.ToolCalls.Count > 0)
                {
                    bool anyWriteSuccess = false;

                    foreach (var toolCall in choice.Message.ToolCalls)
                    {
                        AgentLog.Write("AgentLoop", "step=" + step + " — tool call: " +
                            (toolCall.Function?.Name ?? "null") + " checking CT");
                        cancellationToken.ThrowIfCancellationRequested();

                        var toolName = toolCall.Function != null ? toolCall.Function.Name : null;
                        var toolArgs = toolCall.Function != null ? toolCall.Function.Arguments : null;
                        observer.OnToolCall(toolName ?? string.Empty, string.Empty, toolArgs ?? string.Empty);

                        AgentLog.Write("AgentLoop", "step=" + step + " — dispatching tool: " + (toolName ?? "null"));
                        var result = _dispatcher.Execute(toolName, toolArgs);
                        AgentLog.Write("AgentLoop", "step=" + step + " — tool returned: " + (toolName ?? "null") +
                            " resultLen=" + (result?.Length.ToString() ?? "null"));

                        observer.OnToolResult(toolName ?? string.Empty, !result.Contains("\"status\":\"error\""), result);

                        messages.Add(new OpenRouterMessage
                        {
                            Role = "tool",
                            ToolCallId = toolCall.Id,
                            Content = result
                        });

                        if (_visionFeedback && IsWriteTool(toolName) && result != null && result.Contains("\"status\":\"ok\""))
                            anyWriteSuccess = true;
                        if (IsWriteTool(toolName) && result != null && result.Contains("\"status\":\"ok\""))
                            hadSuccessfulWrite = true;
                    }

                    // After all tool results are committed, inject a screenshot so the model
                    // can visually verify the drawing and adapt parameters for the next step.
                    if (anyWriteSuccess)
                    {
                        var b64 = DrawingScreenCapture.CaptureBase64Png();
                        if (b64 != null)
                        {
                            messages.Add(OpenRouterMessage.VisionUser(
                                "Here is the current state of the AutoCAD drawing after the last operation.\n" +
                                "Review the screenshot carefully:\n" +
                                "• Identify any uncovered areas, gaps near walls or corners, or irregular head spacing.\n" +
                                "• Check whether the main pipe route looks reasonable for the zone shape.\n" +
                                "• If coverage looks incomplete, adjust spacing_m (reduce by 0.5) or " +
                                  "grid_anchor_offset_x/y_m (±0.5 m increments) for the next operation.\n" +
                                "• Use preview=true before committing if you plan significant parameter changes.\n" +
                                "Continue with the next required step.",
                                b64));
                            AgentLog.Write("AgentLoop", "step=" + step + " — vision message injected");
                        }
                    }

                    continue;
                }

                var finalText = choice.Message.Content;
                if (string.IsNullOrWhiteSpace(finalText))
                    finalText = "Agent finished without a textual summary.";

                bool autoRetryAfterVerification = false;
                string autoRetryUserText = null;
                string autoRetryScreenshot = null;

                if (_visionFeedback && hadSuccessfulWrite && _verificationDoc != null)
                {
                    try
                    {
                        observer.OnStep(step, "Final verification: refreshing drawing snapshot and screenshot.");
                        var fresh = AgentReadTools.BuildSnapshot(_verificationDoc, _verificationMemory ?? new ProjectMemory());
                        var zoneRows = fresh.Zones == null
                            ? new List<object>()
                            : fresh.Zones.Select(z => (object)new
                            {
                                boundary_handle = z.BoundaryHandle,
                                zone_id = z.Id,
                                status = z.Status,
                                area_m2 = z.AreaM2,
                                shaft_sites_inside = z.ShaftSitesInside,
                                has_shaft_inside = z.HasShaftInside
                            }).ToList();

                        int zoneBoundaries = fresh.Zones?.Count ?? 0;
                        int shaftSiteCount = FindShaftsInsideBoundary.CollectGlobalShaftSitePoints2d(_verificationDoc.Database).Count;
                        int parityDelta = zoneBoundaries - shaftSiteCount;
                        // Shaft-driven zoning: N shaft sites (blocks + session hints) ⇒ N tagged subzone outlines inside the floor.
                        bool zoningParityOk = shaftSiteCount < 2 || zoneBoundaries == shaftSiteCount;
                        string parityExplain;
                        if (shaftSiteCount < 2)
                            parityExplain = "Parity not enforced (need ≥2 shaft sites for multi-zone check).";
                        else if (zoneBoundaries == shaftSiteCount)
                            parityExplain = "Subzone outline count matches shaft site count (blocks + shaft hints).";
                        else
                            parityExplain = "MISMATCH: sprinkler subzone outlines (tagged splits inside the floor) must equal shaft_site_count. " +
                                "The outer floor polyline is not a subzone. Extra outlines usually mean duplicates from re-running create_sprinkler_zones without erasing old dashed zone lines/labels, " +
                                "or Voronoi fragments. This state is NOT valid for approval.";

                        int zonesWithNoShaftInside = fresh.Zones == null
                            ? 0
                            : fresh.Zones.Count(z => z != null && !z.HasShaftInside);
                        var boundaryHandlesMissingShaft = fresh.Zones == null
                            ? new List<string>()
                            : fresh.Zones.Where(z => z != null && !z.HasShaftInside).Select(z => z.BoundaryHandle).ToList();
                        bool everyZoneHasShaftOk = true;
                        string shaftContainExplain;
                        if (zoneBoundaries == 0 || shaftSiteCount == 0)
                        {
                            shaftContainExplain = "Subzone/shaft containment not enforced (no subzones or no shaft sites).";
                        }
                        else
                        {
                            everyZoneHasShaftOk = zonesWithNoShaftInside == 0;
                            shaftContainExplain = everyZoneHasShaftOk
                                ? "Every subzone polygon contains at least one shaft block or shaft hint site."
                                : zonesWithNoShaftInside + " subzone outline(s) have no shaft inside — invalid (orphan fragment, wrong split, or missing shaft/hint).";
                        }

                        bool zoningOk = zoningParityOk && everyZoneHasShaftOk;

                        object zoningRetryHints = BuildZoningRetryHints(
                            zoningOk,
                            zoningParityOk,
                            everyZoneHasShaftOk,
                            shaftSiteCount,
                            zoneBoundaries,
                            parityDelta,
                            boundaryHandlesMissingShaft);

                        var automatedChecks = new
                        {
                            shaft_site_count = shaftSiteCount,
                            zone_boundary_count = zoneBoundaries,
                            zone_count_semantics =
                                "zone_boundary_count = sprinkler subzone polylines (XData-tagged splits drawn inside the floor parcel). " +
                                "It does not include the outer floor/building outline.",
                            parity_delta_subzones_minus_shafts = parityDelta,
                            zoning_parity_ok = zoningParityOk,
                            parity_explanation = parityExplain,
                            zones_with_no_shaft_inside = zonesWithNoShaftInside,
                            boundary_handles_missing_shaft = boundaryHandlesMissingShaft,
                            every_zone_has_shaft_ok = everyZoneHasShaftOk,
                            shaft_containment_explanation = shaftContainExplain,
                            zoning_ok = zoningOk,
                            zoning_failure_breakdown = new
                            {
                                parity_mismatch = shaftSiteCount >= 2 && !zoningParityOk,
                                subzone_missing_shaft = zoneBoundaries > 0 && shaftSiteCount > 0 && !everyZoneHasShaftOk
                            },
                            zoning_retry_hints = zoningRetryHints
                        };

                        var verifyText =
                            "FINAL VERIFICATION (no tools).\n" +
                            "RULES — You must follow the deterministic JSON fields automated_checks.zoning_ok, zoning_parity_ok, every_zone_has_shaft_ok:\n" +
                            "• If zoning_ok is false, your answer MUST state verification FAILED. Read zoning_failure_breakdown and zoning_retry_hints: " +
                            "identify which part failed, then on the NEXT step emit a corrected tool call (e.g. create_sprinkler_zones with a new floor_boundary_handle, " +
                            "clear_shaft_hints + set_shaft_hint, or erase duplicate zone entities before re-zoning). Do not proceed with route_main_pipe / place_sprinklers until zoning_ok is true.\n" +
                            "• If zoning_ok is true, you may still fail the screenshot if coverage or piping is obviously wrong.\n" +
                            "• Do not invent zone or shaft counts — use only the numbers in automated_checks and the zone list.\n\n" +
                            JsonSupport.Serialize(new
                            {
                                automated_checks = automatedChecks,
                                shafts_in_snapshot = shaftSiteCount,
                                zones = zoneRows,
                                census = fresh.Census == null
                                    ? null
                                    : new
                                    {
                                        fresh.Census.TotalEntityCount,
                                        fresh.Census.ClosedPolylineCount,
                                        fresh.Census.Units
                                    }
                            });
                        messages.Add(new OpenRouterMessage { Role = "user", Content = verifyText });
                        var b64 = DrawingScreenCapture.CaptureBase64Png();
                        if (b64 != null)
                        {
                            messages.Add(OpenRouterMessage.VisionUser(
                                "Screenshot of the drawing after your last steps — use it together with the JSON above.",
                                b64));
                        }

                        var verifyReq = new OpenRouterRequest
                        {
                            Model = _model,
                            Messages = PrependSystem(systemPrompt, messages),
                            Tools = null,
                            ToolChoice = "none",
                            MaxTokens = 2048
                        };
                        AgentLog.Write("AgentLoop", "verification pass — CompleteAsync");
                        var verifyResp = await _client.CompleteAsync(verifyReq, cancellationToken).ConfigureAwait(true);
                        var vChoice = verifyResp.Choices != null && verifyResp.Choices.Count > 0 ? verifyResp.Choices[0] : null;
                        var vMsg = vChoice?.Message?.Content;
                        var autoVerdict = new StringBuilder();
                        autoVerdict.AppendLine("── Automated verification (code) ───────────────────────");
                        autoVerdict.Append("shaft_site_count=").Append(shaftSiteCount)
                            .Append(", zone_boundary_count=").Append(zoneBoundaries)
                            .Append(", zoning_parity_ok=").Append(zoningParityOk ? "true" : "false")
                            .Append(", every_zone_has_shaft_ok=").Append(everyZoneHasShaftOk ? "true" : "false")
                            .Append(", zoning_ok=").Append(zoningOk ? "true" : "false").AppendLine();
                        if (!zoningOk)
                            autoVerdict.AppendLine(
                                "RESULT: FAIL — zoning parity and/or per-zone shaft containment failed; do not proceed with sprinkler install until fixed. " +
                                "Use automated_checks.zoning_retry_hints for the next tool parameters.");
                        else
                            autoVerdict.AppendLine("RESULT: zoning OK — counts and per-zone shafts match (still review screenshot manually).");

                        if (!string.IsNullOrWhiteSpace(vMsg))
                            finalText = finalText.TrimEnd() + "\n\n" + autoVerdict + "\n── Model verification (may disagree; trust automated result above for counts) ───────────────────────\n" + vMsg.Trim();
                        else
                            finalText = finalText.TrimEnd() + "\n\n" + autoVerdict;

                        // Auto-iterate on verification failure: inject the verification JSON + hints back into the main tool loop
                        // so the model can call get_drawing_census / clear_shaft_hints / create_sprinkler_zones etc. to fix it.
                        if (!zoningOk && zoningAutoRetryCount < MaxZoningAutoRetries)
                        {
                            autoRetryAfterVerification = true;
                            zoningAutoRetryCount++;
                            AgentLog.Write("AgentLoop", "auto-retry #" + zoningAutoRetryCount + " — zoning_ok=false, re-entering tool loop");
                            observer.OnStep(step, "Verification FAILED — auto-retry " + zoningAutoRetryCount + "/" + MaxZoningAutoRetries + ": diagnosing and re-issuing corrective tool calls.");
                            autoRetryUserText =
                                "AUTOMATED VERIFICATION FAILED (retry " + zoningAutoRetryCount + "/" + MaxZoningAutoRetries + "). " +
                                "Do NOT produce a final summary. Instead:\n" +
                                "1. Start by calling get_drawing_census to confirm current layers, entity counts, and units.\n" +
                                "2. Read the JSON below (automated_checks) to identify which zones lack shafts (boundary_handles_missing_shaft) and why.\n" +
                                "3. Apply the ordered_remediation_steps in zoning_retry_hints in order. Typical sequence when a zone has no shaft inside: " +
                                "(a) erase orphan zone outlines on the sprinkler zone layer (use get_all_closed_polylines to find handles, then a cleanup path), " +
                                "(b) clear_shaft_hints, (c) set_shaft_hint for each real riser location read from the census/snapshot, " +
                                "(d) re-run create_sprinkler_zones with floor_boundary_handle=\"auto\" or the outer parcel handle.\n" +
                                "4. After the corrective tool calls, stop calling tools so verification can run again.\n\n" +
                                JsonSupport.Serialize(new
                                {
                                    automated_checks = automatedChecks,
                                    zones = zoneRows
                                });
                            autoRetryScreenshot = DrawingScreenCapture.CaptureBase64Png();
                        }
                    }
                    catch (Exception ex)
                    {
                        AgentLog.Write("AgentLoop", "verification pass failed: " + ex.Message);
                    }
                }

                if (autoRetryAfterVerification)
                {
                    if (!string.IsNullOrWhiteSpace(autoRetryScreenshot))
                        messages.Add(OpenRouterMessage.VisionUser(autoRetryUserText, autoRetryScreenshot));
                    else
                        messages.Add(new OpenRouterMessage { Role = "user", Content = autoRetryUserText });
                    continue;
                }

                AgentLog.Write("AgentLoop", "step=" + step + " — OnComplete");
                observer.OnComplete(finalText);
                return;
            }

            AgentLog.Write("AgentLoop", "hit maxSteps=" + _maxSteps);

            observer.OnError("Agent hit the maximum step count.");
        }

        private static bool IsWriteTool(string name)
        {
            switch (name?.ToLowerInvariant())
            {
                case "route_main_pipe":
                case "place_sprinklers":
                case "attach_branches":
                case "design_zone":
                case "create_sprinkler_zones":
                case "create_zones_by_cuts":
                case "cleanup_zone":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Structured hints so the model can map automated_checks failures to the next tool call and parameters.
        /// </summary>
        private static object BuildZoningRetryHints(
            bool zoningOk,
            bool zoningParityOk,
            bool everyZoneHasShaftOk,
            int shaftSiteCount,
            int zoneBoundaries,
            int parityDelta,
            List<string> boundaryHandlesMissingShaft)
        {
            if (zoningOk)
                return new { note = "Zoning checks passed — no mandatory redo." };

            var steps = new List<string>();
            if (shaftSiteCount >= 2 && !zoningParityOk)
            {
                if (parityDelta > 0)
                {
                    steps.Add(
                        "Parity: too many tagged subzone outlines vs shaft sites. Erase duplicate dashed zone polylines and zone labels from earlier runs (sprinkler zone / zone labels layers), then re-run zoning.");
                    steps.Add(
                        "Re-run create_sprinkler_zones with floor_boundary_handle \"auto\" after cleanup, or pass the hex handle of the outer floor parcel only (from get_all_closed_polylines — the closed loop around the building, not inner splits).");
                }
                else if (parityDelta < 0)
                {
                    steps.Add(
                        "Parity: too few subzone outlines vs shaft sites. Ensure each shaft block or hint lies inside the floor polyline, then re-run create_sprinkler_zones.");
                }
            }

            if (!everyZoneHasShaftOk && boundaryHandlesMissingShaft != null && boundaryHandlesMissingShaft.Count > 0)
            {
                steps.Add(
                    "Containment: subzone polygon(s) with no shaft — boundary_handle(s): " +
                    string.Join(", ", boundaryHandlesMissingShaft) +
                    ". Remove orphan fragments or re-zone so every split encloses a shaft site.");
                steps.Add(
                    "If block geometry is correct but counts are wrong: clear_shaft_hints, then set_shaft_hint at each riser, then create_sprinkler_zones.");
            }

            string primaryFailure;
            if (shaftSiteCount >= 2 && !zoningParityOk)
                primaryFailure = "parity_mismatch";
            else if (!everyZoneHasShaftOk && zoneBoundaries > 0 && shaftSiteCount > 0)
                primaryFailure = "subzone_missing_shaft";
            else
                primaryFailure = "zoning_incomplete";

            return new
            {
                primary_failure = primaryFailure,
                suggested_create_sprinkler_zones_parameters = new
                {
                    floor_boundary_handle =
                        "Use \"auto\" after erasing duplicate zone geometry, OR the hex handle of the outer closed floor/room polyline (the parcel), not a inner dashed subzone outline."
                },
                suggested_shaft_session_tools = new
                {
                    clear_and_rehint =
                        "clear_shaft_hints then set_shaft_hint (x,y) per riser, then create_sprinkler_zones."
                },
                ordered_remediation_steps = steps
            };
        }

        private static List<OpenRouterMessage> PrependSystem(string systemPrompt, List<OpenRouterMessage> messages)
        {
            var result = new List<OpenRouterMessage>
            {
                new OpenRouterMessage
                {
                    Role = "system",
                    Content = systemPrompt
                }
            };
            if (messages != null)
                result.AddRange(messages);
            return result;
        }

        private string BuildSystemPrompt(DrawingSnapshot snapshot, ProjectMemory memory)
        {
            var zones  = snapshot?.Zones         != null ? snapshot.Zones.Count         : 0;
            var shafts = snapshot?.Shafts        != null ? snapshot.Shafts.Count        : 0;
            var issues = snapshot?.PendingIssues != null ? snapshot.PendingIssues.Count : 0;

            var sb = new StringBuilder();
            sb.Append(_systemPromptBase);
            sb.Append("\n\nCURRENT SNAPSHOT:\n");
            sb.Append("zones=").Append(zones)
              .Append(", shafts=").Append(shafts)
              .Append(", pending_issues=").Append(issues).Append('\n');

            // Compact census: top layers by entity count so the model knows what is on
            // the drawing without needing to call get_drawing_census first.
            if (snapshot?.Census != null)
            {
                var census = snapshot.Census;
                sb.Append("drawing: total_entities=").Append(census.TotalEntityCount)
                  .Append(", sprinkler_scope_entities=").Append(census.SprinklerScopeEntityCount)
                  .Append(", closed_polylines=").Append(census.ClosedPolylineCount)
                  .Append(", text=").Append(census.TextCount)
                  .Append(", blocks=").Append(census.BlockTypes != null ? census.BlockTypes.Count : 0)
                  .Append('\n');
                sb.Append("Prioritize sprinkler automation layers (floor boundary, sprinkler - zone, zone labels, legacy pipe layers). ");

                if (census.LayersSprinklerScope != null && census.LayersSprinklerScope.Count > 0)
                {
                    sb.Append("layers (sprinkler scope, by count): ");
                    foreach (var l in census.LayersSprinklerScope.Take(12))
                        sb.Append(l.Name).Append('=').Append(l.EntityCount).Append(' ');
                    sb.Append('\n');
                }
                else if (census.Layers != null && census.Layers.Count > 0)
                {
                    sb.Append("layers (by count, full drawing): ");
                    var top = census.Layers.OrderByDescending(l => l.EntityCount).Take(8);
                    foreach (var l in top)
                        sb.Append(l.Name).Append('=').Append(l.EntityCount).Append(' ');
                    sb.Append('\n');
                }
            }

            sb.Append(JsonSupport.Serialize(new
            {
                nfpa             = snapshot?.Nfpa,
                recent_decisions = snapshot?.RecentDecisions,
                pending_issues   = snapshot?.PendingIssues,
                memory           = new
                {
                    hazard_class      = memory?.HazardClass          ?? "Light",
                    default_spacing_m = memory?.DefaultSpacingM      ?? RuntimeSettings.Load().SprinklerSpacingM,
                    max_coverage_m2   = memory?.MaxCoveragePerHeadM2 ?? 20.9
                }
            }));

            return sb.ToString();
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _client.Dispose();
        }
    }
}
