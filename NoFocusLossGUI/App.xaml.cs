using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace NoFocusLossGUI
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            DispatcherUnhandledException += (s, args) =>
            {
                args.Handled = true;
                MessageBox.Show("Mithya encountered an error and could not open.\n\n" + args.Exception.Message,
                    "Mithya", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
            };
            base.OnStartup(e);
        }
    }
}
