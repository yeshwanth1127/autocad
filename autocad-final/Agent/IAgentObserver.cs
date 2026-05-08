namespace autocad_final.Agent
{
    /// <summary>
    /// UI observer contract for autonomous agent progress updates.
    /// The runtime uses this interface so agent logic remains UI-framework agnostic.
    /// </summary>
    public interface IAgentObserver
    {
        void OnStep(int stepNum, string description);
        void OnToolCall(string toolName, string zoneId, string paramSummary);
        void OnToolResult(string toolName, bool success, string summary);
        void OnComplete(string finalMessage);
        void OnStopped();
        void OnError(string message);
    }
}
