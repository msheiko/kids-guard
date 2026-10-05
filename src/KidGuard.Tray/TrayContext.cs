using KidGuard.Core;

namespace KidGuard.Tray;

/// <summary>
/// Значок в области уведомлений (раздел 4.1): оставшееся время в подсказке, всплывающие предупреждения
/// и сообщения родителей, кнопка «Попросить ещё времени». Решений не принимает.
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    const string Title = "KidGuard";

    readonly Control _ui;
    readonly NotifyIcon _icon;
    readonly ToolStripMenuItem _statusItem;
    readonly PipeClient _pipe;
    string _status = "подключение…";

    public TrayContext()
    {
        // Невидимый элемент управления — для передачи событий из фонового потока в поток интерфейса.
        _ui = new Control();
        _ui.CreateControl();
        _ = _ui.Handle;

        _statusItem = new ToolStripMenuItem(_status) { Enabled = false };
        var requestItem = new ToolStripMenuItem("Попросить ещё времени…", null, (_, _) => AskForTime());
        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(requestItem);

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Shield,
            Text = Title,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ShowBalloon(Title, _status, ToolTipIcon.Info, 5000);
        };

        _pipe = new PipeClient(
            message => OnUi(() => Handle(message)),
            connected => OnUi(() => SetStatus(connected ? "подключено" : "нет связи со службой KidGuard")));
        _pipe.Start();
    }

    void Handle(TrayMessage message)
    {
        switch (message.Type)
        {
            case TrayMessage.StatusType:
                SetStatus(message.Text);
                break;
            case TrayMessage.WarningType:
                ShowBalloon(Title, message.Text, ToolTipIcon.Warning, 15000);
                break;
            case TrayMessage.MessageType:
                ShowBalloon($"Сообщение от: {message.From ?? "родитель"}", message.Text, ToolTipIcon.Info, 20000);
                break;
            case TrayMessage.TimeRequestResultType:
                ShowBalloon("Запрос времени", message.Text, ToolTipIcon.Info, 10000);
                break;
        }
    }

    void SetStatus(string status)
    {
        _status = status;
        _statusItem.Text = status;
        var tooltip = $"{Title}: {status}";
        // У подсказки значка ограничена длина.
        _icon.Text = tooltip.Length <= 63 ? tooltip : tooltip[..62] + "…";
    }

    void AskForTime()
    {
        using var dialog = new RequestForm();
        if (dialog.ShowDialog() != DialogResult.OK) return;
        if (!_pipe.Send(new TrayRequest(TrayRequest.TimeRequestType, dialog.Comment)))
        {
            ShowBalloon("Запрос времени", "Нет связи со службой KidGuard. Попробуй позже.", ToolTipIcon.Warning, 10000);
        }
    }

    void ShowBalloon(string title, string text, ToolTipIcon icon, int timeoutMs)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        _icon.ShowBalloonTip(timeoutMs, title, text, icon);
    }

    void OnUi(Action action)
    {
        if (_ui.IsDisposed) return;
        try
        {
            _ui.BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
            // Приложение закрывается.
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pipe.Dispose();
            _icon.Visible = false;
            _icon.Dispose();
            _ui.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>Окно запроса времени с необязательным комментарием (раздел 5.9).</summary>
internal sealed class RequestForm : Form
{
    readonly TextBox _comment;

    public RequestForm()
    {
        Text = "Попросить ещё времени";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = true;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(380, 130);

        var label = new Label
        {
            Text = "Зачем нужно время? (необязательно)",
            AutoSize = true,
            Location = new Point(12, 12),
        };
        _comment = new TextBox
        {
            MaxLength = 100,
            Location = new Point(12, 36),
            Width = 356,
        };
        var send = new Button
        {
            Text = "Попросить",
            DialogResult = DialogResult.OK,
            Location = new Point(192, 84),
            Width = 85,
        };
        var cancel = new Button
        {
            Text = "Отмена",
            DialogResult = DialogResult.Cancel,
            Location = new Point(283, 84),
            Width = 85,
        };
        Controls.AddRange([label, _comment, send, cancel]);
        AcceptButton = send;
        CancelButton = cancel;
    }

    public string? Comment => string.IsNullOrWhiteSpace(_comment.Text) ? null : _comment.Text.Trim();
}
