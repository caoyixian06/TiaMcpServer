using System;
using System.Threading;
using System.Windows.Forms;

namespace TiaMcpServer;

internal static class DashboardHost
{
    private static DashboardForm? _form;
    private static Thread? _thread;

    public static void Start(McpServer server, ServerRuntimeState state)
    {
        if (_thread != null) return;
        state.HasInteractiveUi = true;
        _thread = new Thread(() =>
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var userSettings = BeginnerUserSettings.Load();
            state.SetExecutionMode(userSettings.ResolveExecutionMode());
            if (!userSettings.FirstRunCompleted)
            {
                using var wizard = new FirstRunWizardForm(userSettings);
                wizard.ShowDialog();
            }
            _form = new DashboardForm(server, state);
            Application.Run(_form);
        });
        _thread.Name = "TIA MCP Dashboard";
        _thread.IsBackground = true;
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public static void Stop()
    {
        try
        {
            var form = _form;
            if (form != null && !form.IsDisposed)
            {
                form.BeginInvoke(new Action(() => form.Close()));
            }
        }
        catch { }
    }
}
