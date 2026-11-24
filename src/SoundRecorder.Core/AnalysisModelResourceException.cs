namespace SoundRecorder.Core;

/// <summary>An explicit conservative preflight failure; changing resources requires another explicit setup request.</summary>
public sealed class AnalysisModelResourceException(string message) : InvalidOperationException(message);
