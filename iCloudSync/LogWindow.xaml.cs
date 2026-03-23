using System;
using System.Windows; // <--- THIS IS THE MISSING PIECE
using System.Windows.Controls;

namespace ICloudSync
{
    /// <summary>
    /// Interaction logic for LogWindow.xaml
    /// </summary>
    public partial class LogWindow : Window
    {
        public LogWindow()
        {
            InitializeComponent();
        }

        public void AppendLog(string message)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => AppendLog(message)));
                return;
            }

            LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
            LogBox.ScrollToEnd();
            
            if (LogBox.LineCount > 1000)
            {
                LogBox.Text = LogBox.Text.Substring(LogBox.GetCharacterIndexFromLineIndex(100));
            }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            e.Cancel = true;
            this.Hide();
        }
    }
}