namespace Baihua.Contracts.Ai;

public record TtsRequest(
    string Text,
    string Voice,
    float? Speed = null
);

public record TtsVoice(
    string Id,
    string Name,
    string Language,
    string Gender
);

public record TtsVoiceList(List<TtsVoice> Voices);