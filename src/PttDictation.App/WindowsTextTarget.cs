using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using PttDictation.Core;

namespace PttDictation.App;

internal enum TextTargetUpdateResult { Success, Pending, Unfocused, Unsupported, Conflict }

internal interface IWindowsTextTarget
{
    bool IsFocused { get; }
    bool SupportsReplacement { get; }
    bool CanPasteFallback { get; }
    bool IsPreparedSelectionCurrent { get; }
    bool HasAttemptedWrite { get; }
    TextTargetUpdateResult TryReplace(string previousText, string replacementText, Action<string> paste);
}

// A complete partition is required: a provider that normalizes or omits document text
// cannot be used to safely identify the portion owned by this recording.
internal sealed record TextTargetSnapshot(string Document, string Prefix, string Selection, string Suffix)
{
    public bool IsConsistent => Document == Prefix + Selection + Suffix;
}

internal interface IWindowsTextSurface
{
    bool IsFocused { get; }
    bool SupportsReplacement { get; }
    bool CanPasteFallback { get; }
    TextTargetSnapshot Read();
    bool Select(string prefix, string ownedText, string suffix);
    void RevealCaret();
}

internal sealed class WindowsTextTarget : IWindowsTextTarget
{
    private static readonly TimeSpan AcknowledgementTimeout = TimeSpan.FromSeconds(2);
    private readonly IWindowsTextSurface _surface;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TextTargetSnapshot? _initial;
    private readonly TextTargetSnapshot? _capturedSelection;
    private TextTargetSnapshot? _preparedSelection;
    private TextTargetSnapshot? _selectionBeforeRequest;
    private DateTimeOffset _selectionSince;
    private TextTargetSnapshot? _unsentRetrySelection;
    private DateTimeOffset _unsentRetrySince;
    private string _ownedText = "";
    private string? _pendingText;
    private DateTimeOffset _pendingSince;
    private bool _conflicted;
    private bool _hasAcknowledgedWrite;
    private bool _initialDecorationDisappeared;
    private readonly string? _recordingId;

    public static IWindowsTextTarget Capture() => new WindowsTextTarget(AutomationTextSurface.Capture());

    // Only identity is read synchronously at the hotkey boundary. The returned
    // factory performs all document/provider inspection on the automation worker.
    public static Func<IWindowsTextTarget> CaptureFocusedIdentity()
    {
        var recordingId = DiagnosticTrace.CurrentRecordingId;
        AutomationElement? identity;
        try { identity = AutomationElement.FocusedElement; }
        catch (Exception ex) when (IsProviderFailure(ex))
        {
            DiagnosticTrace.Write("target.identity_failed", error: ex, recordingId: recordingId);
            identity = null;
        }
        return () => new WindowsTextTarget(AutomationTextSurface.Capture(identity), recordingId: recordingId);
    }

    internal WindowsTextTarget(IWindowsTextSurface surface, Func<DateTimeOffset>? clock = null, string? recordingId = null)
    {
        _recordingId = recordingId ?? DiagnosticTrace.CurrentRecordingId;
        _surface = surface;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        try
        {
            if (surface.IsFocused)
            {
                var snapshot = surface.Read();
                Trace("target.snapshot", new { snapshot.IsConsistent, documentLength = snapshot.Document.Length, prefixLength = snapshot.Prefix.Length, selectionLength = snapshot.Selection.Length, suffixLength = snapshot.Suffix.Length });
                if (snapshot.IsConsistent)
                {
                    _capturedSelection = snapshot;
                    if (surface.SupportsReplacement) _initial = snapshot;
                }
            }
        }
        catch (Exception ex) when (IsProviderFailure(ex)) { Trace("target.capture_failed", error: ex); }
        Trace("target.capture_completed", new { replacement = _initial is not null, capturedSelection = _capturedSelection is not null });
    }

    public bool IsFocused
    {
        get
        {
            try { return _surface.IsFocused; }
            catch (Exception ex) when (IsProviderFailure(ex)) { Trace("target.focus_read_failed", error: ex); return false; }
        }
    }

    public bool SupportsReplacement => _initial is not null;
    public bool CanPasteFallback => _surface.CanPasteFallback;
    public bool HasAttemptedWrite { get; private set; }
    public bool IsPreparedSelectionCurrent
    {
        get
        {
            try
            {
                if (!IsFocused) { Trace("target.prepared_invalid", new { reason = "unfocused" }); return false; }
                var expected = _preparedSelection ?? _capturedSelection;
                // Some unsupported editors expose no readable selection at all;
                // their fallback still requires the exact captured field.
                if (expected is null) return CanPasteFallback && IsFocused;
                var current = _surface.Read();
                var valid = current.IsConsistent && current == expected && IsFocused;
                if (!valid) Trace("target.prepared_invalid", new { reason = "snapshot_or_focus_changed", current.IsConsistent, snapshotMatches = current == expected });
                return valid;
            }
            catch (Exception ex) when (IsProviderFailure(ex)) { Trace("target.prepared_failed", error: ex); return false; }
        }
    }

    public TextTargetUpdateResult TryReplace(string previousText, string replacementText, Action<string> paste)
    {
        if (!IsFocused) return TextTargetUpdateResult.Unfocused;
        if (_initial is null) return TextTargetUpdateResult.Unsupported;
        if (_conflicted) { Trace("target.conflict", new { reason = "previous_conflict" }); return TextTargetUpdateResult.Conflict; }
        try
        {
            var snapshot = _surface.Read();
            if (_unsentRetrySelection is { } retrySelection)
            {
                if (snapshot != retrySelection) return Conflict("unsent_retry_selection_changed");
                if (_clock() - _unsentRetrySince >= AcknowledgementTimeout)
                    return Conflict("clipboard_read_timeout");
            }
            if (_pendingText is { } pending)
            {
                // Chromium includes CSS-generated empty-editor placeholders in TextPattern.
                // They disappear on input. Adopt no surrounding text only when the first
                // verified collapsed-caret paste leaves exactly our payload and an end caret.
                // Never strip arbitrary text, normalize a mismatch, or do this after an ack.
                if (snapshot.Document != ExpectedDocument(pending) && !_hasAcknowledgedWrite
                    && _initial.Selection.Length == 0 && snapshot.Document == pending
                    && snapshot.Prefix == pending && snapshot.Selection.Length == 0 && snapshot.Suffix.Length == 0)
                {
                    _initialDecorationDisappeared = true;
                    Trace("target.initial_decoration_disappeared", new { prefixLength = _initial.Prefix.Length, suffixLength = _initial.Suffix.Length });
                }
                if (snapshot.Document != ExpectedDocument(pending))
                {
                    if (_clock() - _pendingSince < AcknowledgementTimeout)
                        return TextTargetUpdateResult.Pending;
                    return Conflict("acknowledgement_timeout");
                }

                _ownedText = pending;
                _hasAcknowledgedWrite = true;
                _pendingText = null;
                _preparedSelection = null;
                Trace("target.write_acknowledged", new { ownedLength = _ownedText.Length });
                try { _surface.RevealCaret(); }
                catch (Exception ex) when (IsProviderFailure(ex))
                {
                    Trace("target.reveal_failed", error: ex);
                    // Scrolling is presentation only. A provider without scrolling
                    // must not turn an acknowledged insertion into a failed write.
                }
                // The caller still holds the last acknowledged text while a paste
                // is pending. Acknowledge that update before accepting another one.
                return TextTargetUpdateResult.Success;
            }

            if (previousText != _ownedText) return Conflict("caller_owned_text_mismatch");
            var currentOwned = HasAttemptedWrite ? _ownedText : _initial.Selection;
            if (!snapshot.IsConsistent || snapshot.Document != ExpectedDocument(currentOwned))
                return Conflict("document_partition_or_surroundings_changed");
            if (HasAttemptedWrite && replacementText == _ownedText)
                return TextTargetUpdateResult.Success;
            // Empty initial previews must not erase the user's selected text.
            if (!HasAttemptedWrite && replacementText.Length == 0)
                return TextTargetUpdateResult.Success;
            TextTargetSnapshot selected;
            if (_selectionBeforeRequest is null)
            {
                if (!_surface.Select(Prefix, currentOwned, Suffix))
                    return Conflict("select_owned_range_failed");
                _selectionBeforeRequest = snapshot;
                _selectionSince = _clock();
                selected = _surface.Read();
            }
            else selected = snapshot;
            if (!IsFocused) return TextTargetUpdateResult.Unfocused;
            if (!selected.IsConsistent || selected.Prefix != Prefix
                || selected.Selection != currentOwned || selected.Suffix != Suffix)
            {
                // Chromium can return the old caret briefly after Select succeeds.
                // Wait only for an unchanged pre-request snapshot; never reselect
                // or paste while the requested range is still unacknowledged.
                if (selected == _selectionBeforeRequest)
                {
                    if (_clock() - _selectionSince < AcknowledgementTimeout)
                    {
                        Trace("target.selection_pending");
                        return TextTargetUpdateResult.Pending;
                    }
                    return Conflict("selection_acknowledgement_timeout");
                }
                Trace("target.selection_mismatch", new { selected.IsConsistent,
                    documentLength = selected.Document.Length, prefixLength = selected.Prefix.Length,
                    selectionLength = selected.Selection.Length, suffixLength = selected.Suffix.Length,
                    expectedPrefixLength = Prefix.Length, expectedSelectionLength = currentOwned.Length,
                    expectedSuffixLength = Suffix.Length, documentMatches = selected.Document == ExpectedDocument(currentOwned) });
                return Conflict("selected_range_validation_failed");
            }
            if (!IsFocused) return TextTargetUpdateResult.Unfocused;
            _selectionBeforeRequest = null;
            _preparedSelection = selected;
            var previouslyAttemptedWrite = HasAttemptedWrite;
            HasAttemptedWrite = true;
            _pendingText = replacementText;
            _pendingSince = _clock();
            Trace("target.write_pending", new { replacementLength = replacementText.Length });
            try
            {
                paste(replacementText);
                _unsentRetrySelection = null;
            }
            catch (ClipboardReadUnavailableException)
            {
                // No input was sent. Retry on the existing pump, with a bounded
                // deadline and the exact selected range still required. Never
                // retry an exception from SendPaste or an unacknowledged write.
                HasAttemptedWrite = previouslyAttemptedWrite;
                _pendingText = null;
                if (_unsentRetrySelection is null) _unsentRetrySince = _clock();
                _unsentRetrySelection = selected;
                Trace("target.unsent_paste_retry");
            }
            return TextTargetUpdateResult.Pending;
        }
        catch (Exception ex) when (IsProviderFailure(ex))
        {
            return Conflict("provider_failure", ex);
        }
    }

    private string Prefix => _initialDecorationDisappeared ? "" : _initial!.Prefix;
    private string Suffix => _initialDecorationDisappeared ? "" : _initial!.Suffix;
    private string ExpectedDocument(string text) => Prefix + text + Suffix;
    private TextTargetUpdateResult Conflict(string reason, Exception? error = null)
    {
        Trace("target.conflict", new { reason }, error);
        _conflicted = true;
        return TextTargetUpdateResult.Conflict;
    }

    internal static bool IsProviderFailure(Exception ex) => ex is ElementNotAvailableException
        or InvalidOperationException or NotSupportedException or COMException or ArgumentException;

    private void Trace(string stage, object? data = null, Exception? error = null)
        => DiagnosticTrace.Write(stage, data, error, _recordingId);
}

internal sealed class AutomationTextSurface : IWindowsTextSurface
{
    private readonly AutomationElement? _element;
    private readonly TextPattern? _pattern;
    private readonly IntPtr _foregroundWindow;
    private readonly bool _supportsReplacement;

    private AutomationTextSurface(AutomationElement? element, TextPattern? pattern, bool canPasteFallback = false,
        bool supportsReplacement = false)
    {
        _element = element;
        _pattern = pattern;
        CanPasteFallback = canPasteFallback;
        _supportsReplacement = supportsReplacement;
        _foregroundWindow = GetForegroundWindow();
    }

    internal static AutomationTextSurface Capture()
        => Capture(AutomationElement.FocusedElement);

    internal static AutomationTextSurface Capture(AutomationElement? capturedIdentity)
    {
        var element = capturedIdentity;
        try
        {
            if (element is null || !element.Current.IsEnabled || element.Current.IsPassword)
            {
                DiagnosticTrace.Write("target.surface_rejected", new { reason = "missing_disabled_or_password_field" });
                return new AutomationTextSurface(element, null);
            }
            bool? valueReadOnly = null;
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var rawValue) && rawValue is ValuePattern value)
                valueReadOnly = value.Current.IsReadOnly;
            TextPattern? pattern = null;
            object? textReadOnly = null;
            if (element.TryGetCurrentPattern(TextPattern.Pattern, out var rawText) && rawText is TextPattern text)
            {
                pattern = text;
                textReadOnly = text.DocumentRange.GetAttributeValue(TextPattern.IsReadOnlyAttribute);
            }

            var canPaste = AllowsFallback(element.Current.IsEnabled, element.Current.IsPassword,
                element.Current.ControlType == ControlType.Edit, valueReadOnly, textReadOnly);
            DiagnosticTrace.Write("target.surface_capabilities", new { canPaste, hasTextPattern = pattern is not null, valueReadOnly, textReadOnly = textReadOnly is bool readOnly ? (bool?)readOnly : null });
            if (canPaste && pattern is not null && textReadOnly is false
                && pattern.SupportedTextSelection != SupportedTextSelection.None)
                return new AutomationTextSurface(element, pattern, canPasteFallback: true, supportsReplacement: true);
            return new AutomationTextSurface(element, pattern, canPaste);
        }
        catch (Exception ex) when (WindowsTextTarget.IsProviderFailure(ex)) { DiagnosticTrace.Write("target.surface_capture_failed", error: ex); }
        return new AutomationTextSurface(element, null);
    }

    internal static bool AllowsFallback(bool enabled, bool password, bool editControl, bool? valueReadOnly, object? textReadOnly)
    {
        if (!enabled || password || valueReadOnly is true || textReadOnly is true) return false;
        // A mixed/not-supported read-only attribute is not evidence of writability.
        if (textReadOnly is not null && textReadOnly is not bool) return false;
        return valueReadOnly is false || textReadOnly is false || editControl;
    }

    public bool IsFocused => _foregroundWindow != IntPtr.Zero && GetForegroundWindow() == _foregroundWindow
        && _element is not null && _element.Current.HasKeyboardFocus
        && Automation.Compare(_element, AutomationElement.FocusedElement);
    public bool SupportsReplacement => _supportsReplacement;
    public bool CanPasteFallback { get; }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    public TextTargetSnapshot Read()
    {
        if (_pattern is null) throw new NotSupportedException("This editor exposes no readable text selection.");
        var document = _pattern.DocumentRange;
        var selection = _pattern.GetSelection();
        if (selection.Length != 1) throw new NotSupportedException("Multiple selections are not supported.");
        var prefix = document.Clone();
        prefix.MoveEndpointByRange(TextPatternRangeEndpoint.End, selection[0], TextPatternRangeEndpoint.Start);
        var suffix = document.Clone();
        suffix.MoveEndpointByRange(TextPatternRangeEndpoint.Start, selection[0], TextPatternRangeEndpoint.End);
        return new TextTargetSnapshot(document.GetText(-1), prefix.GetText(-1), selection[0].GetText(-1), suffix.GetText(-1));
    }

    public bool Select(string prefix, string ownedText, string suffix)
    {
        var document = _pattern!.DocumentRange;
        if (document.GetText(-1) != prefix + ownedText + suffix)
        {
            DiagnosticTrace.Write("target.select_rejected", new { reason = "document_changed" });
            return false;
        }
        var owned = document.Clone();
        // Find complete unchanged surroundings rather than equating .NET UTF-16
        // lengths with the provider's character units (emoji and CRLF differ).
        if (prefix.Length > 0)
        {
            var before = document.FindText(prefix, backward: false, ignoreCase: false);
            if (before is null || before.CompareEndpoints(TextPatternRangeEndpoint.Start, document, TextPatternRangeEndpoint.Start) != 0)
            {
                DiagnosticTrace.Write("target.select_rejected", new { reason = "prefix_range_not_found" });
                return false;
            }
            owned.MoveEndpointByRange(TextPatternRangeEndpoint.Start, before, TextPatternRangeEndpoint.End);
        }
        if (suffix.Length > 0)
        {
            var after = document.FindText(suffix, backward: true, ignoreCase: false);
            if (after is null || after.CompareEndpoints(TextPatternRangeEndpoint.End, document, TextPatternRangeEndpoint.End) != 0)
            {
                DiagnosticTrace.Write("target.select_rejected", new { reason = "suffix_range_not_found" });
                return false;
            }
            owned.MoveEndpointByRange(TextPatternRangeEndpoint.End, after, TextPatternRangeEndpoint.Start);
        }
        if (owned.GetText(-1) != ownedText || !IsFocused)
        {
            DiagnosticTrace.Write("target.select_rejected", new { reason = "owned_text_or_focus_changed" });
            return false;
        }
        owned.Select();
        return true;
    }

    public void RevealCaret()
    {
        if (!IsFocused) return;
        var selected = _pattern!.GetSelection();
        if (selected.Length != 1) return;
        var caret = selected[0].Clone();
        caret.MoveEndpointByRange(TextPatternRangeEndpoint.Start, caret, TextPatternRangeEndpoint.End);
        caret.ScrollIntoView(alignToTop: false);
    }
}
