namespace RatioMaster.Behaviors;

using System.Globalization;
using Avalonia;
using Avalonia.Controls;

public enum NumericRule
{
    None,
    Int,
    Double,
}

/// <summary>
/// Attach <c>behaviors:FieldValidation.Rule="Int|Double"</c> to a TextBox to toggle an
/// "invalid" style class (red border) when the text isn't a valid non-negative number.
/// Empty is treated as valid (not yet filled). AOT-safe.
/// </summary>
public static class FieldValidation
{
    public static readonly AttachedProperty<NumericRule> RuleProperty =
        AvaloniaProperty.RegisterAttached<TextBox, NumericRule>("Rule", typeof(FieldValidation));

    static FieldValidation()
    {
        RuleProperty.Changed.AddClassHandler<TextBox>((tb, _) =>
        {
            tb.TextChanged -= OnTextChanged;
            if (tb.GetValue(RuleProperty) != NumericRule.None)
            {
                tb.TextChanged += OnTextChanged;
            }

            Validate(tb);
        });
    }

    public static void SetRule(TextBox element, NumericRule value) => element.SetValue(RuleProperty, value);

    public static NumericRule GetRule(TextBox element) => element.GetValue(RuleProperty);

    private static void OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (sender is TextBox tb)
        {
            Validate(tb);
        }
    }

    private static void Validate(TextBox tb)
    {
        NumericRule rule = tb.GetValue(RuleProperty);
        string text = tb.Text ?? string.Empty;
        bool valid = true;

        if (rule != NumericRule.None && !string.IsNullOrWhiteSpace(text))
        {
            valid = rule switch
            {
                NumericRule.Int => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) && i >= 0,
                NumericRule.Double => double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && d >= 0,
                _ => true,
            };
        }

        tb.Classes.Set("invalid", !valid);
    }
}
