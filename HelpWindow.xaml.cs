using System.Windows;

namespace ELRSWifiJoystick
{
    public partial class HelpWindow : Window
    {
        public HelpWindow()
        {
            InitializeComponent();
            SourceInitialized += (s, e) => DarkTitleBar.Enable(this);
        }

        private void Ok_Click(object sender, RoutedEventArgs e) => Close();
    }
}
