using Autodesk.AutoCAD.ApplicationServices;

namespace autocad_final.Agent
{
    public sealed class ContextBuilder
    {
        public string Build(Document doc, ProjectMemory memory)
        {
            var snapshot = AgentReadTools.BuildSnapshot(doc, memory);
            return ToJson(snapshot);
        }

        public DrawingSnapshot BuildSnapshot(Document doc, ProjectMemory memory)
        {
            return AgentReadTools.BuildSnapshot(doc, memory);
        }

        public string BuildCompactSummary(Document doc, ProjectMemory memory)
        {
            return Build(doc, memory);
        }

        private static string ToJson(object value)
        {
            return JsonSupport.Serialize(value);
        }
    }
}
