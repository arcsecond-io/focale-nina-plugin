using System;
using System.ComponentModel.Composition;
using System.Windows;

namespace Focale.NINA.Plugin.Ui;

/// <summary>
/// Exposes the plugin's WPF resources (DataTemplate "Focale_Options") to NINA's PluginLoader,
/// which composes [ImportMany] ResourceDictionary parts into Application.Current.Resources.
/// </summary>
[Export(typeof(ResourceDictionary))]
public sealed class PluginResources : ResourceDictionary {
    public PluginResources() {
        Source = new Uri("/Focale.NINA.Plugin;component/Ui/PluginResources.xaml", UriKind.Relative);
    }
}
