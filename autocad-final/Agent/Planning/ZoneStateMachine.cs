using System;
using System.Collections.Generic;

namespace autocad_final.Agent.Planning
{
    public enum ZoneDesignState
    {
        Empty,              // no design content
        Cleaned,            // cleanup_zone succeeded
        PipeRouted,         // route_main_pipe succeeded
        SprinklersPlaced,   // place_sprinklers succeeded
        Complete,           // attach_branches succeeded
        Failed              // a step failed — zone must be cleaned before retrying
    }

    /// <summary>
    /// Tracks the design state of each zone boundary handle for the duration of one agent run.
    /// Enforces legal tool ordering so the planner cannot skip steps or apply tools out of sequence.
    /// All methods are called on the UI thread — no locking needed.
    /// </summary>
    public sealed class ZoneStateMachine
    {
        private readonly Dictionary<string, ZoneDesignState> _states =
            new Dictionary<string, ZoneDesignState>(StringComparer.OrdinalIgnoreCase);

        public ZoneDesignState GetState(string boundaryHandle)
        {
            if (string.IsNullOrWhiteSpace(boundaryHandle)) return ZoneDesignState.Empty;
            return _states.TryGetValue(boundaryHandle, out var s) ? s : ZoneDesignState.Empty;
        }

        /// <summary>
        /// Returns true when <paramref name="toolName"/> is permitted to execute for this zone.
        /// Sets <paramref name="blockReason"/> to an actionable message the LLM can act on when blocked.
        /// </summary>
        public bool CanExecute(string toolName, string boundaryHandle, out string blockReason)
        {
            blockReason = null;
            var state = GetState(boundaryHandle);

            // Read-only tools are always allowed.
            if (IsReadTool(toolName))
                return true;

            if (state == ZoneDesignState.Failed)
            {
                blockReason =
                    $"Zone {boundaryHandle} is in Failed state from a previous error. " +
                    $"Call cleanup_zone for this boundary_handle to reset it before retrying.";
                return false;
            }

            switch (toolName.ToLowerInvariant())
            {
                case "cleanup_zone":
                    return true;   // allowed from any state

                case "route_main_pipe":
                    if (state == ZoneDesignState.Empty || state == ZoneDesignState.Cleaned)
                        return true;
                    blockReason = RequiresCleanup(toolName, boundaryHandle, state);
                    return false;

                case "place_sprinklers":
                    if (state == ZoneDesignState.PipeRouted)
                        return true;
                    if (state < ZoneDesignState.PipeRouted)
                    {
                        blockReason =
                            $"place_sprinklers requires main pipe to be routed first. " +
                            $"Call route_main_pipe for boundary_handle {boundaryHandle}, then place_sprinklers.";
                        return false;
                    }
                    blockReason = RequiresCleanup(toolName, boundaryHandle, state);
                    return false;

                case "attach_branches":
                    if (state == ZoneDesignState.SprinklersPlaced)
                        return true;
                    if (state < ZoneDesignState.SprinklersPlaced)
                    {
                        blockReason =
                            $"attach_branches requires sprinklers to be placed first. " +
                            $"Complete route_main_pipe and place_sprinklers for boundary_handle {boundaryHandle} first.";
                        return false;
                    }
                    blockReason = RequiresCleanup(toolName, boundaryHandle, state);
                    return false;

                case "design_zone":
                    return true;   // design_zone runs its own internal pipeline from any state

                default:
                    return true;   // unknown tools pass through; their own guards handle them
            }
        }

        public void RecordSuccess(string toolName, string boundaryHandle)
        {
            if (string.IsNullOrWhiteSpace(boundaryHandle)) return;

            switch (toolName.ToLowerInvariant())
            {
                case "cleanup_zone":
                    _states[boundaryHandle] = ZoneDesignState.Cleaned;
                    break;
                case "route_main_pipe":
                    _states[boundaryHandle] = ZoneDesignState.PipeRouted;
                    break;
                case "place_sprinklers":
                    _states[boundaryHandle] = ZoneDesignState.SprinklersPlaced;
                    break;
                case "attach_branches":
                    _states[boundaryHandle] = ZoneDesignState.Complete;
                    break;
                case "design_zone":
                    _states[boundaryHandle] = ZoneDesignState.Complete;
                    break;
            }
        }

        public void RecordFailure(string toolName, string boundaryHandle)
        {
            if (string.IsNullOrWhiteSpace(boundaryHandle)) return;
            // Only transition to Failed for write tools — read tools never fail the state machine.
            if (!IsReadTool(toolName))
                _states[boundaryHandle] = ZoneDesignState.Failed;
        }

        public void Reset(string boundaryHandle)
        {
            if (!string.IsNullOrWhiteSpace(boundaryHandle))
                _states[boundaryHandle] = ZoneDesignState.Empty;
        }

        private static bool IsReadTool(string toolName)
        {
            if (string.IsNullOrEmpty(toolName)) return false;
            switch (toolName.ToLowerInvariant())
            {
                case "list_zones":
                case "get_zone_geometry":
                case "get_shaft_location":
                case "validate_coverage":
                case "get_xdata_tags":
                case "get_pipe_summary":
                case "evaluate_zone":
                case "get_drawing_census":
                case "get_all_closed_polylines":
                case "get_text_content":
                case "list_entities_on_layer":
                case "get_entity_details":
                case "set_shaft_hint":
                case "clear_shaft_hints":
                    return true;
                default:
                    return false;
            }
        }

        private static string RequiresCleanup(string toolName, string boundaryHandle, ZoneDesignState current)
            => $"{toolName} cannot run on zone {boundaryHandle} (current state: {current}). " +
               $"Call cleanup_zone first to reset the zone before re-running the design pipeline.";
    }
}
