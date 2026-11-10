using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace Focale.NINA.Plugin.Ui;

public partial class FocaleOptionsView : UserControl {
    public FocaleOptionsView() {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>
    /// PasswordBox.Password is intentionally not bindable in WPF for security.
    /// Push the typed value into the VM via PasswordEntry; the VM clears it after a sign-in attempt
    /// so the plaintext is never held longer than necessary.
    /// </summary>
    private void PasswordBoxControl_PasswordChanged(object sender, RoutedEventArgs e) {
        if (DataContext is FocaleOptionsViewModel vm && sender is PasswordBox box) {
            vm.PasswordEntry = box.Password;
        }
    }

    /// <summary>When the VM drops the password, empty the box too, so what is shown is what will be sent.</summary>
    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) {
        if (e.OldValue is INotifyPropertyChanged old) old.PropertyChanged -= OnViewModelPropertyChanged;
        if (e.NewValue is INotifyPropertyChanged now) now.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e) {
        if (e.PropertyName != nameof(FocaleOptionsViewModel.PasswordEntry)) return;
        if (sender is not FocaleOptionsViewModel vm || vm.PasswordEntry.Length > 0) return;
        Dispatcher.BeginInvoke(() => {
            if (PasswordBoxControl.Password.Length > 0) PasswordBoxControl.Clear();
        });
    }
}
