namespace Gem.Core;

/// <summary>
/// States for the JARVIS assistant visual overlay and core processing pipeline.
/// </summary>
public enum JarvisState
{
    /// <summary>
    /// Passive waiting for wake-word (slow breathing cyan glow).
    /// </summary>
    Idle,

    /// <summary>
    /// Active listening for speech command after wake-word (bright neon pulsating scale).
    /// </summary>
    Listening,

    /// <summary>
    /// Requesting interpretation from LLM / LM Studio (blue-violet gradient shifting).
    /// </summary>
    Thinking,

    /// <summary>
    /// Executing command or speaking response (bright flash with smooth return to Idle).
    /// </summary>
    Action
}
