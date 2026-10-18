namespace Smw2Pc.Core.Recompilation;

public sealed class MissingCompiledCodeException(string message) : InvalidOperationException(message);
