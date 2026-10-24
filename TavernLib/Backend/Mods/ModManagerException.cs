using System;

namespace TavernLib.Backend.Mods;

public class ModManagerException(string message) : Exception(message)
{
    /// <summary>The message and every inner exception's, outermost first: the
    /// useful cause of a failed fetch is often only in an inner one.</summary>
    public static string Describe(Exception e)
    {
        var message = e.Message;
        for (var inner = e.InnerException; inner != null; inner = inner.InnerException)
            message += $" -> {inner.GetType().Name}: {inner.Message}";
        return message;
    }
}
