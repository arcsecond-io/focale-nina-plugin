using System.Windows.Controls;

namespace Focale.NINA.Plugin.Ui;

public partial class FocaleOptionsView : UserControl {
    public FocaleOptionsView() {
        InitializeComponent();
    }

    /// <summary>
    /// PasswordBox.Password is intentionally not bindable in WPF for security.
    /// Push the typed value into the VM via PasswordEntry; the VM clears it after Sign-in
    /// so the plaintext is never held longer than necessary.
    /// </summary>
    private void PasswordBoxControl_PasswordChanged(object sender, System.Windows.RoutedEventArgs e) {
        if (DataContext is FocaleOptionsViewModel vm && sender is PasswordBox box) {
            vm.PasswordEntry = box.Password;
        }
    }
}
