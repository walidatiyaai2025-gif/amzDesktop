using System.Drawing;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

sealed class MainForm : Form
{
    readonly ComboBox backendCombo = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 390
    };

    readonly Button restartButton = new()
    {
        Text = "Start / Restart",
        AutoSize = true
    };

    readonly Button copyButton = new()
    {
        Text = "Copy address",
        AutoSize = true
    };

    readonly Label addressLabel = new()
    {
        AutoSize = true,
        Font = new Font("Segoe UI", 13, FontStyle.Bold)
    };

    readonly Label sessionLabel = new()
    {
        AutoSize = true,
        Font = new Font("Consolas", 18, FontStyle.Bold)
    };

    readonly Label backendLabel = new() { AutoSize = true };
    readonly Label screenLabel = new() { AutoSize = true };
    readonly Label audioLabel = new() { AutoSize = true };
    readonly Label diagLabel = new()
    {
        AutoSize = true,
        MaximumSize = new Size(700, 0)
    };

    RemoteServer? server;

    public MainForm()
    {
        Text = "Remote Screen Desktop V2.4";
        Width = 820;
        Height = 570;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;

        backendCombo.Items.AddRange(new object[]
        {
            "Auto (DXGI → BitBlt CAPTUREBLT → GDI)",
            "DXGI Desktop Duplication",
            "Windows Graphics Capture (system picker)",
            "GDI BitBlt (SRCCOPY)",
            "GDI BitBlt (SRCCOPY + CAPTUREBLT)",
            "GDI CopyFromScreen",
            "PrintWindow (current foreground window)"
        });

        backendCombo.SelectedIndex = 0;

        restartButton.Click += (_, __) => StartServer();

        copyButton.Click += (_, __) =>
        {
            if (!string.IsNullOrWhiteSpace(addressLabel.Text))
                Clipboard.SetText(addressLabel.Text);
        };

        var title = new Label
        {
            Text = "Remote Screen Desktop V2.4",
            AutoSize = true,
            Font = new Font("Segoe UI", 20, FontStyle.Bold)
        };

        var note = new Label
        {
            Text = "WGC opens the Windows system picker: select the full display. PrintWindow captures whichever window is currently foreground.",
            AutoSize = true,
            MaximumSize = new Size(700, 0)
        };

        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(28)
        };

        flow.Controls.Add(title);
        flow.Controls.Add(new Label
        {
            Text = "Capture backend",
            AutoSize = true,
            Font = new Font("Segoe UI", 10, FontStyle.Bold)
        });
        flow.Controls.Add(backendCombo);
        flow.Controls.Add(note);
        flow.Controls.Add(restartButton);
        flow.Controls.Add(new Label
        {
            Text = "PC address",
            AutoSize = true,
            Font = new Font("Segoe UI", 10, FontStyle.Bold)
        });
        flow.Controls.Add(addressLabel);
        flow.Controls.Add(copyButton);
        flow.Controls.Add(new Label
        {
            Text = "Session code",
            AutoSize = true,
            Font = new Font("Segoe UI", 10, FontStyle.Bold)
        });
        flow.Controls.Add(sessionLabel);
        flow.Controls.Add(backendLabel);
        flow.Controls.Add(screenLabel);
        flow.Controls.Add(audioLabel);
        flow.Controls.Add(diagLabel);

        Controls.Add(flow);

        Shown += (_, __) => StartServer();
        FormClosed += (_, __) => server?.Dispose();
    }

    void StartServer()
    {
        restartButton.Enabled = false;

        try
        {
            server?.Dispose();
            server = null;

            var requested = backendCombo.SelectedIndex switch
            {
                1 => CaptureBackendMode.Dxgi,
                2 => CaptureBackendMode.WindowsGraphicsCapture,
                3 => CaptureBackendMode.BitBlt,
                4 => CaptureBackendMode.BitBltCaptureBlt,
                5 => CaptureBackendMode.GdiCopyFromScreen,
                6 => CaptureBackendMode.PrintWindow,
                _ => CaptureBackendMode.Auto
            };

            server = new RemoteServer(requested, Handle);
            server.Start();

            addressLabel.Text = server.Address;
            sessionLabel.Text = server.SessionCode;
            backendLabel.Text = $"Active backend: {server.BackendName}";
            screenLabel.Text =
                $"Screen: READY · {server.CaptureWidth}×{server.CaptureHeight} · target 15 FPS";
            audioLabel.Text = server.AudioReady
                ? "System audio: READY (PCM)"
                : "System audio: UNAVAILABLE";

            diagLabel.Text = string.IsNullOrWhiteSpace(server.Diagnostics)
                ? "Diagnostics: OK"
                : $"Diagnostics: {server.Diagnostics}";
        }
        catch (Exception ex)
        {
            addressLabel.Text = "";
            sessionLabel.Text = "";
            backendLabel.Text = "Active backend: FAILED";
            screenLabel.Text = "Screen: UNAVAILABLE";
            audioLabel.Text = "";
            diagLabel.Text = $"Diagnostics: {ex.Message}";
        }
        finally
        {
            restartButton.Enabled = true;
        }
    }
}
