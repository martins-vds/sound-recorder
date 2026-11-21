namespace SoundRecorder.Desktop;

/// <summary>Window presentation effects that may activate or hide a real desktop window.</summary>
public sealed record WindowPresentationActions(Action Show, Action Hide, Func<bool> Activate);
