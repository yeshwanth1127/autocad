using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using autocad_final.Agent.Planning;
using autocad_final.Licensing;

namespace autocad_final.Agent
{
    public sealed class ToolDispatcher
    {
        private DrawingSnapshot _snapshot;
        private readonly ProjectMemory _memory;
        private readonly Document _doc;
        private readonly System.Windows.Forms.Control _uiControl;
        private readonly Dictionary<string, Func<string, string>> _handlers;
        private readonly PlanningToolInterceptor _interceptor;
        private CancellationToken _cancellationToken = CancellationToken.None;

        /// <summary>
        /// Sets the cancellation token checked before each tool execution.
        /// Call this at the start of each agent run so that Stop unblocks any in-flight write tool.
        /// </summary>
        public void SetCancellationToken(CancellationToken ct) => _cancellationToken = ct;

        public ToolDispatcher(DrawingSnapshot snapshot, ProjectMemory memory, Document doc = null,
                              System.Windows.Forms.Control uiControl = null,
                              PlanningToolInterceptor interceptor = null)
        {
            _snapshot    = snapshot ?? new DrawingSnapshot { Zones = new List<ZoneSnapshot>(), Shafts = new List<ShaftSnapshot>(), RecentDecisions = new List<DecisionSnapshot>(), PendingIssues = new List<string>() };
            _memory      = memory   ?? new ProjectMemory();
            _doc         = doc;
            _uiControl   = uiControl;
            _interceptor = interceptor;
            _handlers = new Dictionary<string, Func<string, string>>(StringComparer.OrdinalIgnoreCase)
            {
                // ── Snapshot-based read tools ────────────────────────────────────
                ["list_zones"]          = _ => { RefreshSnapshotSafe(); return JsonSupport.Serialize(new { status = "ok", zones = _snapshot.Zones }); },
                ["get_zone_geometry"]   = args => { RefreshSnapshotSafe(); return JsonSupport.Serialize(SerializeZoneGeometry(GetBoundaryHandle(args))); },
                ["get_shaft_location"]  = _ => { RefreshSnapshotSafe(); return JsonSupport.Serialize(new { status = "ok", shafts = _snapshot.Shafts }); },
                ["validate_coverage"]   = args => RequireDoc(() => AgentReadTools.ValidateCoverage(_doc, GetBoundaryHandle(args))),
                ["get_xdata_tags"]      = args => { RefreshSnapshotSafe(); return JsonSupport.Serialize(SerializeTags(GetBoundaryHandle(args))); },
                ["get_pipe_summary"]    = args => { RefreshSnapshotSafe(); return JsonSupport.Serialize(SerializePipeSummary(GetBoundaryHandle(args))); },
                ["evaluate_zone"]       = args => RequireDoc(() => AgentReadTools.GetZoneScorecard(_doc, GetBoundaryHandle(args), AgentReadTools.BuildSnapshot(_doc, ProjectMemory.LoadFor(_doc)))),

                // ── Full-drawing read tools (live document) ──────────────────────
                ["get_drawing_census"]       = _ => RequireDoc(() => AgentReadTools.GetDrawingCensus(_doc)),
                ["get_all_closed_polylines"] = _ => RequireDoc(() => AgentReadTools.GetAllClosedPolylines(_doc)),
                ["get_text_content"]         = _ => RequireDoc(() => AgentReadTools.GetTextContent(_doc)),
                ["list_entities_on_layer"]   = args => RequireDoc(() => AgentReadTools.ListEntitiesOnLayer(_doc, GetStringArg(args, "layer_name"))),
                ["get_entity_details"]       = args => RequireDoc(() => AgentReadTools.GetEntityDetails(_doc, GetStringArg(args, "handle"))),

                // ── Write tools (modify drawing) ──────────────────────────────────
                ["cleanup_zone"]    = args => Intercept("cleanup_zone",    args, a => RequireDoc(() => AgentWriteTools.CleanupZone(_doc,    GetBoundaryHandle(a), GetBoolArg(a, "override_manual_edits"), _memory))),
                ["route_main_pipe"] = args => Intercept("route_main_pipe", args, a => RequireDoc(() => AgentWriteTools.RouteMainPipe(_doc,  GetBoundaryHandle(a), GetBoolArg(a, "override_manual_edits"), _memory, ParseRouteMainPipeOptions(a)))),
                ["place_sprinklers"]= args => Intercept("place_sprinklers",args, a => RequireDoc(() => AgentWriteTools.PlaceSprinklers(_doc,GetBoundaryHandle(a), GetBoolArg(a, "override_manual_edits"), _memory, ParsePlaceSprinklersOptions(a)))),
                ["attach_branches"] = args => Intercept("attach_branches", args, a => RequireDoc(() => AgentWriteTools.AttachBranches(_doc, GetBoundaryHandle(a), GetBoolArg(a, "override_manual_edits"), _memory))),
                ["design_zone"]     = args => Intercept("design_zone",     args, a => RequireDoc(() => AgentWriteTools.DesignZone(_doc,     GetBoundaryHandle(a), GetBoolArg(a, "override_manual_edits"), _memory, ParseDesignZoneOptions(a)))),
                ["create_sprinkler_zones"] = args => RequireDoc(() => AgentWriteTools.CreateSprinklerZones(_doc, GetFloorBoundaryHandle(args), _memory)),
                ["create_zones_by_cuts"] = args => RequireDoc(() => AgentWriteTools.CreateZonesByCuts(_doc, args, _memory)),

                // ── Shaft hint tools (session-level virtual shafts) ───────────────
                ["set_shaft_hint"] = args =>
                {
                    var vals = JsonSupport.DeserializeDictionary(args);
                    double x = 0, y = 0;
                    if (vals.TryGetValue("x", out var xv) && xv != null) { double.TryParse(xv.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out x); }
                    if (vals.TryGetValue("y", out var yv) && yv != null) { double.TryParse(yv.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out y); }
                    ShaftHintStore.AddHint(x, y);
                    return JsonSupport.Serialize(new { status = "ok", x, y, total_hints = ShaftHintStore.Count, message = "Shaft hint registered. It will be treated as a virtual shaft in all zone and routing tools for this session." });
                },
                ["clear_shaft_hints"] = _ =>
                {
                    int n = ShaftHintStore.Count;
                    ShaftHintStore.Clear();
                    return JsonSupport.Serialize(new { status = "ok", cleared = n });
                }
            };

            AgentWriteTools.AfterSuccessfulWrite += _ => RefreshSnapshotSafe();
        }

        public IReadOnlyList<OpenRouterToolDefinition> GetToolDefinitions()
        {
            return new List<OpenRouterToolDefinition>
            {
                // ── Snapshot-based ────────────────────────────────────────────────
                CreateTool("list_zones",          "List all zone summaries in the current drawing.", null),
                CreateTool("get_zone_geometry",   "Get a compact geometry summary for a zone.", CreateBoundaryHandleSchema()),
                CreateTool("get_shaft_location",  "List shaft positions and zone connections.", null),
                CreateTool("validate_coverage",   "Validate sprinkler coverage against expected placement.", CreateBoundaryHandleSchema()),
                CreateTool("get_xdata_tags",      "List xdata-tagged entities for a zone.", CreateBoundaryHandleSchema()),
                CreateTool("get_pipe_summary",    "Summarize main pipe and branch entities in a zone.", CreateBoundaryHandleSchema()),
                CreateTool("evaluate_zone",       "Return a compact zone scorecard (coverage + routing + geometry) for adaptive iteration after writes.", CreateBoundaryHandleSchema()),

                // ── Full-drawing (live document) ──────────────────────────────────
                CreateTool("get_drawing_census",
                    "Return the full drawing census: all layers with entity type breakdowns, block inventory, extents, and category counts. Use this to understand what exists anywhere in the drawing.",
                    null),

                CreateTool("get_all_closed_polylines",
                    "Return all closed polylines in the drawing (up to 300), sorted by area descending. Flags which ones are zone boundaries. Use to discover untagged rooms, boundaries, or floor areas.",
                    null),

                CreateTool("get_text_content",
                    "Return all text entities (DBText and MText) in the drawing (up to 500) with their content, position, and layer. Use to read room labels, annotations, dimensions text, or any drawing annotations.",
                    null),

                CreateTool("list_entities_on_layer",
                    "Return all entities on a specific layer (up to 200) with type, position, and a brief summary. Use to inspect the contents of any layer by name.",
                    CreateLayerNameSchema()),

                CreateTool("get_entity_details",
                    "Return comprehensive details for a single entity by its handle hex string. Returns full vertex list for polylines, attributes for blocks, text content, line endpoints, arc geometry, etc.",
                    CreateHandleSchema()),

                // ── Write tools ───────────────────────────────────────────────────
                CreateTool("cleanup_zone",
                    "Erase all automated sprinkler content (pipe, heads, branches) tagged to this zone. Use before redesigning a zone from scratch. Requires override_manual_edits=true when the zone has manual edits.",
                    CreateWriteZoneSchema()),

                CreateTool("route_main_pipe",
                    "Detect the nearest shaft and draw the tagged trunk + connector pipe for this zone. Run before place_sprinklers. When options.strategy is omitted, tries sprinkler_driven first (both orientations, minimises total branch length), then skeleton, grid_aligned, spine, shortest_path. Pass strategy explicitly to use only that method. Tune the length/drop balance via options.main_pipe_length_penalty.",
                    CreateWriteZoneSchema()),

                CreateTool("place_sprinklers",
                    "Compute a trunk-anchored sprinkler grid and insert pendent sprinkler heads for this zone. Requires main pipe already routed. Clears prior heads automatically.",
                    CreateWriteZoneSchema()),

                CreateTool("attach_branches",
                    "Draw branch pipe segments from each sprinkler head to the main trunk for this zone. Requires main pipe and sprinklers already placed.",
                    CreateWriteZoneSchema()),

                CreateTool("design_zone",
                    "Full pipeline for one zone: cleanup → route main pipe → place sprinklers → attach branches. Accepts options (spacing_m, coverage_radius_m, preferred_orientation, strategy, main_pipe_length_penalty, trunk_anchored, max_boundary_gap_m, grid_anchor_offset_x_m, grid_anchor_offset_y_m, skeleton_* parameters) for adaptive tuning. The strategy parameter controls main pipe derivation (sprinkler_driven / skeleton / grid_aligned / spine / shortest_path). Use override_manual_edits=true to proceed past manual-edit protection.",
                    CreateWriteZoneSchema()),

                CreateTool("create_sprinkler_zones",
                    "Pass the OUTER floor/room closed polyline handle (or \"auto\"). Tries equal-area recursive bisection first, then Voronoi/grid fallbacks. Draws dashed SUBZONE outlines inside that parcel (one per shaft). Requires at least two shaft blocks or set_shaft_hint points inside the floor. After success, call list_zones then design_zone per subzone boundary_handle.",
                    CreateFloorBoundarySchema()),

                CreateTool("create_zones_by_cuts",
                    "Manual straight-cut zoning: supply floor_boundary_handle (or \"auto\") and exactly N−1 cut segments {{x1,y1,x2,y2}} in WCS units for N shaft sites. Each cut must bisect one remaining polygon piece; after all cuts, each piece must contain exactly one shaft. Use when automatic create_sprinkler_zones produces jagged boundaries and you want clean partitions. Idempotent: erases prior zone outlines inside the floor first (same as create_sprinkler_zones).",
                    CreateZonesByCutsSchema()),

                // ── Shaft hint tools ──────────────────────────────────────────────
                CreateTool("set_shaft_hint",
                    "Register a WCS (X, Y) coordinate as a virtual shaft point for this session. Use when the drawing has no blocks named 'shaft' — for example when shafts are represented as 'RISER', 'LIFT', 'STAIRCORE', or similar. Hints are merged with real shaft blocks in all zone and routing tools. Multiple calls accumulate hints.",
                    CreateShaftHintSchema()),

                CreateTool("clear_shaft_hints",
                    "Remove all virtual shaft hints registered via set_shaft_hint for this session.",
                    null)
            };
        }

        public string Execute(string toolName, string argumentsJson)
        {
            AgentLog.Write("Dispatcher.Execute", "tool=" + (toolName ?? "null") +
                " tid=" + Thread.CurrentThread.ManagedThreadId);

            if (string.IsNullOrWhiteSpace(toolName))
                return JsonSupport.Serialize(new { status = "error", message = "Tool name is required." });

            if (TrialExpiry.IsExpired())
                return JsonSupport.Serialize(new { status = "error", message = TrialExpiry.ExpiredUserMessage });

            if (_handlers.TryGetValue(toolName, out var handler))
            {
                // All doc-touching tools must run on T001 (the Win32 message-pump thread).
                // Control.Invoke is the only reliable marshal — ExecuteInApplicationContext can
                // fire on a thread that triggers AutoCAD's Idle event and crashes WPF's
                // dispatcher check.
                if (_doc != null && IsDocTool(toolName))
                {
                    string docResult = null;
                    Exception docError  = null;

                    if (_uiControl != null && _uiControl.IsHandleCreated && !_uiControl.IsDisposed
                        && _uiControl.InvokeRequired)
                    {
                        // Called from a background thread — marshal to UI thread via Invoke.
                        AgentLog.Write("Dispatcher.Execute", "tool=" + toolName + " Invoke→T001 from tid=" +
                            Thread.CurrentThread.ManagedThreadId);
                        _uiControl.Invoke(new Action(() =>
                        {
                            AgentLog.Write("Dispatcher.Execute", "tool=" + toolName + " running on tid=" +
                                Thread.CurrentThread.ManagedThreadId);
                            try { docResult = handler(argumentsJson); }
                            catch (Exception ex) { docError = ex; }
                        }));
                    }
                    else
                    {
                        // Already on the UI thread — run directly (no Invoke needed).
                        AgentLog.Write("Dispatcher.Execute", "tool=" + toolName + " running inline on tid=" +
                            Thread.CurrentThread.ManagedThreadId);
                        try { docResult = handler(argumentsJson); }
                        catch (Exception ex) { docError = ex; }
                    }

                    if (docError != null)
                    {
                        AgentLog.Write("Dispatcher.Execute", "tool=" + toolName + " threw: " + docError.Message);
                        return JsonSupport.Serialize(new { status = "error", message = docError.Message });
                    }
                    AgentLog.Write("Dispatcher.Execute", "tool=" + toolName + " done");
                    return docResult ?? JsonSupport.Serialize(new { status = "error", message = "Tool returned no result." });
                }

                AgentLog.Write("Dispatcher.Execute", "tool=" + toolName + " running snapshot-based (no doc access)");
                return handler(argumentsJson);
            }

            AgentLog.Write("Dispatcher.Execute", "tool=" + (toolName ?? "null") + " UNKNOWN");
            return JsonSupport.Serialize(new { status = "error", message = "Unknown tool: " + toolName });
        }

        private static readonly HashSet<string> WriteTool = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "cleanup_zone", "route_main_pipe", "place_sprinklers", "attach_branches", "design_zone", "create_sprinkler_zones",
            "create_zones_by_cuts",
            "set_shaft_hint", "clear_shaft_hints"
        };

        // All tools that open transactions on doc.Database — both reads and writes.
        // These must run on the AutoCAD application context (never on a threadpool thread)
        // to avoid the cross-thread SendMessage deadlock that freezes the AutoCAD UI.
        private static readonly HashSet<string> DocTool = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Live-DB read tools
            "evaluate_zone",
            "get_drawing_census", "get_all_closed_polylines", "get_text_content",
            "list_entities_on_layer", "get_entity_details",
            // Snapshot-based reads that rebuild the snapshot on each call so write-tool
            // side-effects (new zones, erased entities) are visible immediately.
            "list_zones", "get_zone_geometry", "get_shaft_location",
            "get_xdata_tags", "get_pipe_summary",
            // Write tools
            "cleanup_zone", "route_main_pipe", "place_sprinklers", "attach_branches",
            "design_zone", "create_sprinkler_zones", "create_zones_by_cuts"
        };

        public bool IsWriteTool(string toolName)
            => !string.IsNullOrWhiteSpace(toolName) && WriteTool.Contains(toolName);

        public bool IsDocTool(string toolName)
            => !string.IsNullOrWhiteSpace(toolName) && DocTool.Contains(toolName);

        // ── Helpers ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Rebuilds the drawing snapshot against the live DB so read tools see the current
        /// zone topology — including zones created by create_sprinkler_zones or mutated by
        /// other write tools within the same agent run. Swallows errors so a refresh failure
        /// never breaks the read tool; the caller will just get the stale snapshot.
        /// </summary>
        private void RefreshSnapshotSafe()
        {
            if (_doc == null) return;
            try
            {
                var fresh = AgentReadTools.BuildSnapshot(_doc, _memory);
                if (fresh != null) _snapshot = fresh;
            }
            catch (Exception ex)
            {
                AgentLog.Write("Dispatcher.RefreshSnapshot", "failed: " + ex.Message);
            }
        }

        private string Intercept(string toolName, string args, Func<string, string> handler) =>
            _interceptor != null ? _interceptor.Intercept(toolName, args, handler) : handler(args);

        private string RequireDoc(Func<string> action)
        {
            if (_doc == null)
                return JsonSupport.Serialize(new { status = "error", message = "No active document available." });
            return action();
        }

        private ZoneSnapshot FindZone(string boundaryHandle)
        {
            if (string.IsNullOrWhiteSpace(boundaryHandle) || _snapshot.Zones == null)
                return null;

            return _snapshot.Zones.FirstOrDefault(z => string.Equals(z.BoundaryHandle, boundaryHandle, StringComparison.OrdinalIgnoreCase));
        }

        private object SerializeZoneGeometry(string boundaryHandle)
        {
            var zone = FindZone(boundaryHandle);
            if (zone == null)
                return new { status = "error", message = "Zone boundary not found." };

            return new
            {
                status = "ok",
                boundary_handle = zone.BoundaryHandle,
                layer = zone.Layer,
                area_du = zone.AreaDrawingUnits,
                area_m2 = zone.AreaM2,
                perimeter_du = zone.PerimeterDrawingUnits,
                vertex_count = zone.VertexCount,
                centroid_x = zone.CentroidX,
                centroid_y = zone.CentroidY,
                summary = zone.Summary
            };
        }

        private object SerializeCoverage(string boundaryHandle)
        {
            var zone = FindZone(boundaryHandle);
            if (zone == null)
                return new { status = "error", message = "Zone boundary not found." };

            return new
            {
                status = "ok",
                boundary_handle = zone.BoundaryHandle,
                coverage_ok = zone.CoverageGaps <= 0,
                expected_count = zone.ExpectedHeadCount,
                actual_count = zone.HeadCount,
                gap_count = zone.CoverageGaps,
                summary = zone.Summary
            };
        }

        private object SerializeTags(string boundaryHandle)
        {
            var zone = FindZone(boundaryHandle);
            if (zone == null)
                return new { status = "error", message = "Zone boundary not found." };

            return new
            {
                status = "ok",
                boundary_handle = zone.BoundaryHandle,
                tags = zone.Tags ?? new List<ZoneTagSnapshot>()
            };
        }

        private object SerializePipeSummary(string boundaryHandle)
        {
            var zone = FindZone(boundaryHandle);
            if (zone == null)
            {
                return new
                {
                    status = "ok",
                    boundary_handle = boundaryHandle,
                    main_pipe_count = _snapshot.Zones.Sum(z => z.MainPipeCount),
                    branch_count = _snapshot.Zones.Sum(z => z.BranchCount),
                    trunk_tagged_count = _snapshot.Zones.Sum(z => z.TrunkTaggedCount),
                    total_pipe_entities = _snapshot.Zones.Sum(z => z.TotalPipeEntities),
                    zones = _snapshot.Zones
                };
            }

            return new
            {
                status = "ok",
                boundary_handle = zone.BoundaryHandle,
                main_pipe_count = zone.MainPipeCount,
                branch_count = zone.BranchCount,
                trunk_tagged_count = zone.TrunkTaggedCount,
                total_pipe_entities = zone.TotalPipeEntities,
                zones = new[] { zone }
            };
        }

        // ── Schema / Tool factories ───────────────────────────────────────────────

        // Empty schema used for tools that take no parameters.
        // Sending null parameters causes DCJS to emit "parameters":null, which OpenRouter
        // converts to "input_schema":null and Anthropic rejects as "not a valid dictionary".
        private static readonly JsonSchemaObject EmptySchema = new JsonSchemaObject
        {
            Type = "object",
            Properties = new Dictionary<string, JsonSchemaObject>()
        };

        private static OpenRouterToolDefinition CreateTool(string name, string description, JsonSchemaObject schema)
        {
            return new OpenRouterToolDefinition
            {
                Function = new OpenRouterFunctionDefinition
                {
                    Name = name,
                    Description = description,
                    Parameters = schema ?? EmptySchema
                }
            };
        }

        private static JsonSchemaObject CreateBoundaryHandleSchema()
        {
            return new JsonSchemaObject
            {
                Type = "object",
                AdditionalProperties = false,
                Properties = new Dictionary<string, JsonSchemaObject>
                {
                    ["boundary_handle"] = new JsonSchemaObject
                    {
                        Type = "string",
                        Description = "Boundary polyline handle in hex string form."
                    }
                },
                Required = new List<string> { "boundary_handle" }
            };
        }

        private static JsonSchemaObject CreateLayerNameSchema()
        {
            return new JsonSchemaObject
            {
                Type = "object",
                AdditionalProperties = false,
                Properties = new Dictionary<string, JsonSchemaObject>
                {
                    ["layer_name"] = new JsonSchemaObject
                    {
                        Type = "string",
                        Description = "Exact layer name as it appears in the drawing."
                    }
                },
                Required = new List<string> { "layer_name" }
            };
        }

        private static JsonSchemaObject CreateHandleSchema()
        {
            return new JsonSchemaObject
            {
                Type = "object",
                AdditionalProperties = false,
                Properties = new Dictionary<string, JsonSchemaObject>
                {
                    ["handle"] = new JsonSchemaObject
                    {
                        Type = "string",
                        Description = "Entity handle in hex string form (e.g. \"2A3F\")."
                    }
                },
                Required = new List<string> { "handle" }
            };
        }

        private static string GetBoundaryHandle(string argumentsJson)
        {
            var values = JsonSupport.DeserializeDictionary(argumentsJson);
            if (values.TryGetValue("boundary_handle", out var boundaryHandle) && boundaryHandle != null)
                return boundaryHandle.ToString();
            if (values.TryGetValue("zone_id", out var zoneId) && zoneId != null)
                return zoneId.ToString();
            return string.Empty;
        }

        private static string GetFloorBoundaryHandle(string argumentsJson)
        {
            var values = JsonSupport.DeserializeDictionary(argumentsJson);
            if (values.TryGetValue("floor_boundary_handle", out var h) && h != null)
                return h.ToString();
            return GetBoundaryHandle(argumentsJson);
        }

        private static string GetStringArg(string argumentsJson, string key)
        {
            var values = JsonSupport.DeserializeDictionary(argumentsJson);
            if (values.TryGetValue(key, out var val) && val != null)
                return val.ToString();
            return string.Empty;
        }

        private static bool GetBoolArg(string argumentsJson, string key)
        {
            var values = JsonSupport.DeserializeDictionary(argumentsJson);
            if (values.TryGetValue(key, out var val) && val != null)
            {
                if (val is bool b) return b;
                bool parsed;
                if (bool.TryParse(val.ToString(), out parsed)) return parsed;
            }
            return false;
        }

        private static JsonSchemaObject CreateWriteZoneSchema()
        {
            return new JsonSchemaObject
            {
                Type = "object",
                AdditionalProperties = false,
                Properties = new Dictionary<string, JsonSchemaObject>
                {
                    ["boundary_handle"] = new JsonSchemaObject
                    {
                        Type = "string",
                        Description = "Boundary polyline handle in hex string form."
                    },
                    ["override_manual_edits"] = new JsonSchemaObject
                    {
                        Type = "boolean",
                        Description = "Set true to proceed even when the zone has manual engineer edits. Default false."
                    },
                    ["preview"] = new JsonSchemaObject
                    {
                        Type = "boolean",
                        Description = "When true, simulate the operation and return projected statistics without making any changes to the drawing. Use before committing to verify the plan produces good coverage."
                    },
                    ["strategy"] = new JsonSchemaObject
                    {
                        Type = "string",
                        Enum = new List<string> { "sprinkler_driven", "skeleton", "grid_aligned", "spine", "shortest_path" },
                        Description = "Main pipe routing strategy. sprinkler_driven: places sprinklers first, evaluates BOTH horizontal and vertical candidate trunks along sprinkler rows/columns, picks the trunk that minimises Σ(sprinkler→trunk manhattan) + λ·trunk_length — recommended default for clean grids (tune λ with main_pipe_length_penalty). skeleton: medial-axis skeleton + longest spine path — best for irregular / concave zones. grid_aligned: single-orientation scanline along the centre of sprinkler rows/columns. spine: longest inside segment along the zone median axis. shortest_path: single-orientation min-cost scanline (legacy)."
                    },
                    ["orientation"] = new JsonSchemaObject
                    {
                        Type = "string",
                        Enum = new List<string> { "auto", "horizontal", "vertical" },
                        Description = "Trunk orientation override. auto lets the engine choose based on zone shape."
                    },
                    ["options"] = new JsonSchemaObject
                    {
                        Type = "object",
                        AdditionalProperties = false,
                        Description = "Optional adaptive parameters for this tool.",
                        Properties = new Dictionary<string, JsonSchemaObject>
                        {
                            ["spacing_m"] = new JsonSchemaObject { Type = "number", Description = "Sprinkler spacing in meters (default 3.0). Reduce to 2.5 or 2.0 to fill gaps in concave/narrow zones." },
                            ["coverage_radius_m"] = new JsonSchemaObject { Type = "number", Description = "Coverage radius in meters (default 1.5)." },
                            ["max_boundary_gap_m"] = new JsonSchemaObject { Type = "number", Description = "Max allowed gap from boundary before adding heads (meters). place_sprinklers only." },
                            ["trunk_anchored"] = new JsonSchemaObject { Type = "boolean", Description = "Use trunk-anchored grid placement. place_sprinklers / design_zone only. Try false if trunk-anchored leaves gaps." },
                            ["preferred_orientation"] = new JsonSchemaObject { Type = "string", Description = "Trunk orientation: \"auto\" (default), \"horizontal\", or \"vertical\". route_main_pipe / design_zone. Use when bbox_aspect_ratio or concavity_proxy suggests the auto choice is wrong." },
                            ["grid_anchor_offset_x_m"] = new JsonSchemaObject { Type = "number", Description = "Shift the sprinkler grid origin by this many meters along X. place_sprinklers / design_zone. Use in ±0.5 m increments to slide the grid and cover missed corners or alcoves." },
                            ["grid_anchor_offset_y_m"] = new JsonSchemaObject { Type = "number", Description = "Shift the sprinkler grid origin by this many meters along Y. place_sprinklers / design_zone. Use in ±0.5 m increments to slide the grid and cover missed corners or alcoves." },
                            ["skeleton_cell_size_m"] = new JsonSchemaObject { Type = "number", Description = "Skeleton strategy only: distance-transform cell size (m). Default ≈ spacing/4. Decrease (0.15–0.3) for fine-grained skeletons in narrow zones; increase (0.5–1.0) for large zones to stay within the cell cap." },
                            ["skeleton_min_clearance_m"] = new JsonSchemaObject { Type = "number", Description = "Skeleton strategy only: minimum clearance from the boundary (m) for skeleton cells. Default ≈ coverage_radius/2. Raise to push the trunk toward the zone centre; lower to let it hug concave bays." },
                            ["skeleton_prune_branch_length_m"] = new JsonSchemaObject { Type = "number", Description = "Skeleton strategy only: prune skeleton branches shorter than this (m). Default ≈ one spacing. Raise to get a cleaner main spine; lower/0 to keep more branch detail." },
                            ["main_pipe_length_penalty"] = new JsonSchemaObject { Type = "number", Description = "sprinkler_driven / shortest_path only: λ in cost = Σ(sprinkler→trunk) + λ·trunk_length. Default 0.1. Raise (0.3–1.0) to prefer shorter trunks; lower (→0) to let the trunk stretch until every sprinkler has the shortest possible drop." }
                        }
                    }
                },
                Required = new List<string> { "boundary_handle" }
            };
        }

        private static AgentWriteTools.RouteMainPipeOptions ParseRouteMainPipeOptions(string argumentsJson)
        {
            var vals = JsonSupport.DeserializeDictionary(argumentsJson);
            var opt  = GetOptions(argumentsJson); // may be null — fall back to flat vals
            return new AgentWriteTools.RouteMainPipeOptions
            {
                SpacingM                     = TryGetNumber(opt, "spacing_m")                    ?? TryGetNumber(vals, "spacing_m"),
                CoverageRadiusM              = TryGetNumber(opt, "coverage_radius_m")            ?? TryGetNumber(vals, "coverage_radius_m"),
                PreferredOrientation         = TryGetString(opt, "preferred_orientation")        ?? TryGetString(vals, "preferred_orientation")
                                               ?? TryGetString(vals, "orientation"),
                Strategy                     = TryGetString(opt, "strategy")                     ?? TryGetString(vals, "strategy"),
                SkeletonCellSizeM            = TryGetNumber(opt, "skeleton_cell_size_m")         ?? TryGetNumber(vals, "skeleton_cell_size_m"),
                SkeletonMinClearanceM        = TryGetNumber(opt, "skeleton_min_clearance_m")     ?? TryGetNumber(vals, "skeleton_min_clearance_m"),
                SkeletonPruneBranchLengthM   = TryGetNumber(opt, "skeleton_prune_branch_length_m") ?? TryGetNumber(vals, "skeleton_prune_branch_length_m"),
                MainPipeLengthPenalty        = TryGetNumber(opt, "main_pipe_length_penalty")     ?? TryGetNumber(vals, "main_pipe_length_penalty")
            };
        }

        private static AgentWriteTools.PlaceSprinklersOptions ParsePlaceSprinklersOptions(string argumentsJson)
        {
            var vals = JsonSupport.DeserializeDictionary(argumentsJson);
            var opt  = GetOptions(argumentsJson); // may be null — fall back to flat vals
            return new AgentWriteTools.PlaceSprinklersOptions
            {
                SpacingM           = TryGetNumber(opt, "spacing_m")              ?? TryGetNumber(vals, "spacing_m"),
                CoverageRadiusM    = TryGetNumber(opt, "coverage_radius_m")      ?? TryGetNumber(vals, "coverage_radius_m"),
                MaxBoundaryGapM    = TryGetNumber(opt, "max_boundary_gap_m")     ?? TryGetNumber(vals, "max_boundary_gap_m"),
                TrunkAnchored      = TryGetBool(opt, "trunk_anchored")           ?? TryGetBool(vals, "trunk_anchored"),
                GridAnchorOffsetXM = TryGetNumber(opt, "grid_anchor_offset_x_m") ?? TryGetNumber(vals, "grid_anchor_offset_x_m"),
                GridAnchorOffsetYM = TryGetNumber(opt, "grid_anchor_offset_y_m") ?? TryGetNumber(vals, "grid_anchor_offset_y_m")
            };
        }

        private static Dictionary<string, object> GetOptions(string argumentsJson)
        {
            var values = JsonSupport.DeserializeDictionary(argumentsJson);
            if (values.TryGetValue("options", out var o) && o is Dictionary<string, object> d)
                return d;
            return null;
        }

        private static double? TryGetNumber(Dictionary<string, object> dict, string key)
        {
            if (dict == null || !dict.TryGetValue(key, out var v) || v == null) return null;
            if (v is double dd) return dd;
            if (v is float ff) return ff;
            if (v is int ii) return ii;
            if (v is long ll) return ll;
            double parsed;
            return double.TryParse(v.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out parsed) ? parsed : (double?)null;
        }

        private static bool? TryGetBool(Dictionary<string, object> dict, string key)
        {
            if (dict == null || !dict.TryGetValue(key, out var v) || v == null) return null;
            if (v is bool b) return b;
            bool parsed;
            return bool.TryParse(v.ToString(), out parsed) ? parsed : (bool?)null;
        }

        private static string TryGetString(Dictionary<string, object> dict, string key)
        {
            if (dict == null || !dict.TryGetValue(key, out var v) || v == null) return null;
            var s = v.ToString();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }

        private static AgentWriteTools.DesignZoneOptions ParseDesignZoneOptions(string argumentsJson)
        {
            var vals = JsonSupport.DeserializeDictionary(argumentsJson);
            var opt  = GetOptions(argumentsJson); // may be null — fall back to flat vals
            return new AgentWriteTools.DesignZoneOptions
            {
                SpacingM                     = TryGetNumber(opt, "spacing_m")                      ?? TryGetNumber(vals, "spacing_m"),
                CoverageRadiusM              = TryGetNumber(opt, "coverage_radius_m")              ?? TryGetNumber(vals, "coverage_radius_m"),
                PreferredOrientation         = TryGetString(opt, "preferred_orientation")          ?? TryGetString(vals, "preferred_orientation")
                                               ?? TryGetString(vals, "orientation"),
                MaxBoundaryGapM              = TryGetNumber(opt, "max_boundary_gap_m")             ?? TryGetNumber(vals, "max_boundary_gap_m"),
                TrunkAnchored                = TryGetBool(opt, "trunk_anchored")                   ?? TryGetBool(vals, "trunk_anchored"),
                GridAnchorOffsetXM           = TryGetNumber(opt, "grid_anchor_offset_x_m")         ?? TryGetNumber(vals, "grid_anchor_offset_x_m"),
                GridAnchorOffsetYM           = TryGetNumber(opt, "grid_anchor_offset_y_m")         ?? TryGetNumber(vals, "grid_anchor_offset_y_m"),
                Strategy                     = TryGetString(opt, "strategy")                       ?? TryGetString(vals, "strategy"),
                SkeletonCellSizeM            = TryGetNumber(opt, "skeleton_cell_size_m")           ?? TryGetNumber(vals, "skeleton_cell_size_m"),
                SkeletonMinClearanceM        = TryGetNumber(opt, "skeleton_min_clearance_m")       ?? TryGetNumber(vals, "skeleton_min_clearance_m"),
                SkeletonPruneBranchLengthM   = TryGetNumber(opt, "skeleton_prune_branch_length_m") ?? TryGetNumber(vals, "skeleton_prune_branch_length_m"),
                MainPipeLengthPenalty        = TryGetNumber(opt, "main_pipe_length_penalty")       ?? TryGetNumber(vals, "main_pipe_length_penalty"),
                Preview                      = TryGetBool(opt, "preview")                          ?? TryGetBool(vals, "preview")
            };
        }

        private static JsonSchemaObject CreateShaftHintSchema()
        {
            return new JsonSchemaObject
            {
                Type = "object",
                AdditionalProperties = false,
                Properties = new Dictionary<string, JsonSchemaObject>
                {
                    ["x"] = new JsonSchemaObject { Type = "number", Description = "WCS X coordinate of the shaft/riser location (drawing units, same coordinate system as polyline vertices)." },
                    ["y"] = new JsonSchemaObject { Type = "number", Description = "WCS Y coordinate of the shaft/riser location." }
                },
                Required = new List<string> { "x", "y" }
            };
        }

        private static JsonSchemaObject CreateFloorBoundarySchema()
        {
            return new JsonSchemaObject
            {
                Type = "object",
                AdditionalProperties = false,
                Properties = new Dictionary<string, JsonSchemaObject>
                {
                    ["floor_boundary_handle"] = new JsonSchemaObject
                    {
                        Type = "string",
                        Description =
                            "Hex handle of the OUTER closed floor/room parcel to subdivide (not a dashed inner subzone). " +
                            "Or \"auto\" to pick a closed polyline in model space that contains all shaft blocks and hints (may clone to floor boundary layer)."
                    }
                },
                Required = new List<string> { "floor_boundary_handle" }
            };
        }

        private static JsonSchemaObject CreateZonesByCutsSchema()
        {
            var cutSeg = new JsonSchemaObject
            {
                Type = "object",
                AdditionalProperties = false,
                Properties = new Dictionary<string, JsonSchemaObject>
                {
                    ["x1"] = new JsonSchemaObject { Type = "number", Description = "Segment start X (WCS, drawing units)." },
                    ["y1"] = new JsonSchemaObject { Type = "number", Description = "Segment start Y (WCS, drawing units)." },
                    ["x2"] = new JsonSchemaObject { Type = "number", Description = "Segment end X (WCS, drawing units)." },
                    ["y2"] = new JsonSchemaObject { Type = "number", Description = "Segment end Y (WCS, drawing units)." }
                },
                Required = new List<string> { "x1", "y1", "x2", "y2" }
            };

            return new JsonSchemaObject
            {
                Type = "object",
                AdditionalProperties = false,
                Properties = new Dictionary<string, JsonSchemaObject>
                {
                    ["floor_boundary_handle"] = new JsonSchemaObject
                    {
                        Type = "string",
                        Description =
                            "Hex handle of the OUTER closed floor parcel, or \"auto\". Same rules as create_sprinkler_zones."
                    },
                    ["cuts"] = new JsonSchemaObject
                    {
                        Type = "array",
                        Description =
                            "Exactly N−1 line segments for N distinct shaft sites inside the floor. " +
                            "Each segment defines an infinite split line (normal from the segment); cuts are applied in order. " +
                            "Use get_all_closed_polylines + list_shaft_location to pick endpoints on or across the boundary.",
                        Items = cutSeg
                    }
                },
                Required = new List<string> { "floor_boundary_handle", "cuts" }
            };
        }
    }
}
