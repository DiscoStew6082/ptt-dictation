using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using PttDictation.Core;

namespace PttDictation.App;

internal enum TextTargetUpdateResult { Success, Pending, Unfocused, Unsupported, Conflict }
internal enum TextPatternRequeryResult { NotSupported, Refreshed, Rejected }

internal sealed class TextTargetUnavailableException(ElementNotAvailableException innerException)
    : InvalidOperationException("Windows can no longer access the original textbox's automation reference. The textbox may still be visible; its identity and selected text cannot be verified safely.", innerException)
{ }

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
    // Refresh only the original provider reference; never adopt another editor.
    bool TryRefreshOriginalReference() => false;
    TextPatternRequeryResult RequerySelectionPattern() => TextPatternRequeryResult.NotSupported;
}

internal sealed class WindowsTextTarget : IWindowsTextTarget
{
    private static readonly TimeSpan AcknowledgementTimeout = TimeSpan.FromSeconds(2);
    private readonly IWindowsTextSurface _surface;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<long>? _readFocusGeneration;
    private readonly TextTargetSnapshot? _initial;
    private readonly TextTargetSnapshot? _capturedSelection;
    private TextTargetSnapshot? _preparedSelection;
    private TextTargetSnapshot? _selectionBeforeRequest;
    private bool _selectionPatternRequeryAttempted;
    private DateTimeOffset _selectionSince;
    private long _selectionRequestOrdinal;
    private long? _selectionGenerationAtRequest;
    private TextTargetSnapshot? _unsentRetrySelection;
    private DateTimeOffset _unsentRetrySince;
    private string _ownedText = "";
    private string? _pendingText;
    private DateTimeOffset _pendingSince;
    private bool _conflicted;
    private bool _hasAcknowledgedWrite;
    private bool _initialDecorationDisappeared;
    private bool _originalReferenceRefreshAttempted;
    private readonly string? _recordingId;

    public static IWindowsTextTarget Capture() => new WindowsTextTarget(AutomationTextSurface.Capture());

    // Only identity is read synchronously at the hotkey boundary. The returned
    // factory performs all document/provider inspection on the automation worker.
    public static Func<IWindowsTextTarget> CaptureFocusedIdentity(Func<long>? readFocusGeneration = null)
    {
        var recordingId = DiagnosticTrace.CurrentRecordingId;
        var generation = readFocusGeneration?.Invoke();
        var foreground = GetForegroundWindow();
        GetWindowThreadProcessId(foreground, out var processId);
        AutomationElement? identity;
        try { identity = AutomationElement.FocusedElement; }
        catch (Exception ex) when (IsProviderFailure(ex))
        {
            DiagnosticTrace.Write("target.identity_failed", error: ex, recordingId: recordingId);
            identity = null;
        }
        bool OriginalWindowCurrent()
        {
            if (foreground == IntPtr.Zero || processId == 0 || foreground != GetForegroundWindow()) return false;
            GetWindowThreadProcessId(foreground, out var currentProcess);
            return currentProcess == processId;
        }
        bool CaptureUnchanged() => generation is not null && generation == readFocusGeneration?.Invoke()
            && OriginalWindowCurrent();

        return () =>
        {
            var firstInspection = true;
            var legacyContainer = false;
            var captured = InitialTextTargetCapture.Capture(identity,
                element =>
                {
                    var surface = AutomationTextSurface.Capture(element);
                    var target = new WindowsTextTarget(surface, recordingId: recordingId,
                        readFocusGeneration: readFocusGeneration);
                    if (firstInspection)
                    {
                        firstInspection = false;
                        legacyContainer = surface.IsLegacyFocusContainer && element is not null
                            && element.Current.ProcessId == processId;
                    }
                    return new InitialTextTargetCandidate(target, surface.IsUnsupportedInitialPane
                        && element is not null && element.Current.ProcessId == processId);
                },
                () => AutomationElement.FocusedElement,
                (original, candidate) =>
                {
                    if (candidate.Current.ProcessId != processId || !candidate.Current.HasKeyboardFocus) return false;
                    // Refine only the exact captured container. Never search for an
                    // arbitrary editor or infer identity from its text or position.
                    AutomationElement? ancestor = candidate;
                    for (var depth = 0; ancestor is not null && depth < 32; depth++)
                    {
                        if (Automation.Compare(original, ancestor)) return true;
                        ancestor = TreeWalker.RawViewWalker.GetParent(ancestor);
                    }
                    return false;
                },
                CaptureUnchanged, Thread.Sleep,
                (reason, attempt) => DiagnosticTrace.Write("target.initial_capture_recovery",
                    new { reason, attempt }, recordingId: recordingId));
            if (captured.SupportsReplacement || captured.CanPasteFallback || !legacyContainer
                || !CaptureUnchanged()) return captured;

            // A browser's read-only UIA container may have a more precise MSAA
            // keyboard-focus target. The container remains unwritable; only
            // an independently verified, exact focused text child earns fallback.
            var legacy = LegacyAccessibleTextTarget.TryCapture(
                () => NativeLegacyAccessibleNode.FromWindow(foreground), processId,
                CaptureUnchanged, OriginalWindowCurrent);
            DiagnosticTrace.Write("target.legacy_focus_capture", new { accepted = legacy is not null },
                recordingId: recordingId);
            return legacy ?? captured;
        };
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);

    internal WindowsTextTarget(IWindowsTextSurface surface, Func<DateTimeOffset>? clock = null, string? recordingId = null,
        Func<long>? readFocusGeneration = null)
    {
        _recordingId = recordingId ?? DiagnosticTrace.CurrentRecordingId;
        _surface = surface;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _readFocusGeneration = readFocusGeneration;
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
            try { return ReadOriginalSurface(() => _surface.IsFocused); }
            catch (ElementNotAvailableException ex)
            {
                // The captured automation reference cannot verify the original
                // editor. Its visible textbox may still exist after a UI rebuild.
                // Never redirect this recording to a newly focused element.
                throw Unavailable("focus", ex);
            }
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
                var current = ReadOriginalSurface(_surface.Read);
                var valid = current.IsConsistent && current == expected && IsFocused;
                if (!valid) Trace("target.prepared_invalid", new { reason = "snapshot_or_focus_changed", current.IsConsistent, snapshotMatches = current == expected });
                return valid;
            }
            catch (ElementNotAvailableException ex) { throw Unavailable("prepared_selection", ex); }
            catch (Exception ex) when (ex is not TextTargetUnavailableException && IsProviderFailure(ex))
            {
                Trace("target.prepared_failed", error: ex);
                return false;
            }
        }
    }

    public TextTargetUpdateResult TryReplace(string previousText, string replacementText, Action<string> paste)
    {
        if (!IsFocused) return TextTargetUpdateResult.Unfocused;
        if (_initial is null) return TextTargetUpdateResult.Unsupported;
        if (_conflicted) { Trace("target.conflict", new { reason = "previous_conflict" }); return TextTargetUpdateResult.Conflict; }
        try
        {
            var snapshot = ReadOriginalSurface(_surface.Read);
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
                _selectionRequestOrdinal++;
                _selectionGenerationAtRequest = ReadFocusGenerationForDiagnostics();
                TraceSelectionObservation("target.selection_requested", currentOwned, snapshot, snapshot, TimeSpan.Zero);
                if (!_surface.Select(Prefix, currentOwned, Suffix))
                    return Conflict("select_owned_range_failed");
                _selectionBeforeRequest = snapshot;
                _selectionPatternRequeryAttempted = false;
                _selectionSince = _clock();
                selected = ReadOriginalSurface(_surface.Read);
            }
            else selected = snapshot;
            if (!IsFocused) return TextTargetUpdateResult.Unfocused;
            if (!MatchesOwnedSelection(selected, currentOwned) && selected == _selectionBeforeRequest
                && !_selectionPatternRequeryAttempted && _clock() - _selectionSince < AcknowledgementTimeout)
            {
                // A provider can keep returning its pre-Select caret. Query the
                // SAME element's pattern once; never repeat Select or extend its
                // deadline. A new pattern is not proof of success: the exact
                // original document partition and focus still govern the paste.
                _selectionPatternRequeryAttempted = true;
                TraceSelectionObservation("target.selection_requery_requested", currentOwned,
                    selected, _selectionBeforeRequest!, _clock() - _selectionSince);
                var requery = _surface.RequerySelectionPattern();
                Trace("target.selection_requery_result", new { result = requery.ToString(), requestOrdinal = _selectionRequestOrdinal });
                if (requery == TextPatternRequeryResult.Rejected)
                {
                    if (!IsFocused) return TextTargetUpdateResult.Unfocused;
                    return Conflict("selection_provider_requery_rejected");
                }
                if (requery == TextPatternRequeryResult.Refreshed)
                {
                    // Do not recursively refresh an unavailable query/read.
                    selected = _surface.Read();
                    var elapsed = _clock() - _selectionSince;
                    TraceSelectionObservation("target.selection_requery_observed", currentOwned,
                        selected, _selectionBeforeRequest!, elapsed);
                    if (elapsed >= AcknowledgementTimeout)
                        return Conflict("selection_acknowledgement_timeout");
                    if (!IsFocused) return TextTargetUpdateResult.Unfocused;
                }
            }
            if (!MatchesOwnedSelection(selected, currentOwned))
            {
                // Chromium can return the old caret briefly after Select succeeds.
                // Wait only for an unchanged pre-request snapshot; never reselect
                // or paste while the requested range is still unacknowledged.
                if (selected == _selectionBeforeRequest)
                {
                    var elapsed = _clock() - _selectionSince;
                    if (elapsed < AcknowledgementTimeout)
                    {
                        Trace("target.selection_pending");
                        return TextTargetUpdateResult.Pending;
                    }
                    TraceSelectionObservation("target.selection_timeout", currentOwned, selected, _selectionBeforeRequest, elapsed);
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
            TraceSelectionObservation("target.selection_acknowledged", currentOwned, selected, _selectionBeforeRequest!,
                _clock() - _selectionSince);
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
        catch (ElementNotAvailableException ex) { throw Unavailable("replacement", ex); }
        catch (Exception ex) when (ex is not TextTargetUnavailableException && IsProviderFailure(ex))
        {
            return Conflict("provider_failure", ex);
        }
    }

    private T ReadOriginalSurface<T>(Func<T> read)
    {
        try { return read(); }
        catch (ElementNotAvailableException) when (!_originalReferenceRefreshAttempted)
        {
            // Retry a read once, never selection or clipboard input. Do not
            // acquire a new focused element: even UIA runtime IDs can be reused.
            _originalReferenceRefreshAttempted = true;
            Trace("target.original_reference_refresh_requested");
            try
            {
                if (_surface.TryRefreshOriginalReference())
                {
                    var result = read();
                    Trace("target.original_reference_refreshed");
                    return result;
                }
                Trace("target.original_reference_refresh_rejected");
            }
            catch (Exception error) when (IsProviderFailure(error))
            {
                Trace("target.original_reference_refresh_failed",
                    new { exceptionType = error.GetType().FullName, error.HResult });
            }
            // Preserve the original unavailable-reference error if refresh or
            // its read fails. A secondary provider error must not mask it.
            throw;
        }
    }

    private string Prefix => _initialDecorationDisappeared ? "" : _initial!.Prefix;
    private string Suffix => _initialDecorationDisappeared ? "" : _initial!.Suffix;
    private string ExpectedDocument(string text) => Prefix + text + Suffix;
    private bool MatchesOwnedSelection(TextTargetSnapshot snapshot, string owned) =>
        snapshot.IsConsistent && snapshot.Prefix == Prefix && snapshot.Selection == owned && snapshot.Suffix == Suffix;
    private void TraceSelectionObservation(string stage, string currentOwned, TextTargetSnapshot snapshot,
        TextTargetSnapshot beforeRequest, TimeSpan elapsed)
    {
        // Diagnose only snapshots already read for the existing safety checks.
        // No editor text is logged, and no extra provider call can delay failure.
        var generation = ReadFocusGenerationForDiagnostics();
        Trace(stage, new
        {
            requestOrdinal = _selectionRequestOrdinal,
            elapsedMilliseconds = elapsed.TotalMilliseconds,
            focusGenerationAtRequest = _selectionGenerationAtRequest,
            focusGenerationObserved = generation,
            focusGenerationDelta = _selectionGenerationAtRequest is { } initial && generation is { } current
                ? current - initial : (long?)null,
            expectedDocumentLength = Prefix.Length + currentOwned.Length + Suffix.Length,
            expectedPrefixLength = Prefix.Length,
            expectedSelectionLength = currentOwned.Length,
            expectedSuffixLength = Suffix.Length,
            documentLength = snapshot.Document.Length,
            prefixLength = snapshot.Prefix.Length,
            selectionLength = snapshot.Selection.Length,
            suffixLength = snapshot.Suffix.Length,
            snapshot.IsConsistent,
            documentMatches = snapshot.Document == ExpectedDocument(currentOwned),
            prefixMatches = snapshot.Prefix == Prefix,
            selectionMatches = snapshot.Selection == currentOwned,
            suffixMatches = snapshot.Suffix == Suffix,
            snapshotMatchesBeforeRequest = snapshot == beforeRequest
        });
    }

    private long? ReadFocusGenerationForDiagnostics()
    {
        try { return _readFocusGeneration?.Invoke(); }
        catch (Exception error)
        {
            // A diagnostic source must not change selection, paste, or failure
            // decisions. Exception messages may contain provider/editor text.
            Trace("target.selection_generation_failed", new { exceptionType = error.GetType().FullName, error.HResult });
            return null;
        }
    }

    private TextTargetUnavailableException Unavailable(string operation, ElementNotAvailableException error)
    {
        Trace("target.unavailable", new { operation,
            originalReferenceRefreshAttempted = _originalReferenceRefreshAttempted,
            selectionRequeryAttempted = _selectionPatternRequeryAttempted,
            selectionRequestOrdinal = _selectionRequestOrdinal,
            writeAwaitingAcknowledgement = _pendingText is not null,
            hasAcknowledgedWrite = _hasAcknowledgedWrite }, error);
        return new TextTargetUnavailableException(error);
    }

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
    private TextPattern? _pattern;
    private readonly IntPtr _foregroundWindow;
    private readonly bool _supportsReplacement;

    private AutomationTextSurface(AutomationElement? element, TextPattern? pattern, bool canPasteFallback = false,
        bool supportsReplacement = false, bool isUnsupportedInitialPane = false, bool isLegacyFocusContainer = false)
    {
        _element = element;
        _pattern = pattern;
        CanPasteFallback = canPasteFallback;
        _supportsReplacement = supportsReplacement;
        IsUnsupportedInitialPane = isUnsupportedInitialPane;
        IsLegacyFocusContainer = isLegacyFocusContainer;
        _foregroundWindow = GetForegroundWindow();
    }

    internal static AutomationTextSurface Capture()
        => Capture(AutomationElement.FocusedElement);

    internal static AutomationTextSurface Capture(AutomationElement? capturedIdentity)
    {
        var element = capturedIdentity;
        try
        {
            if (element is null
                || !TextProviderDiagnostics.Observe("capture.enabled", () => element.Current.IsEnabled)
                || TextProviderDiagnostics.Observe("capture.password", () => element.Current.IsPassword))
            {
                DiagnosticTrace.Write("target.surface_rejected", new { reason = "missing_disabled_or_password_field" });
                return new AutomationTextSurface(element, null);
            }
            bool? valueReadOnly = null;
            var rawValue = TextProviderDiagnostics.Observe("capture.value_pattern", () =>
                element.TryGetCurrentPattern(ValuePattern.Pattern, out var candidate) ? candidate : null);
            if (rawValue is ValuePattern value)
                valueReadOnly = TextProviderDiagnostics.Observe("capture.value_read_only", () => value.Current.IsReadOnly);
            TextPattern? pattern = null;
            object? textReadOnly = null;
            var rawText = TextProviderDiagnostics.Observe("capture.text_pattern", () =>
                element.TryGetCurrentPattern(TextPattern.Pattern, out var candidate) ? candidate : null);
            if (rawText is TextPattern text)
            {
                pattern = text;
                textReadOnly = TextProviderDiagnostics.Observe("capture.text_read_only", () =>
                    text.DocumentRange.GetAttributeValue(TextPattern.IsReadOnlyAttribute));
            }

            var enabled = TextProviderDiagnostics.Observe("capture.enabled", () => element.Current.IsEnabled);
            var password = TextProviderDiagnostics.Observe("capture.password", () => element.Current.IsPassword);
            var controlType = TextProviderDiagnostics.Observe("capture.control_type", () => element.Current.ControlType);
            var canPaste = AllowsFallback(enabled, password,
                controlType == ControlType.Edit, valueReadOnly, textReadOnly);
            DiagnosticTrace.Write("target.surface_capabilities", new { canPaste, controlType = controlType.ProgrammaticName,
                hasTextPattern = pattern is not null, valueReadOnly, textReadOnly = textReadOnly is bool readOnly ? (bool?)readOnly : null });
            if (canPaste && pattern is not null && textReadOnly is false
                && TextProviderDiagnostics.Observe("capture.selection_support", () => pattern.SupportedTextSelection) != SupportedTextSelection.None)
                return new AutomationTextSurface(element, pattern, canPasteFallback: true, supportsReplacement: true);
            return new AutomationTextSurface(element, pattern, canPaste,
                isUnsupportedInitialPane: enabled && !password && controlType == ControlType.Pane
                    && rawValue is null && pattern is null && !canPaste,
                isLegacyFocusContainer: AllowsLegacyFocusResolution(enabled, password, canPaste, controlType));
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

    internal static bool AllowsLegacyFocusResolution(bool enabled, bool password, bool canPaste, ControlType controlType)
        => enabled && !password && !canPaste
            && (controlType == ControlType.Window || controlType == ControlType.Document || controlType == ControlType.Pane);

    public bool IsFocused
    {
        get
        {
            if (_element is null) return false;
            // Probe the captured element even while its window is in the
            // background, so a closed editor does not look merely unfocused.
            var hasKeyboardFocus = TextProviderDiagnostics.Observe("focus.original_element", () => _element.Current.HasKeyboardFocus);
            return _foregroundWindow != IntPtr.Zero && GetForegroundWindow() == _foregroundWindow
                && hasKeyboardFocus && TextProviderDiagnostics.Observe("focus.compare_original", () =>
                    Automation.Compare(_element, AutomationElement.FocusedElement));
        }
    }
    public bool TryRefreshOriginalReference()
    {
        // Capture queries the SAME AutomationElement and refreshes its pattern.
        // A permanently unavailable element cannot pass these current-property
        // reads; no new element is adopted from focus, position, text, or IDs.
        if (_element is null) return RejectRefresh("missing_original_element");
        if (!IsFocused) return RejectRefresh("original_element_unfocused");
        if (!_supportsReplacement) return RejectRefresh("replacement_unsupported");
        var refreshed = Capture(_element);
        if (!refreshed.SupportsReplacement || !refreshed.CanPasteFallback)
            return RejectRefresh("original_element_capabilities_unavailable");
        if (!refreshed.IsFocused) return RejectRefresh("original_element_focus_changed");
        _pattern = refreshed._pattern;
        DiagnosticTrace.Write("target.provider_refresh_completed");
        return true;
    }

    public TextPatternRequeryResult RequerySelectionPattern() => TryRefreshOriginalReference()
        ? TextPatternRequeryResult.Refreshed : TextPatternRequeryResult.Rejected;

    private static bool RejectRefresh(string reason)
    {
        DiagnosticTrace.Write("target.provider_refresh_rejected", new { reason });
        return false;
    }

    public bool SupportsReplacement => _supportsReplacement;
    internal bool IsUnsupportedInitialPane { get; }
    internal bool IsLegacyFocusContainer { get; }
    public bool CanPasteFallback { get; }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    public TextTargetSnapshot Read()
    {
        if (_pattern is null) throw new NotSupportedException("This editor exposes no readable text selection.");
        var document = TextProviderDiagnostics.Observe("snapshot.document_range", () => _pattern.DocumentRange);
        var selection = TextProviderDiagnostics.Observe("snapshot.selection", _pattern.GetSelection);
        if (selection.Length != 1) throw new NotSupportedException("Multiple selections are not supported.");
        var prefix = TextProviderDiagnostics.Observe("snapshot.prefix_range", () =>
        {
            var range = document.Clone();
            range.MoveEndpointByRange(TextPatternRangeEndpoint.End, selection[0], TextPatternRangeEndpoint.Start);
            return range;
        });
        var suffix = TextProviderDiagnostics.Observe("snapshot.suffix_range", () =>
        {
            var range = document.Clone();
            range.MoveEndpointByRange(TextPatternRangeEndpoint.Start, selection[0], TextPatternRangeEndpoint.End);
            return range;
        });
        return new TextTargetSnapshot(
            TextProviderDiagnostics.Observe("snapshot.document_text", () => document.GetText(-1)),
            TextProviderDiagnostics.Observe("snapshot.prefix_text", () => prefix.GetText(-1)),
            TextProviderDiagnostics.Observe("snapshot.selected_text", () => selection[0].GetText(-1)),
            TextProviderDiagnostics.Observe("snapshot.suffix_text", () => suffix.GetText(-1)));
    }

    public bool Select(string prefix, string ownedText, string suffix)
        => TextProviderDiagnostics.Observe("selection.request", () =>
            TextRangeSelector.Select(new AutomationTextRange(_pattern!.DocumentRange), prefix, ownedText, suffix, () => IsFocused));

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
