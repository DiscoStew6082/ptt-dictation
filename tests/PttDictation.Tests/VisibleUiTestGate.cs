using System.Diagnostics.CodeAnalysis;

namespace PttDictation.Tests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class VisibleUiAttribute : TestCategoryBaseAttribute
{
    public override IList<string> TestCategories => ["VisibleUi"];
}

internal static class VisibleUiTestGate
{
    internal const string EnvironmentVariable = "PTT_RUN_VISIBLE_UI_TESTS";

    internal static bool IsEnabled(string? value) => value == "1";

    internal static void RequireOptIn() => RequireOptIn(Environment.GetEnvironmentVariable(EnvironmentVariable));

    internal static void RequireOptIn(string? value)
    {
        if (!IsEnabled(value))
            Assert.Inconclusive($"Visible UI test skipped. Set {EnvironmentVariable}=1 only on a desktop explicitly approved for visible test windows.");
    }

    internal static bool ShouldCaptureScreenshot([NotNullWhen(true)] string? path)
        => ShouldCaptureScreenshot(path, Environment.GetEnvironmentVariable(EnvironmentVariable));

    internal static bool ShouldCaptureScreenshot([NotNullWhen(true)] string? path, string? optIn)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        RequireOptIn(optIn);
        return true;
    }
}
