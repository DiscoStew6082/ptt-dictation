using PttDictation.Core;

namespace PttDictation.App;

internal static class TextProviderDiagnostics
{
    internal static T Observe<T>(string operation, Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception error) when (WindowsTextTarget.IsProviderFailure(error))
        {
            DiagnosticTrace.Write("target.provider_operation_failed",
                new { operation, exceptionType = error.GetType().FullName, error.HResult });
            throw;
        }
    }
}
