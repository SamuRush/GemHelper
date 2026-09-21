using Microsoft.Extensions.Configuration;

namespace Gem.Services;

/// <summary>
/// Text-to-speech service delegating to the hybrid resilient CompositeVoiceFeedbackService.
/// Retained for backward compatibility with existing command handlers, singletons, and tests.
/// </summary>
public class VoiceFeedbackService : CompositeVoiceFeedbackService
{
    /// <summary>
    /// Global accessor bridging legacy VoiceFeedbackService.Instance to CompositeVoiceFeedbackService.Instance.
    /// </summary>
    public new static CompositeVoiceFeedbackService? Instance
    {
        get => CompositeVoiceFeedbackService.Instance;
        set => CompositeVoiceFeedbackService.Instance = value;
    }

    public VoiceFeedbackService(VoiceListener? voiceListener = null)
        : base(voiceListener, configuration: null)
    {
    }

    public VoiceFeedbackService(VoiceListener? voiceListener, IConfiguration? configuration)
        : base(voiceListener, configuration)
    {
    }

    public VoiceFeedbackService(VoiceListener? voiceListener, TtsConfig ttsConfig)
        : base(voiceListener, ttsConfig)
    {
    }

    public VoiceFeedbackService(VoiceListener? voiceListener, ITtsEngine edgeTts, ITtsEngine systemSpeech)
        : base(voiceListener, edgeTts, systemSpeech)
    {
    }

    public VoiceFeedbackService(VoiceListener? voiceListener, ITtsEngine edgeTts, ITtsEngine? sileroTts, ITtsEngine systemSpeech)
        : base(voiceListener, edgeTts, sileroTts, systemSpeech)
    {
    }
}
