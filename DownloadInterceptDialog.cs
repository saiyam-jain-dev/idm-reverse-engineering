namespace DownloadManager;

internal sealed class DownloadInterceptDialog : Form
{
    public DownloadInterceptDialog(string fileName, string source)
    {
        Text = "Download file";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(440, 190);
        Font = new Font("Segoe UI", 9F);
        BackColor = Color.FromArgb(245, 247, 250);

        var heading = new Label
        {
            AutoSize = false,
            Text = "Download with Independent Download Manager?",
            Font = new Font(Font, FontStyle.Bold),
            Location = new Point(18, 18),
            Size = new Size(404, 25)
        };
        var name = new Label
        {
            AutoEllipsis = true,
            Text = fileName,
            Location = new Point(18, 55),
            Size = new Size(404, 22)
        };
        var sourceLabel = new Label
        {
            AutoEllipsis = true,
            ForeColor = Color.FromArgb(91, 106, 120),
            Text = $"From {source}",
            Location = new Point(18, 80),
            Size = new Size(404, 20)
        };
        var explanation = new Label
        {
            ForeColor = Color.FromArgb(91, 106, 120),
            Text = "Chrome has paused this download. Choose which app should handle it.",
            Location = new Point(18, 108),
            Size = new Size(404, 34)
        };
        var useChrome = new Button
        {
            Text = "Keep in Chrome",
            DialogResult = DialogResult.Cancel,
            Location = new Point(204, 150),
            Size = new Size(112, 28),
            FlatStyle = FlatStyle.System
        };
        var useDesktop = new Button
        {
            Text = "Download here",
            DialogResult = DialogResult.OK,
            Location = new Point(326, 150),
            Size = new Size(96, 28),
            FlatStyle = FlatStyle.System
        };

        Controls.AddRange([heading, name, sourceLabel, explanation, useChrome, useDesktop]);
        AcceptButton = useDesktop;
        CancelButton = useChrome;
    }
}
