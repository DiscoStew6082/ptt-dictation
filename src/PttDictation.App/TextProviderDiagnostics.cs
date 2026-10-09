using PttDictation.Core;
using System.Diagnostics;
using System.Windows.Automation;

namespace PttDictation.App;

internal static class TextProviderDiagnostics
{
    // Read only the provider's selected range and its ancestors. This is evidence
    // for rejected captures, never permission to adopt or write another editor.
    internal static void InspectUnsupportedSelection(AutomationElement element, TextPattern? pattern)
    {
        try
        {
            DiagnosticTrace.Write("target.unsupported_provider", new {
                framework = element.Current.FrameworkId, className = element.Current.ClassName,
                hasKeyboardFocus = element.Current.HasKeyboardFocus });
            if (pattern is null) return;
            var selection = pattern.GetSelection();
            DiagnosticTrace.Write("target.unsupported_selection", new { count = selection.Length });
            if (selection.Length != 1) return;
            var selected = selection[0];
            var selectedReadOnly = selected.GetAttributeValue(TextPattern.IsReadOnlyAttribute);
            DiagnosticTrace.Write("target.unsupported_selection_range", new {
                collapsed = selected.CompareEndpoints(System.Windows.Automation.Text.TextPatternRangeEndpoint.Start,
                    selected, System.Windows.Automation.Text.TextPatternRangeEndpoint.End) == 0,
                readOnly = selectedReadOnly is bool value ? (bool?)value : null });
            var enclosing = selected.GetEnclosingElement();
            for (var depth = 0; enclosing is not null && depth < 8; depth++)
            {
                var current = enclosing.Current;
                var sameAsCaptured = Automation.Compare(element, enclosing);
                var valuePattern = enclosing.TryGetCurrentPattern(ValuePattern.Pattern, out var raw)
                    ? raw as ValuePattern : null;
                DiagnosticTrace.Write("target.unsupported_selection_ancestor", new {
                    depth, sameAsCaptured, controlType = current.ControlType.ProgrammaticName,
                    hasKeyboardFocus = current.HasKeyboardFocus, enabled = current.IsEnabled,
                    password = current.IsPassword, valueReadOnly = valuePattern?.Current.IsReadOnly });
                if (sameAsCaptured) break;
                enclosing = TreeWalker.RawViewWalker.GetParent(enclosing);
            }
        }
        catch (Exception error) when (WindowsTextTarget.IsProviderFailure(error))
        {
            DiagnosticTrace.Write("target.unsupported_selection_probe_failed",
                new { exceptionType = error.GetType().FullName, error.HResult });
        }
    }

    internal static T Observe<T>(string operation, Func<T> read)
    {
        var started = Stopwatch.GetTimestamp();
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
        finally
        {
            var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (elapsedMs >= 250)
                DiagnosticTrace.Write("target.provider_operation_slow", new { operation, elapsedMs });
        }
    }
}
