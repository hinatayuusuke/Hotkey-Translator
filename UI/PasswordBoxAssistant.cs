using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Hotkey_Translator;

internal static class PasswordBoxAssistant
{
    static PasswordBoxAssistant()
    {
        // WHY: BoundPassword initial value is often empty string, same as DP default.
        // In that case OnBoundPasswordChanged may not fire on startup, so PasswordChanged
        // is never subscribed and user input cannot flow back to ViewModel.
        EventManager.RegisterClassHandler(
            typeof(PasswordBox),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnPasswordBoxLoaded),
            handledEventsToo: true);
    }

    public static readonly DependencyProperty BoundPasswordProperty =
        DependencyProperty.RegisterAttached(
            "BoundPassword",
            typeof(string),
            typeof(PasswordBoxAssistant),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnBoundPasswordChanged));

    private static readonly DependencyProperty IsUpdatingProperty =
        DependencyProperty.RegisterAttached(
            "IsUpdating",
            typeof(bool),
            typeof(PasswordBoxAssistant),
            new PropertyMetadata(false));

    public static string GetBoundPassword(DependencyObject obj)
    {
        return (string)obj.GetValue(BoundPasswordProperty);
    }

    public static void SetBoundPassword(DependencyObject obj, string value)
    {
        obj.SetValue(BoundPasswordProperty, value);
    }

    private static bool GetIsUpdating(DependencyObject obj)
    {
        return (bool)obj.GetValue(IsUpdatingProperty);
    }

    private static void SetIsUpdating(DependencyObject obj, bool value)
    {
        obj.SetValue(IsUpdatingProperty, value);
    }

    private static void OnBoundPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox passwordBox)
        {
            return;
        }

        passwordBox.PasswordChanged -= PasswordBoxOnPasswordChanged;
        if (!GetIsUpdating(passwordBox))
        {
            // SECURITY: This bridge keeps plaintext password only long enough to synchronize view and ViewModel.
            passwordBox.Password = e.NewValue as string ?? string.Empty;
        }

        passwordBox.PasswordChanged += PasswordBoxOnPasswordChanged;
    }

    private static void OnPasswordBoxLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not PasswordBox passwordBox)
        {
            return;
        }

        var binding = BindingOperations.GetBindingExpression(passwordBox, BoundPasswordProperty);
        if (binding == null)
        {
            return;
        }

        passwordBox.PasswordChanged -= PasswordBoxOnPasswordChanged;
        passwordBox.PasswordChanged += PasswordBoxOnPasswordChanged;

        if (GetIsUpdating(passwordBox))
        {
            return;
        }

        var boundPassword = GetBoundPassword(passwordBox) ?? string.Empty;
        if (string.Equals(passwordBox.Password, boundPassword, System.StringComparison.Ordinal))
        {
            return;
        }

        SetIsUpdating(passwordBox, true);
        passwordBox.Password = boundPassword;
        SetIsUpdating(passwordBox, false);
    }

    private static void PasswordBoxOnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not PasswordBox passwordBox)
        {
            return;
        }

        SetIsUpdating(passwordBox, true);
        SetBoundPassword(passwordBox, passwordBox.Password);
        SetIsUpdating(passwordBox, false);
    }
}
