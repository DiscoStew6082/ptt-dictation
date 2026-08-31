using System.Runtime.InteropServices;

namespace PttDictation.App;

internal static class DarkTheme
{
    public static readonly Color Background = Color.FromArgb(24, 26, 30);
    public static readonly Color Surface = Color.FromArgb(34, 37, 43);
    public static readonly Color SurfaceRaised = Color.FromArgb(44, 48, 56);
    public static readonly Color Text = Color.FromArgb(238, 241, 245);
    public static readonly Color MutedText = Color.FromArgb(164, 171, 181);
    public static readonly Color Accent = Color.FromArgb(91, 141, 239);
    public static readonly Color Danger = Color.FromArgb(221, 86, 86);
    public static readonly Color Border = Color.FromArgb(62, 67, 77);

    public static Font HeaderFont => new("Segoe UI", 14F, FontStyle.Bold, GraphicsUnit.Point);
    public static Font BodyFont => new("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

    public static void Apply(Form form)
    {
        form.BackColor = Background;
        form.ForeColor = Text;
        form.Font = BodyFont;
        form.StartPosition = FormStartPosition.CenterScreen;
        ApplyWindowChrome(form);
    }

    public static void Apply(Control control)
    {
        control.ForeColor = Text;
        control.BackColor = control is TextBoxBase or ComboBox or ListBox ? SurfaceRaised : Background;
        control.Font = BodyFont;

        foreach (Control child in control.Controls)
        {
            Apply(child);
        }
    }

    public static void ApplyNativeDarkTheme(Control control)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (control.IsHandleCreated)
        {
            NativeControlTheme.Apply(control.Handle);
            return;
        }

        control.HandleCreated += (_, _) => NativeControlTheme.Apply(control.Handle);
    }

    public static Button Button(string text)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = false,
            Height = 34,
            FlatStyle = FlatStyle.Flat,
            BackColor = SurfaceRaised,
            ForeColor = Text,
            UseVisualStyleBackColor = false
        };
        button.FlatAppearance.BorderColor = Border;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(54, 59, 69);
        button.FlatAppearance.MouseDownBackColor = Color.FromArgb(63, 69, 81);
        return button;
    }

    public static Label Label(string text)
    {
        return new Label
        {
            Text = text,
            AutoSize = true,
            ForeColor = MutedText,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 6, 0, 3)
        };
    }

    public static Label HelpText(string text)
    {
        return new Label
        {
            Text = text,
            AutoSize = false,
            Height = 38,
            Dock = DockStyle.Top,
            ForeColor = MutedText,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 8)
        };
    }

    private static void ApplyWindowChrome(Form form)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (form.IsHandleCreated)
        {
            DarkWindowChrome.Apply(form.Handle);
            return;
        }

        form.HandleCreated += (_, _) => DarkWindowChrome.Apply(form.Handle);
    }
}

internal static class NativeControlTheme
{
    public static void Apply(IntPtr handle)
    {
        if (handle != IntPtr.Zero)
        {
            SetWindowTheme(handle, "DarkMode_Explorer", null);
        }
    }

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? pszSubAppName, string? pszSubIdList);
}

internal static class DarkWindowChrome
{
    private const int Succeeded = 0;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    public static void Apply(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var enabled = 1;
        if (DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int)) != Succeeded)
        {
            DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeBefore20H1, ref enabled, sizeof(int));
        }

        var caption = ColorTranslator.ToWin32(DarkTheme.Background);
        var border = ColorTranslator.ToWin32(DarkTheme.Border);
        var text = ColorTranslator.ToWin32(DarkTheme.Text);
        DwmSetWindowAttribute(handle, DwmwaCaptionColor, ref caption, sizeof(int));
        DwmSetWindowAttribute(handle, DwmwaBorderColor, ref border, sizeof(int));
        DwmSetWindowAttribute(handle, DwmwaTextColor, ref text, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);
}
