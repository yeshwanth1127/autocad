using System.Collections.Generic;
using System.Runtime.Serialization;

namespace autocad_final.Agent
{
    // Request-only types below do NOT carry [DataContract] so that JsonSupport.Serialize
    // routes them through SerializeReflection, which always emits IDictionary as a JSON
    // object.  DataContractJsonSerializer's UseSimpleDictionaryFormat is unreliable for
    // nested Dictionary<string,T> and produces an array → Anthropic HTTP 400.
    // [DataMember] attributes are kept solely for JSON name mapping (snake_case).
    public sealed class OpenRouterRequest
    {
        [DataMember(Name = "model")]
        public string Model { get; set; }

        [DataMember(Name = "messages")]
        public List<OpenRouterMessage> Messages { get; set; }

        [DataMember(Name = "tools")]
        public List<OpenRouterToolDefinition> Tools { get; set; }

        [DataMember(Name = "tool_choice")]
        public string ToolChoice { get; set; }

        [DataMember(Name = "max_tokens")]
        public int MaxTokens { get; set; }
    }

    public sealed class OpenRouterMessage
    {
        [DataMember(Name = "role")]
        public string Role { get; set; }

        /// <summary>
        /// Message content. Assign a plain <c>string</c> for text-only turns.
        /// For vision turns, leave null and set <see cref="ContentParts"/> instead.
        /// <see cref="ContentForSerialization"/> merges both for JSON output.
        /// </summary>
        public string Content { get; set; }

        /// <summary>
        /// Multipart content blocks (text + image) used for vision turns.
        /// When set, <see cref="Content"/> must be null.
        /// </summary>
        public List<object> ContentParts { get; set; }

        /// <summary>
        /// Serialized as "content" — returns <see cref="ContentParts"/> when present,
        /// otherwise falls back to <see cref="Content"/> string.
        /// EmitDefaultValue=false omits the key entirely when both are null
        /// (e.g. assistant messages that only carry tool_calls).
        /// </summary>
        [DataMember(Name = "content", EmitDefaultValue = false)]
        public object ContentForSerialization => (object)ContentParts ?? Content;

        [DataMember(Name = "tool_call_id", EmitDefaultValue = false)]
        public string ToolCallId { get; set; }

        [DataMember(Name = "tool_calls", EmitDefaultValue = false)]
        public List<OpenRouterToolCall> ToolCalls { get; set; }

        /// <summary>
        /// Creates a user message that bundles a text prompt with a drawing screenshot.
        /// Requires a vision-capable model (gpt-4o, claude-3+, etc.).
        /// </summary>
        public static OpenRouterMessage VisionUser(string text, string base64Png) =>
            new OpenRouterMessage
            {
                Role = "user",
                ContentParts = new List<object>
                {
                    new Dictionary<string, object> { ["type"] = "text",  ["text"] = text },
                    new Dictionary<string, object>
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new Dictionary<string, object>
                        {
                            ["url"] = "data:image/png;base64," + base64Png
                        }
                    }
                }
            };
    }

    [DataContract]
    public sealed class OpenRouterToolCall
    {
        [DataMember(Name = "id")]
        public string Id { get; set; }

        [DataMember(Name = "type")]
        public string Type { get; set; }

        [DataMember(Name = "function")]
        public OpenRouterToolFunctionCall Function { get; set; }
    }

    [DataContract]
    public sealed class OpenRouterToolFunctionCall
    {
        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "arguments")]
        public string Arguments { get; set; }
    }

    [DataContract]
    public sealed class OpenRouterResponse
    {
        [DataMember(Name = "choices")]
        public List<OpenRouterChoice> Choices { get; set; }
    }

    [DataContract]
    public sealed class OpenRouterChoice
    {
        [DataMember(Name = "finish_reason")]
        public string FinishReason { get; set; }

        [DataMember(Name = "message")]
        public OpenRouterResponseMessage Message { get; set; }
    }

    [DataContract]
    public sealed class OpenRouterResponseMessage
    {
        [DataMember(Name = "role")]
        public string Role { get; set; }

        [DataMember(Name = "content")]
        public string Content { get; set; }

        [DataMember(Name = "tool_calls")]
        public List<OpenRouterToolCall> ToolCalls { get; set; }
    }

    public sealed class OpenRouterToolDefinition
    {
        [DataMember(Name = "type")]
        public string Type { get; set; } = "function";

        [DataMember(Name = "function")]
        public OpenRouterFunctionDefinition Function { get; set; }
    }

    public sealed class OpenRouterFunctionDefinition
    {
        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "description")]
        public string Description { get; set; }

        [DataMember(Name = "parameters")]
        public JsonSchemaObject Parameters { get; set; }
    }

    public sealed class JsonSchemaObject
    {
        [DataMember(Name = "type", EmitDefaultValue = false)]
        public string Type { get; set; }

        // EmitDefaultValue=false prevents "properties":null when a property has no sub-properties.
        // Anthropic's API requires properties to be a dictionary object (or absent), never null.
        [DataMember(Name = "properties", EmitDefaultValue = false)]
        public Dictionary<string, JsonSchemaObject> Properties { get; set; }

        [DataMember(Name = "required", EmitDefaultValue = false)]
        public List<string> Required { get; set; }

        // additionalProperties is not part of Anthropic's tool schema spec — omit when absent.
        [DataMember(Name = "additionalProperties", EmitDefaultValue = false)]
        public bool? AdditionalProperties { get; set; }

        [DataMember(Name = "items", EmitDefaultValue = false)]
        public JsonSchemaObject Items { get; set; }

        [DataMember(Name = "description", EmitDefaultValue = false)]
        public string Description { get; set; }

        [DataMember(Name = "enum", EmitDefaultValue = false)]
        public List<string> Enum { get; set; }
    }
}
