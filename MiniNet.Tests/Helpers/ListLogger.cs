using MiniNet.Logging;

namespace MiniNet.Tests.Helpers;

public sealed class ListLogger : ILogger
{
    public List<string> Messages { get; } = new();
    public void Log(string message) => Messages.Add(message);
}
