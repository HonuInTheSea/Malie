using System.Globalization;
using System.Windows;
using Malie.Models;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfSlider = System.Windows.Controls.Slider;
using WpfKey = System.Windows.Input.Key;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfKeyboard = System.Windows.Input.Keyboard;

namespace Malie.Windows;

public partial class GlbOrientationWindow : Window
{
    public event EventHandler<GlbOrientationSettings>? OrientationApplied;

    private bool _isInitializing = true;

    public GlbOrientationWindow(GlbOrientationSettings initialOrientation)
    {
        InitializeComponent();
        SetOrientation(initialOrientation);
    }

    public void SetOrientation(GlbOrientationSettings orientation)
    {
        var normalized = orientation.Normalize();
        _isInitializing = true;
        try
        {
            RotXSlider.Value = normalized.RotationXDegrees;
            RotYSlider.Value = normalized.RotationYDegrees;
            RotZSlider.Value = normalized.RotationZDegrees;
            ScaleSlider.Value = normalized.Scale;
            OffsetXSlider.Value = normalized.OffsetX;
            OffsetYSlider.Value = normalized.OffsetY;
            OffsetZSlider.Value = normalized.OffsetZ;
        }
        finally
        {
            _isInitializing = false;
            UpdateValueInputs();
        }
    }

    private void OnSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isInitializing)
        {
            return;
        }

        UpdateValueInputs();
        OrientationApplied?.Invoke(this, BuildOrientationFromSliders());
    }

    private void OnValueTextBoxCommit(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || sender is not WpfTextBox textBox)
        {
            return;
        }

        CommitTextBoxValue(textBox);
    }

    private void OnValueTextBoxKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key != WpfKey.Enter || sender is not WpfTextBox textBox)
        {
            return;
        }

        CommitTextBoxValue(textBox);
        WpfKeyboard.ClearFocus();
        e.Handled = true;
    }

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        SetOrientation(GlbOrientationSettings.Default);
        OrientationApplied?.Invoke(this, BuildOrientationFromSliders());
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void UpdateValueInputs()
    {
        if (RotXValueTextBox is null ||
            RotYValueTextBox is null ||
            RotZValueTextBox is null ||
            ScaleValueTextBox is null ||
            OffsetXValueTextBox is null ||
            OffsetYValueTextBox is null ||
            OffsetZValueTextBox is null ||
            RotXSlider is null ||
            RotYSlider is null ||
            RotZSlider is null ||
            ScaleSlider is null ||
            OffsetXSlider is null ||
            OffsetYSlider is null ||
            OffsetZSlider is null)
        {
            return;
        }

        UpdateTextBoxValue(RotXValueTextBox, RotXSlider.Value, "0.##");
        UpdateTextBoxValue(RotYValueTextBox, RotYSlider.Value, "0.##");
        UpdateTextBoxValue(RotZValueTextBox, RotZSlider.Value, "0.##");
        UpdateTextBoxValue(ScaleValueTextBox, ScaleSlider.Value, "0.###");
        UpdateTextBoxValue(OffsetXValueTextBox, OffsetXSlider.Value, "0.###");
        UpdateTextBoxValue(OffsetYValueTextBox, OffsetYSlider.Value, "0.###");
        UpdateTextBoxValue(OffsetZValueTextBox, OffsetZSlider.Value, "0.###");
    }

    private static void UpdateTextBoxValue(WpfTextBox textBox, double value, string format)
    {
        if (textBox.IsKeyboardFocusWithin)
        {
            return;
        }

        textBox.Text = value.ToString(format, CultureInfo.InvariantCulture);
    }

    private void CommitTextBoxValue(WpfTextBox textBox)
    {
        var slider = ResolveSliderForTextBox(textBox);
        if (slider is null)
        {
            return;
        }

        if (!TryParseDouble(textBox.Text, out var parsed))
        {
            UpdateValueInputs();
            return;
        }

        var clamped = Math.Clamp(parsed, slider.Minimum, slider.Maximum);
        if (Math.Abs(slider.Value - clamped) > 0.000001d)
        {
            slider.Value = clamped;
            return;
        }

        UpdateValueInputs();
        OrientationApplied?.Invoke(this, BuildOrientationFromSliders());
    }

    private WpfSlider? ResolveSliderForTextBox(WpfTextBox textBox)
    {
        if (ReferenceEquals(textBox, RotXValueTextBox))
        {
            return RotXSlider;
        }

        if (ReferenceEquals(textBox, RotYValueTextBox))
        {
            return RotYSlider;
        }

        if (ReferenceEquals(textBox, RotZValueTextBox))
        {
            return RotZSlider;
        }

        if (ReferenceEquals(textBox, ScaleValueTextBox))
        {
            return ScaleSlider;
        }

        if (ReferenceEquals(textBox, OffsetXValueTextBox))
        {
            return OffsetXSlider;
        }

        if (ReferenceEquals(textBox, OffsetYValueTextBox))
        {
            return OffsetYSlider;
        }

        if (ReferenceEquals(textBox, OffsetZValueTextBox))
        {
            return OffsetZSlider;
        }

        return null;
    }

    private static bool TryParseDouble(string? text, out double value)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
        {
            return true;
        }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private GlbOrientationSettings BuildOrientationFromSliders()
    {
        return new GlbOrientationSettings(
            RotationXDegrees: RotXSlider.Value,
            RotationYDegrees: RotYSlider.Value,
            RotationZDegrees: RotZSlider.Value,
            Scale: ScaleSlider.Value,
            OffsetX: OffsetXSlider.Value,
            OffsetY: OffsetYSlider.Value,
            OffsetZ: OffsetZSlider.Value).Normalize();
    }
}
