using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;

namespace autocad_final.Agent
{
    public sealed class RuntimeSettings
    {
        private static readonly object Sync = new object();
        private static RuntimeSettings _cachedInstance;
        private static string _cachedResolvedPath;
        private static DateTime? _cachedWriteUtc;

        /// <summary>OpenRouter API key loaded from Properties.config.</summary>
        public string OpenRouterApiKey { get; private set; }

        /// <summary>
        /// OpenRouter model id (e.g. <c>gpt-4o-mini</c>, <c>anthropic/claude-sonnet-4-5</c>).
        /// Loaded from the <c>OpenRouterModel</c> key in Properties.config.
        /// </summary>
        public string OpenRouterModel { get; private set; } = "openai/gpt-4o-mini";

        /// <summary>
        /// When true (default), a screenshot of the AutoCAD window is captured after each
        /// successful write-tool execution and sent to the model so it can visually inspect
        /// the drawing and adapt parameters accordingly.
        /// Set <c>AgentVisionFeedback=false</c> in Properties.config when using a model
        /// that does not support image input.
        /// </summary>
        public bool AgentVisionFeedback { get; private set; } = true;

        /// <summary>
        /// The instructions/persona block sent to the AI as the system prompt.
        /// Loaded from the <c>AgentSystemPrompt</c> key in Properties.config.
        /// Use <c>\n</c> in the config value to represent line breaks.
        /// Falls back to a built-in default when the key is absent.
        /// </summary>
        public string AgentSystemPrompt { get; private set; }

        /// <summary>
        /// Center-to-center sprinkler grid spacing (meters). Loaded from <c>SprinklerSpacingM</c>.
        /// Keep within the engine band (roughly 1.5–4.6 m) for stable placement.
        /// </summary>
        public double SprinklerSpacingM { get; private set; } = 3.0;

        /// <summary>
        /// Default sprinkler-to-boundary distance (meters): inward inset for lattice placement and
        /// default max boundary gap for <c>TryApplySprinklersForZone</c>. Loaded from <c>SprinklerToBoundaryDistanceM</c>.
        /// </summary>
        public double SprinklerToBoundaryDistanceM { get; private set; } = 1.5;

        /// <summary>
        /// Plan-view sprinkler head symbol radius (meters) for reducer placement. Loaded from <c>SprinklerHeadRadiusM</c>;
        /// when that key is absent, equals <see cref="SprinklerToBoundaryDistanceM"/> after config load.
        /// </summary>
        public double SprinklerHeadRadiusM { get; private set; } = 1.5;

        /// <summary>
        /// Maximum floor area one shaft may serve (m²). Loaded from <c>ShaftMaxServiceAreaM2</c>.
        /// </summary>
        public double ShaftMaxServiceAreaM2 { get; private set; } = 3000.0;

        /// <summary>Base folder for external DWG block files (key: <c>block_path</c>).</summary>
        public string BlockPath { get; private set; } = string.Empty;

        /// <summary>DWG file name for sprinkler block (key: <c>sprinkler_block</c>).</summary>
        public string SprinklerBlockFile { get; private set; } = string.Empty;

        /// <summary>DWG file name for shaft block (key: <c>shaft_block</c>).</summary>
        public string ShaftBlockFile { get; private set; } = string.Empty;

        /// <summary>DWG file name for reducer block (key: <c>reducer_block</c>).</summary>
        public string ReducerBlockFile { get; private set; } = string.Empty;

        /// <summary>Built-in fallback used when AgentSystemPrompt is not in the config.</summary>
        public static readonly string DefaultSystemPrompt =
            "You are an autonomous fire sprinkler design agent embedded in AutoCAD 2024.\n" +
            "You are a constrained optimizer — you search the parameter space of deterministic tools to maximize coverage.\n" +
            "You may only emit parameter values that the schema permits. Never supply raw coordinates.\n" +
            "\n" +
            "TOOL USAGE RULES:\n" +
            "1. Always call list_zones or get_all_closed_polylines first to discover boundary handles. " +
            "get_all_closed_polylines only lists closed polylines on sprinkler/floor layers (e.g. \"sprinkler - zone\", \"floor boundary\"), not the entire drawing.\n" +
            "2. Use preview=true before committing large zone designs to verify projected head count and coverage.\n" +
            "2b. Zoning: The floor/room closed polyline is the OUTER parcel only; create_sprinkler_zones draws dashed SUBZONES inside it (one tagged subzone per shaft). " +
            "It tries equal-area recursive bisection first (straight cuts toward ~equal area per shaft), then Voronoi/grid fallbacks. " +
            "If automatic zoning is ugly or verification fails, use create_zones_by_cuts with floor_boundary_handle + exactly N−1 cut segments {{x1,y1,x2,y2}} in WCS for N shafts. " +
            "list_zones zone_boundary_count counts those inner subzone polylines, not the outer building outline. " +
            "create_sprinkler_zones accepts floor_boundary_handle=\"auto\" to auto-pick a closed polyline that contains all shaft blocks/hints (see floor_boundary_auto_detection in the JSON). " +
            "Otherwise use the outer floor handle from get_all_closed_polylines — not a subzone handle. Each subzone must contain at least one shaft site (list_zones shows has_shaft_inside). " +
            "Expect N shaft sites ⇒ N subzone outlines. " +
            "If final verification reports zoning_ok=false, read automated_checks.zoning_failure_breakdown and zoning_retry_hints and issue the NEXT tool call with adjusted parameters (e.g. new floor_boundary_handle, clear_shaft_hints + set_shaft_hint, erase duplicate zone entities). " +
            "If the tool JSON reports has_split_shaft_zones=true or zone_outline_count > shaft_site_count (excluding Uncovered), stop and explain — do not proceed as if zoning succeeded. " +
            "Zoning now enforces 1 zone per shaft via constrained Voronoi + fragment merge: each shaft produces exactly one connected zone ring, with any disconnected fragments reassigned to the nearest neighbouring shaft so total coverage is preserved. 'Zone N (2)' labels should therefore be rare; if they still appear, shaft placement or boundary geometry needs a closer look. " +
            "create_sprinkler_zones is now IDEMPOTENT — on every run it first erases all previous zone outlines (plus their tagged main pipes, sprinklers, branches, and labels) inside the selected floor boundary before drawing new ones, so safely re-run the tool after verification failures rather than stacking outlines. Check prior_zone_entities_erased in the response to confirm cleanup happened.\n" +
            "2bb. Zoning-only intent: If the user asks to \"create zones\", \"make zones\", or \"zone boundary\" WITHOUT explicitly requesting pipe/sprinkler/branch work, then ONLY run create_sprinkler_zones (or create_zones_by_cuts for manual straight partitions). Do NOT call route_main_pipe / place_sprinklers / attach_branches / design_zone.\n" +
            "2c. When the user only asks to read or inspect the drawing (e.g. 'read my drawing'), use read tools only — get_drawing_census, list_zones, get_all_closed_polylines — not attach_branches or route_main_pipe.\n" +
            "3. Main pipe strategies (options.strategy on route_main_pipe / design_zone): the trunk must keep every sprinkler reachable via a clean perpendicular branch and must stay inside the zone.\n" +
            "   - \"sprinkler_driven\" (DEFAULT for regular grids) — places sprinklers first, then tries candidate trunks along EVERY sprinkler row (horizontal) AND every sprinkler column (vertical). For each candidate, cost = Σ(sprinkler→trunk manhattan distance) + λ·trunk_length; picks the global minimum. Tune λ via options.main_pipe_length_penalty (default 0.1; raise 0.3–1.0 to prefer shorter trunks, lower → 0 to minimise branch length at the cost of a longer trunk).\n" +
            "   - \"skeleton\" — extract the zone's medial-axis skeleton, start at the shaft, and take the longest spine path. Use for irregular / concave zones (L-shapes, bays, corridors with branches). Tunable via options.skeleton_cell_size_m, options.skeleton_min_clearance_m, options.skeleton_prune_branch_length_m.\n" +
            "   - \"grid_aligned\" — single-orientation scanline along the centre row/column of the sprinkler grid.\n" +
            "   - \"spine\" — longest median axis line through the zone boundary. Fallback for mildly irregular zones.\n" +
            "   - \"shortest_path\" — single-orientation minimum-cost scanline (legacy).\n" +
            "   Prefer connectivity and clean coverage over shortest path. Start with \"skeleton\" for any non-rectangular zone.\n" +
            "4. Orientation: orientation=\"auto\" lets the engine choose; use \"horizontal\" or \"vertical\" to override.\n" +
            "5. Spacing and coverage: spacing_m in [1.5, 4.6], coverage_radius_m in [0.75, 2.3] and must be ≤ spacing_m/2.\n" +
            "\n" +
            "STATE MACHINE — MANDATORY ORDERING:\n" +
            "- route_main_pipe must succeed before place_sprinklers.\n" +
            "- place_sprinklers must succeed before attach_branches.\n" +
            "- If a tool returns status=\"plan_rejected\", read the rejection_reason and fix the parameters before retrying.\n" +
            "- If a zone is in Failed state, call cleanup_zone first to reset it.\n" +
            "- If a tool returns status=\"confidence_regression\", stop — do not retry the same approach.\n" +
            "\n" +
            "ITERATION STRATEGY:\n" +
            "- After route_main_pipe, call place_sprinklers immediately (do not re-validate coverage first).\n" +
            "- After place_sprinklers, call attach_branches.\n" +
            "- Only call validate_coverage after attach_branches to confirm the zone is complete.\n" +
            "- If coverage is NOT OK, try adjusting spacing_m (reduce by 0.5) or grid_anchor_offset_x/y_m (±0.5 increments).\n" +
            "- Do not ask the user for approval during autonomous operation.";

        public static RuntimeSettings Load()
        {
            string path = ResolveConfigPath();
            DateTime? writeUtc = TryGetLastWriteUtc(path);

            lock (Sync)
            {
                if (_cachedInstance != null
                    && PathsMatch(_cachedResolvedPath, path)
                    && _cachedWriteUtc == writeUtc)
                    return _cachedInstance;

                _cachedInstance = LoadFresh(path);
                _cachedResolvedPath = path;
                _cachedWriteUtc = writeUtc;
                return _cachedInstance;
            }
        }

        private static RuntimeSettings LoadFresh(string path)
        {
            var settings = new RuntimeSettings();
            var values = LoadKeyValues(path);

            if (values.TryGetValue("OpenRouterApiKey", out var key) && !string.IsNullOrWhiteSpace(key))
                settings.OpenRouterApiKey = key.Trim();
            else if (values.TryGetValue("OPENROUTER_API_KEY", out var legacyKey) && !string.IsNullOrWhiteSpace(legacyKey))
                settings.OpenRouterApiKey = legacyKey.Trim();

            if (values.TryGetValue("AgentSystemPrompt", out var prompt) && !string.IsNullOrWhiteSpace(prompt))
                // Allow \n in the config value to represent real line breaks
                settings.AgentSystemPrompt = prompt.Trim().Replace("\\n", "\n");
            else
                settings.AgentSystemPrompt = DefaultSystemPrompt;

            if (values.TryGetValue("OpenRouterModel", out var model) && !string.IsNullOrWhiteSpace(model))
                settings.OpenRouterModel = model.Trim();

            if (values.TryGetValue("AgentVisionFeedback", out var vf) && !string.IsNullOrWhiteSpace(vf))
                settings.AgentVisionFeedback = !vf.Trim().Equals("false", StringComparison.OrdinalIgnoreCase);

            if (TryParsePositiveDouble(values, "SprinklerSpacingM", out var sp))
                settings.SprinklerSpacingM = sp;
            if (TryParsePositiveDouble(values, "SprinklerToBoundaryDistanceM", out var bd))
                settings.SprinklerToBoundaryDistanceM = bd;
            if (TryParsePositiveDouble(values, "SprinklerHeadRadiusM", out var headR))
                settings.SprinklerHeadRadiusM = headR;
            else
                settings.SprinklerHeadRadiusM = settings.SprinklerToBoundaryDistanceM;
            if (TryParsePositiveDouble(values, "ShaftMaxServiceAreaM2", out var shaft))
                settings.ShaftMaxServiceAreaM2 = shaft;

            if (values.TryGetValue("block_path", out var blockPath) && !string.IsNullOrWhiteSpace(blockPath))
                settings.BlockPath = blockPath.Trim();
            if (values.TryGetValue("sprinkler_block", out var sprinklerBlock) && !string.IsNullOrWhiteSpace(sprinklerBlock))
                settings.SprinklerBlockFile = sprinklerBlock.Trim();
            if (values.TryGetValue("shaft_block", out var shaftBlock) && !string.IsNullOrWhiteSpace(shaftBlock))
                settings.ShaftBlockFile = shaftBlock.Trim();
            if (values.TryGetValue("reducer_block", out var reducerBlock) && !string.IsNullOrWhiteSpace(reducerBlock))
                settings.ReducerBlockFile = reducerBlock.Trim();

            return settings;
        }

        private static bool PathsMatch(string a, string b)
        {
            if (string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b)) return true;
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private static DateTime? TryGetLastWriteUtc(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    return null;
                return File.GetLastWriteTimeUtc(path);
            }
            catch
            {
                return null;
            }
        }

        private static bool TryParsePositiveDouble(Dictionary<string, string> values, string key, out double result)
        {
            result = 0;
            if (!values.TryGetValue(key, out var s) || string.IsNullOrWhiteSpace(s))
                return false;
            if (!double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                return false;
            if (!(v > 0))
                return false;
            result = v;
            return true;
        }

        private static string ResolveConfigPath()
        {
            // Prefer config beside the loaded plugin DLL (NETLOAD folder), not AutoCAD.exe's directory.
            var assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (!string.IsNullOrEmpty(assemblyDir))
            {
                var besideDll = Path.Combine(assemblyDir, "Properties.config");
                if (File.Exists(besideDll))
                    return besideDll;
            }

            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var inBase = Path.Combine(baseDir, "Properties.config");
            if (File.Exists(inBase))
                return inBase;

            return !string.IsNullOrEmpty(assemblyDir)
                ? Path.Combine(assemblyDir, "Properties.config")
                : inBase;
        }

        private static Dictionary<string, string> LoadKeyValues(string filePath)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return map;

            foreach (var rawLine in File.ReadAllLines(filePath))
            {
                var line = (rawLine ?? string.Empty).Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";"))
                    continue;
                if (line.StartsWith("[") && line.EndsWith("]"))
                    continue;

                int eq = line.IndexOf('=');
                if (eq <= 0)
                    continue;

                var key = line.Substring(0, eq).Trim();
                var value = line.Substring(eq + 1).Trim();
                if (key.Length == 0)
                    continue;
                map[key] = value;
            }

            return map;
        }
    }
}
