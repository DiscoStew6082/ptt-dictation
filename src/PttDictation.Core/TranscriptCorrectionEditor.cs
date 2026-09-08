namespace PttDictation.Core;

public sealed class TranscriptCorrectionEditor(IEnumerable<TranscriptCorrection> corrections)
{
    private readonly List<TranscriptCorrection> _rules = [.. corrections];

    public IReadOnlyList<TranscriptCorrection> Rules => _rules.AsReadOnly();

    public TranscriptCorrection? SelectedCorrection { get; private set; }

    public TranscriptCorrection Draft { get; private set; } = new("", "");

    public bool CanCommit => !string.IsNullOrWhiteSpace(Draft.HeardAs)
        && !string.IsNullOrWhiteSpace(Draft.ReplaceWith);

    public void Select(TranscriptCorrection correction)
    {
        if (!_rules.Contains(correction))
        {
            throw new ArgumentException("Select a correction from the current rules.", nameof(correction));
        }

        SelectedCorrection = correction;
        Draft = correction;
    }

    public void UpdateDraft(string heardAs, string replaceWith)
    {
        Draft = new TranscriptCorrection(heardAs, replaceWith);
    }

    public void StartNewDraft()
    {
        SelectedCorrection = null;
        Draft = new TranscriptCorrection("", "");
    }

    public string Preview(string input)
    {
        return TranscriptCorrectionDictionary.Apply(input, RulesIncludingDraft());
    }

    public CorrectionEditResult CommitDraft()
    {
        if (!CanCommit)
        {
            return CorrectionEditResult.Incomplete;
        }

        var updated = SelectedCorrection is not null || _rules.Any(correction =>
            string.Equals(correction.HeardAs, Draft.HeardAs.Trim(), StringComparison.OrdinalIgnoreCase));
        var rules = RulesIncludingDraft().ToArray();
        _rules.Clear();
        _rules.AddRange(rules);
        StartNewDraft();
        return updated ? CorrectionEditResult.Updated : CorrectionEditResult.Added;
    }

    public CorrectionEditResult PrepareSave()
    {
        if (string.IsNullOrWhiteSpace(Draft.HeardAs) != string.IsNullOrWhiteSpace(Draft.ReplaceWith))
        {
            return CorrectionEditResult.Incomplete;
        }

        return HasChangedDraft ? CommitDraft() : CorrectionEditResult.Unchanged;
    }

    public bool RemoveSelected()
    {
        if (SelectedCorrection is null)
        {
            return false;
        }

        _rules.Remove(SelectedCorrection);
        StartNewDraft();
        return true;
    }

    private bool HasChangedDraft => CanCommit
        && (SelectedCorrection is null
            || Draft.HeardAs.Trim() != SelectedCorrection.HeardAs
            || Draft.ReplaceWith.Trim() != SelectedCorrection.ReplaceWith);

    private IReadOnlyList<TranscriptCorrection> RulesIncludingDraft()
    {
        // Selecting an unchanged rule must not reorder matching rules in the preview.
        if (!HasChangedDraft)
        {
            return _rules;
        }

        var rules = _rules.ToList();
        if (SelectedCorrection is not null)
        {
            rules.Remove(SelectedCorrection);
        }

        var draft = new TranscriptCorrection(Draft.HeardAs.Trim(), Draft.ReplaceWith.Trim());
        var existing = rules.FindIndex(correction =>
            string.Equals(correction.HeardAs, draft.HeardAs, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
        {
            rules[existing] = draft;
        }
        else
        {
            rules.Add(draft);
        }

        return rules;
    }
}

public enum CorrectionEditResult
{
    Unchanged,
    Incomplete,
    Added,
    Updated
}
