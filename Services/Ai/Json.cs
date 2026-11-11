using System.Text.Json.Nodes;

namespace ImgViewer.Services.Ai;

/// <summary>JSON helpers that also build for the Win7 target (net6 has no JsonNode.DeepClone).</summary>
internal static class Json
{
    public static JsonNode Clone(JsonNode node) => JsonNode.Parse(node.ToJsonString())!;
}
