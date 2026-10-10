using System;

namespace TavernLib.Backend.Mods;

public class ModManagerException(string message) : Exception(message)
{
    /// <summary>An exception's message followed by each inner exception's, outermost
    /// first. HttpClient's own message is always "An error occurred while sending the
    /// request"; whether that was DNS, a refused connection or a TLS/certificate failure
    /// is only in the inner exceptions, and without it a failed fetch can't be
    /// diagnosed from the server log.</summary>
    public static string Describe(Exception e)
    {
        var message = e.Message;
        for (var inner = e.InnerException; inner != null; inner = inner.InnerException)
            message += $" -> {inner.GetType().Name}: {inner.Message}";
        return message;
    }
}
