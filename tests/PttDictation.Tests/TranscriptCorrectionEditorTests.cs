using PttDictation.Core;

namespace PttDictation.Tests;

[TestClass]
public sealed class TranscriptCorrectionEditorTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RenamingSelectedRuleOntoExistingPhrasePreviewsExactlyWhatIsCommitted(bool save)
    {
        var editor = new TranscriptCorrectionEditor([
            new("quinn", "Qwen"),
            new("steward", "Stewart"),
            new("kuda", "CUDA")]);
        editor.Select(editor.Rules[0]);
        editor.UpdateDraft(" STEWARD ", " Stu ");

        const string input = "quinn steward kuda";
        var preview = editor.Preview(input);

        Assert.AreEqual("quinn Stu CUDA", preview);
        Assert.AreEqual(3, editor.Rules.Count, "Preview must not commit the draft.");
        var result = save ? editor.PrepareSave() : editor.CommitDraft();

        Assert.AreEqual(CorrectionEditResult.Updated, result);
        CollectionAssert.AreEqual(
            new[] { new TranscriptCorrection("STEWARD", "Stu"), new TranscriptCorrection("kuda", "CUDA") },
            editor.Rules.ToArray());
        Assert.AreEqual(preview, TranscriptCorrectionDictionary.Apply(input, editor.Rules));
        Assert.IsNull(editor.SelectedCorrection);
        Assert.AreEqual(new TranscriptCorrection("", ""), editor.Draft);
    }

    [TestMethod]
    public void AddingDuplicatePhraseReplacesCaseInsensitivelyWithoutChangingInputRules()
    {
        var original = new List<TranscriptCorrection> { new("quinn", "Qwen") };
        var editor = new TranscriptCorrectionEditor(original);
        editor.UpdateDraft(" QUINN ", " Qwen 3 ");

        Assert.AreEqual("Qwen 3", editor.Preview("quinn"));
        Assert.AreEqual(CorrectionEditResult.Updated, editor.CommitDraft());
        Assert.AreEqual(1, editor.Rules.Count);
        Assert.AreEqual("Qwen 3", editor.Rules[0].ReplaceWith);
        Assert.AreEqual("Qwen", original[0].ReplaceWith);
    }

    [TestMethod]
    public void SaveIncludesCompleteNewDraftAndCanBeRepeated()
    {
        var editor = new TranscriptCorrectionEditor([]);
        editor.UpdateDraft(" c sharp ", " C# ");

        var preview = editor.Preview("I use c sharp.");
        Assert.AreEqual(CorrectionEditResult.Added, editor.PrepareSave());
        Assert.AreEqual(preview, TranscriptCorrectionDictionary.Apply("I use c sharp.", editor.Rules));
        Assert.AreEqual("I use C#.", preview);
        Assert.AreEqual(CorrectionEditResult.Unchanged, editor.PrepareSave());
        Assert.AreEqual(1, editor.Rules.Count);
    }

    [TestMethod]
    [DataRow("steward", " ")]
    [DataRow(" ", "Stewart")]
    public void IncompleteDraftBlocksSaveWithoutLosingSelectedRuleOrDraft(string heardAs, string replaceWith)
    {
        var editor = new TranscriptCorrectionEditor([new("quinn", "Qwen")]);
        var selected = editor.Rules[0];
        editor.Select(selected);
        editor.UpdateDraft(heardAs, replaceWith);

        Assert.IsFalse(editor.CanCommit);
        Assert.AreEqual("Qwen", editor.Preview("quinn"));
        Assert.AreEqual(CorrectionEditResult.Incomplete, editor.PrepareSave());
        Assert.AreEqual(CorrectionEditResult.Incomplete, editor.CommitDraft());
        CollectionAssert.AreEqual(new[] { selected }, editor.Rules.ToArray());
        Assert.AreEqual(selected, editor.SelectedCorrection);
        Assert.AreEqual(new TranscriptCorrection(heardAs, replaceWith), editor.Draft);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void UnchangedSelectionPreservesRuleOrderAndPreview(bool save)
    {
        // Equal-length rules apply in list order, so reordering changes the result.
        var rules = new[] { new TranscriptCorrection("cat", "dog"), new TranscriptCorrection("dog", "fox") };
        var editor = new TranscriptCorrectionEditor(rules);
        editor.Select(editor.Rules[0]);

        Assert.AreEqual("fox", editor.Preview("cat"));
        var result = save ? editor.PrepareSave() : editor.CommitDraft();

        Assert.AreEqual(save ? CorrectionEditResult.Unchanged : CorrectionEditResult.Updated, result);
        CollectionAssert.AreEqual(rules, editor.Rules.ToArray());
        Assert.AreEqual("fox", editor.Preview("cat"));
    }

    [TestMethod]
    public void NewDraftDiscardsSelectedEditsWithoutRemovingOriginalRule()
    {
        var editor = new TranscriptCorrectionEditor([new("quinn", "Qwen")]);
        editor.Select(editor.Rules[0]);
        editor.UpdateDraft("quinn", "Qwen 3");
        Assert.AreEqual("Qwen 3", editor.Preview("quinn"));

        editor.StartNewDraft();

        Assert.AreEqual("Qwen", editor.Preview("quinn"));
        Assert.AreEqual(CorrectionEditResult.Unchanged, editor.PrepareSave());
        Assert.IsFalse(editor.CanCommit);
        Assert.IsNull(editor.SelectedCorrection);
    }

    [TestMethod]
    public void RemovingSelectedRuleDiscardsItsDraftAndPreservesOtherRules()
    {
        var editor = new TranscriptCorrectionEditor([new("quinn", "Qwen"), new("kuda", "CUDA")]);
        Assert.IsFalse(editor.RemoveSelected());
        editor.Select(editor.Rules[0]);
        editor.UpdateDraft("kuda", "Changed");

        Assert.IsTrue(editor.RemoveSelected());
        Assert.AreEqual("quinn CUDA", editor.Preview("quinn kuda"));
        Assert.AreEqual(CorrectionEditResult.Unchanged, editor.PrepareSave());
        CollectionAssert.AreEqual(new[] { new TranscriptCorrection("kuda", "CUDA") }, editor.Rules.ToArray());
    }

    [TestMethod]
    public void ReopeningSavedRulesDiscardsUnpersistedEdits()
    {
        var saved = new[] { new TranscriptCorrection("quinn", "Qwen") };
        var editor = new TranscriptCorrectionEditor(saved);
        editor.UpdateDraft("kuda", "CUDA");
        editor.CommitDraft();
        editor.Select(editor.Rules[0]);
        editor.RemoveSelected();
        editor.UpdateDraft("steward", "Stewart");

        var reopened = new TranscriptCorrectionEditor(saved);

        Assert.AreEqual("Qwen kuda steward", reopened.Preview("quinn kuda steward"));
        Assert.AreEqual(new TranscriptCorrection("", ""), reopened.Draft);
        CollectionAssert.AreEqual(saved, reopened.Rules.ToArray());
    }
}
