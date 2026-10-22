using System;
using System.Windows.Forms;
using Funbit.Ets.Telemetry.Server.Ui;

namespace Funbit.Ets.Telemetry.Server.Helpers
{
    public static class ExceptionExtensions
    {
        public static void ShowAsMessageBox(this Exception exception, IWin32Window owner, string caption,
                                            MessageBoxIcon icon = MessageBoxIcon.Error)
        {
            DarkMessageBox.Show(owner, exception.Message, caption, MessageBoxButtons.OK, icon);
        }
    }
}