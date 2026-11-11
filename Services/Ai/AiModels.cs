using System;
using System.Collections.Generic;
using System.Linq;

namespace ImgViewer.Services.Ai;

public enum AiModelTier
{
    /// <summary>Sees the photo and calls tools.</summary>
    Recommended = 0,
    /// <summary>Sees the photo; tool calling less reliable.</summary>
    Vision = 1,
    /// <summary>Calls tools, works from the image statistics only.</summary>
    TextOnly = 2,
    /// <summary>No tool calling: cannot change settings.</summary>
    Unsuitable = 3
}

/// <summary>Classification of a model for the assistant, with the label shown in the model list.</summary>
public sealed record AiModelChoice(string Name, AiModelTier Tier, bool Vision, bool Tools, string Note)
{
    public string Display => Tier switch
    {
        AiModelTier.Recommended => "★ " + Name + "  — " + Note,
        _ => Name + "  — " + Note
    };
}

/// <summary>
/// Which models suit the assistant. The server's capabilities (/api/show) decide; when it gives none, the model family
/// in the name does (the cloud catalogue changes, so there is no fixed list of names).
/// </summary>
public static class AiModels
{
    // families known to see images (name fragments)
    private static readonly string[] VisionHints =
    {
        "-vl", "vl:", "vision", "llava", "gemma3", "gemma-3", "llama4", "llama3.2-vision", "minicpm-v", "moondream",
        "mistral-small3.1", "mistral-small3.2", "qwen2.5vl", "granite3.2-vision", "bakllava", "kimi-vl"
    };

    // families with reliable tool calling
    private static readonly string[] ToolHints =
    {
        "qwen", "llama3.1", "llama3.2", "llama3.3", "llama4", "mistral", "gpt-oss", "deepseek-v3", "kimi", "glm",
        "command-r", "granite3", "hermes", "firefunction", "nemotron", "minimax", "devstral"
    };

    // families known to see images but with weaker or no tool calling
    private static readonly string[] NoToolHints = { "llava", "bakllava", "moondream", "minicpm-v" };

    public static AiModelChoice Classify(AiModelInfo m)
    {
        var n = m.Name.ToLowerInvariant();
        bool vision, tools;
        if (m.CapabilitiesKnown)
        {
            vision = m.Capabilities.Contains("vision");
            tools = m.Capabilities.Contains("tools");
        }
        else
        {
            vision = VisionHints.Any(n.Contains);
            tools = !NoToolHints.Any(n.Contains) && (ToolHints.Any(n.Contains) || vision);
        }
        // gemma3 sees images; its tool calling works through the template but is less steady
        bool weakTools = n.Contains("gemma");
        var tier = !tools ? AiModelTier.Unsuitable
            : vision && !weakTools ? AiModelTier.Recommended
            : vision ? AiModelTier.Vision
            : AiModelTier.TextOnly;
        string note = tier switch
        {
            AiModelTier.Recommended => "recommended: sees the photo and changes settings",
            AiModelTier.Vision => "sees the photo; tool calls may be less reliable",
            AiModelTier.TextOnly => "text only: works from image statistics, does not see the photo",
            _ => "no tool calling: cannot change settings"
        };
        if (n.EndsWith("-cloud", StringComparison.Ordinal) || n.Contains("-cloud:") || n.Contains(":cloud")) note += " (cloud)";
        if (!m.CapabilitiesKnown) note += " (guessed from the name)";
        return new AiModelChoice(m.Name, tier, vision, tools, note);
    }

    /// <summary>Best first: recommended, then vision, text-only, unsuitable; by name within a tier.</summary>
    public static List<AiModelChoice> Rank(IEnumerable<AiModelInfo> models) =>
        models.Select(Classify)
            .OrderBy(c => c.Tier)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
